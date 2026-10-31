//go:build linux

package mesh

import (
	"errors"
	"fmt"
	"net"
	"net/netip"

	"github.com/vishvananda/netlink"
	"golang.zx2c4.com/wireguard/wgctrl"
	"golang.zx2c4.com/wireguard/wgctrl/wgtypes"
)

// probeLink is the throw-away interface Probe creates and deletes at once.
const probeLink = "aethera-probe0"

// linuxBackend drives the kernel WireGuard module through netlink (link, address) and wgctrl (keys, peers).
type linuxBackend struct {
	// skipOverlapCheck is for tests that put two mesh interfaces in one network namespace.
	skipOverlapCheck bool
}

// NewBackend returns the Linux backend.
func NewBackend() Backend { return &linuxBackend{} }

func (b *linuxBackend) Probe() error {
	// A leftover from a crash would make the add fail.
	if l, err := netlink.LinkByName(probeLink); err == nil && l.Type() == "wireguard" {
		_ = netlink.LinkDel(l)
	}
	l := &netlink.Wireguard{LinkAttrs: netlink.NewLinkAttrs()}
	l.Name = probeLink
	if err := netlink.LinkAdd(l); err != nil {
		return fmt.Errorf("cannot create a WireGuard interface (the kernel module and CAP_NET_ADMIN are needed): %w", err)
	}
	if err := netlink.LinkDel(l); err != nil {
		return fmt.Errorf("cannot remove the probe interface %s: %w", probeLink, err)
	}
	return nil
}

func (b *linuxBackend) Apply(cfg LinkConfig) error {
	link, err := netlink.LinkByName(cfg.Name)
	var notFound netlink.LinkNotFoundError
	switch {
	case errors.As(err, &notFound):
		if !b.skipOverlapCheck {
			if err := checkOverlap(cfg); err != nil {
				return err
			}
		}
		l := &netlink.Wireguard{LinkAttrs: netlink.NewLinkAttrs()}
		l.Name = cfg.Name
		if err := netlink.LinkAdd(l); err != nil {
			return fmt.Errorf("create %s: %w", cfg.Name, err)
		}
		if link, err = netlink.LinkByName(cfg.Name); err != nil {
			return fmt.Errorf("find %s after creating it: %w", cfg.Name, err)
		}
	case err != nil:
		return fmt.Errorf("look up %s: %w", cfg.Name, err)
	default:
		// Never reconfigure an interface that is not ours, whatever it is called.
		if link.Type() != "wireguard" {
			return fmt.Errorf("an interface called %s exists and is not a WireGuard interface; refusing to touch it", cfg.Name)
		}
		if !b.skipOverlapCheck {
			if err := checkOverlap(cfg); err != nil {
				return err
			}
		}
	}

	if err := netlink.LinkSetMTU(link, cfg.MTU); err != nil {
		return fmt.Errorf("set the MTU of %s: %w", cfg.Name, err)
	}
	if err := setAddress(link, cfg.Address); err != nil {
		return err
	}
	if err := configureDevice(cfg); err != nil {
		return err
	}
	if err := netlink.LinkSetUp(link); err != nil {
		return fmt.Errorf("bring %s up: %w", cfg.Name, err)
	}
	return nil
}

// setAddress makes the interface carry exactly this IPv4 address. The kernel adds the connected route of the
// mesh range with it; no other route is created.
func setAddress(link netlink.Link, want netip.Prefix) error {
	addrs, err := netlink.AddrList(link, netlink.FAMILY_V4)
	if err != nil {
		return fmt.Errorf("list the addresses of %s: %w", link.Attrs().Name, err)
	}
	for i := range addrs {
		if p, ok := toPrefix(addrs[i].IPNet); ok && p == want {
			continue
		}
		if err := netlink.AddrDel(link, &addrs[i]); err != nil {
			return fmt.Errorf("remove a stale address from %s: %w", link.Attrs().Name, err)
		}
	}
	ip := want.Addr().As4()
	a := &netlink.Addr{IPNet: &net.IPNet{IP: net.IP(ip[:]), Mask: net.CIDRMask(want.Bits(), 32)}}
	if err := netlink.AddrReplace(link, a); err != nil {
		return fmt.Errorf("set the address of %s: %w", link.Attrs().Name, err)
	}
	return nil
}

func configureDevice(cfg LinkConfig) error {
	c, err := wgctrl.New()
	if err != nil {
		return fmt.Errorf("open the WireGuard control socket: %w", err)
	}
	defer c.Close()

	key, port := cfg.PrivateKey, cfg.ListenPort
	wc := wgtypes.Config{PrivateKey: &key, ListenPort: &port, ReplacePeers: true}
	for _, p := range cfg.Peers {
		pc := wgtypes.PeerConfig{PublicKey: p.PublicKey, Endpoint: p.Endpoint, ReplaceAllowedIPs: true}
		if p.Keepalive > 0 {
			ka := p.Keepalive
			pc.PersistentKeepaliveInterval = &ka
		}
		for _, ip := range p.AllowedIPs {
			a := ip.Addr().As4()
			pc.AllowedIPs = append(pc.AllowedIPs, net.IPNet{IP: net.IP(a[:]), Mask: net.CIDRMask(ip.Bits(), 32)})
		}
		wc.Peers = append(wc.Peers, pc)
	}
	if err := c.ConfigureDevice(cfg.Name, wc); err != nil {
		// wgctrl errors carry the interface name and the syscall error, never key material.
		return fmt.Errorf("configure the WireGuard device %s: %w", cfg.Name, err)
	}
	return nil
}

func (b *linuxBackend) Read(name string) (*LinkState, error) {
	link, err := netlink.LinkByName(name)
	var notFound netlink.LinkNotFoundError
	if errors.As(err, &notFound) {
		return nil, ErrNoLink
	}
	if err != nil {
		return nil, err
	}
	if link.Type() != "wireguard" {
		return nil, fmt.Errorf("%s is not a WireGuard interface", name)
	}
	st := &LinkState{Up: link.Attrs().Flags&net.FlagUp != 0}
	addrs, err := netlink.AddrList(link, netlink.FAMILY_V4)
	if err != nil {
		return nil, err
	}
	for _, a := range addrs {
		if p, ok := toPrefix(a.IPNet); ok {
			st.Addresses = append(st.Addresses, p)
		}
	}

	c, err := wgctrl.New()
	if err != nil {
		return nil, fmt.Errorf("open the WireGuard control socket: %w", err)
	}
	defer c.Close()
	dev, err := c.Device(name)
	if err != nil {
		return nil, fmt.Errorf("read the WireGuard device %s: %w", name, err)
	}
	st.ListenPort = dev.ListenPort
	for _, p := range dev.Peers {
		st.Peers = append(st.Peers, PeerState{
			PublicKey: p.PublicKey, Endpoint: p.Endpoint, LastHandshake: p.LastHandshakeTime,
			RxBytes: p.ReceiveBytes, TxBytes: p.TransmitBytes,
		})
	}
	return st, nil
}

func (b *linuxBackend) Remove(name string) error {
	link, err := netlink.LinkByName(name)
	var notFound netlink.LinkNotFoundError
	if errors.As(err, &notFound) {
		return nil
	}
	if err != nil {
		return err
	}
	if link.Type() != "wireguard" {
		return fmt.Errorf("%s is not a WireGuard interface; refusing to delete it", name)
	}
	return netlink.LinkDel(link)
}

// checkOverlap refuses a mesh range that collides with an address another interface already carries: two
// networks sharing a range cannot both be routed.
func checkOverlap(cfg LinkConfig) error {
	links, err := netlink.LinkList()
	if err != nil {
		return fmt.Errorf("list the interfaces: %w", err)
	}
	for _, l := range links {
		if l.Attrs().Name == cfg.Name {
			continue
		}
		addrs, err := netlink.AddrList(l, netlink.FAMILY_V4)
		if err != nil {
			continue
		}
		for _, a := range addrs {
			if p, ok := toPrefix(a.IPNet); ok && p.Masked().Overlaps(cfg.CIDR) {
				return fmt.Errorf("the mesh range %s overlaps %s on the interface %s; choose another range in Settings > Network",
					cfg.CIDR, p, l.Attrs().Name)
			}
		}
	}
	return nil
}

func toPrefix(n *net.IPNet) (netip.Prefix, bool) {
	if n == nil {
		return netip.Prefix{}, false
	}
	ip, ok := netip.AddrFromSlice(n.IP)
	if !ok {
		return netip.Prefix{}, false
	}
	ones, _ := n.Mask.Size()
	return netip.PrefixFrom(ip.Unmap(), ones), true
}
