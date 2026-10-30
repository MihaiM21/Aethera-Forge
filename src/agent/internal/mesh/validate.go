package mesh

import (
	"fmt"
	"net"
	"net/netip"
	"regexp"
	"strconv"
	"strings"
	"time"

	"golang.zx2c4.com/wireguard/wgctrl/wgtypes"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
)

// Limits the agent enforces whatever the control plane sends (ADR 0012, "Security notes").
const (
	// WireGuardInterface is the only WireGuard interface name the agent manages.
	WireGuardInterface = "aethera0"
	// TailscaleInterface is the interface the agent reads the Tailscale address from.
	TailscaleInterface = "tailscale0"

	DefaultMTU = 1420
	MinMTU     = 576
	MaxMTU     = 9000
	// MaxPeers bounds the peer list (a /16 mesh would allow 65k; no installation comes close to this).
	MaxPeers = 512
	// MaxKeepalive bounds the persistent keepalive, in seconds.
	MaxKeepalive = 3600

	minPrefixBits = 16
	maxPrefixBits = 24
)

// Error is a failure with a protocol error code; ops maps it to a dispatch error.
type Error struct {
	Code agentv1.ErrorCode
	Msg  string
}

func (e *Error) Error() string { return e.Msg }

func invalid(format string, a ...any) *Error {
	return &Error{Code: agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, Msg: fmt.Sprintf(format, a...)}
}

func violation(format string, a ...any) *Error {
	return &Error{Code: agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION, Msg: fmt.Sprintf(format, a...)}
}

// Plan is a validated MeshApply.
type Plan struct {
	Name       string
	CIDR       netip.Prefix
	Address    netip.Prefix
	ListenPort int
	MTU        int
	Peers      []PeerPlan
}

// PeerPlan is a validated MeshPeer. The endpoint is still a host:port string: resolving a name is done when applying.
type PeerPlan struct {
	Name       string
	PublicKey  wgtypes.Key
	Endpoint   string
	AllowedIPs []netip.Prefix
	Keepalive  time.Duration
}

var hostRE = regexp.MustCompile(`^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$`)

// ParseApply validates a WireGuard MeshApply. name is the interface the agent manages (WireGuardInterface;
// only tests use another one).
func ParseApply(req *agentv1.MeshApply, name string) (*Plan, error) {
	if req.GetProvider() != agentv1.MeshProvider_MESH_PROVIDER_WIREGUARD {
		return nil, invalid("provider must be WireGuard")
	}
	if got := req.GetInterfaceName(); got != "" && got != name {
		return nil, violation("interface %q is not allowed: the agent manages %q only", sanitize(got), name)
	}

	cidr, err := netip.ParsePrefix(req.GetCidr())
	if err != nil {
		return nil, invalid("cidr %q is not a valid network", sanitize(req.GetCidr()))
	}
	if !cidr.Addr().Is4() {
		return nil, violation("the mesh range must be IPv4")
	}
	if !cidr.Addr().IsPrivate() {
		return nil, violation("the mesh range must be inside a private IPv4 range (10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16)")
	}
	if cidr.Bits() < minPrefixBits || cidr.Bits() > maxPrefixBits {
		return nil, violation("the mesh range prefix must be between /%d and /%d", minPrefixBits, maxPrefixBits)
	}
	if cidr != cidr.Masked() {
		return nil, invalid("cidr %s is not a network address (host bits are set)", cidr)
	}

	addr, err := netip.ParsePrefix(req.GetAddress())
	if err != nil {
		return nil, invalid("address %q is not valid (expected like 10.210.0.3/16)", sanitize(req.GetAddress()))
	}
	if !addr.Addr().Is4() || addr.Bits() != cidr.Bits() || !cidr.Contains(addr.Addr()) {
		return nil, violation("address %s is not inside %s with the same prefix length", addr, cidr)
	}
	if isReserved(cidr, addr.Addr()) {
		return nil, invalid("address %s is the network or broadcast address of %s", addr, cidr)
	}

	port := int(req.GetListenPort())
	if port < 1 || port > 65535 {
		return nil, invalid("listen port must be between 1 and 65535")
	}

	mtu := int(req.GetMtu())
	switch {
	case mtu == 0:
		mtu = DefaultMTU
	case mtu < MinMTU || mtu > MaxMTU:
		return nil, invalid("mtu must be between %d and %d", MinMTU, MaxMTU)
	}

	if len(req.GetPeers()) > MaxPeers {
		return nil, invalid("at most %d peers are accepted", MaxPeers)
	}

	plan := &Plan{Name: name, CIDR: cidr, Address: addr, ListenPort: port, MTU: mtu}
	keys := map[wgtypes.Key]bool{}
	owned := map[netip.Prefix]bool{}
	for i, p := range req.GetPeers() {
		pp, err := parsePeer(i, p, cidr, addr.Addr())
		if err != nil {
			return nil, err
		}
		if keys[pp.PublicKey] {
			return nil, invalid("peer %d: the public key is listed twice", i+1)
		}
		keys[pp.PublicKey] = true
		for _, ip := range pp.AllowedIPs {
			if owned[ip] {
				return nil, invalid("peer %d: allowed IP %s belongs to another peer too", i+1, ip)
			}
			owned[ip] = true
		}
		plan.Peers = append(plan.Peers, *pp)
	}
	return plan, nil
}

func parsePeer(i int, p *agentv1.MeshPeer, cidr netip.Prefix, self netip.Addr) (*PeerPlan, error) {
	label := fmt.Sprintf("peer %d", i+1)
	if n := sanitize(p.GetName()); n != "" {
		label += " (" + n + ")"
	}
	key, err := wgtypes.ParseKey(p.GetPublicKey())
	if err != nil {
		return nil, invalid("%s: the public key is not a base64 32-byte key", label)
	}
	pp := &PeerPlan{Name: sanitize(p.GetName()), PublicKey: key}

	if ep := p.GetEndpoint(); ep != "" {
		if err := checkEndpoint(ep); err != nil {
			return nil, invalid("%s: %v", label, err)
		}
		pp.Endpoint = ep
	}

	if len(p.GetAllowedIps()) == 0 || len(p.GetAllowedIps()) > 4 {
		return nil, invalid("%s: between 1 and 4 allowed IPs are required", label)
	}
	for _, s := range p.GetAllowedIps() {
		ip, err := netip.ParsePrefix(s)
		if err != nil {
			return nil, invalid("%s: allowed IP %q is not valid", label, sanitize(s))
		}
		if !ip.Addr().Is4() || ip.Bits() != 32 {
			return nil, violation("%s: allowed IP %s must be a single IPv4 address (/32)", label, ip)
		}
		if !cidr.Contains(ip.Addr()) {
			return nil, violation("%s: allowed IP %s is outside the mesh range %s", label, ip, cidr)
		}
		if isReserved(cidr, ip.Addr()) || ip.Addr() == self {
			return nil, invalid("%s: allowed IP %s is not a usable peer address", label, ip)
		}
		pp.AllowedIPs = append(pp.AllowedIPs, ip)
	}

	ka := p.GetPersistentKeepaliveSeconds()
	if ka > MaxKeepalive {
		return nil, invalid("%s: persistent keepalive must be at most %d seconds", label, MaxKeepalive)
	}
	pp.Keepalive = time.Duration(ka) * time.Second
	return pp, nil
}

// checkEndpoint accepts "host:port" with a literal IP or a plain host name, and a port from 1 to 65535.
func checkEndpoint(ep string) error {
	host, portStr, err := net.SplitHostPort(ep)
	if err != nil {
		return fmt.Errorf("endpoint %q is not host:port", sanitize(ep))
	}
	port, err := strconv.Atoi(portStr)
	if err != nil || port < 1 || port > 65535 {
		return fmt.Errorf("endpoint port must be between 1 and 65535")
	}
	if ip, err := netip.ParseAddr(host); err == nil {
		if ip.IsUnspecified() || ip.IsMulticast() {
			return fmt.Errorf("endpoint address %s cannot be dialled", ip)
		}
		return nil
	}
	if !hostRE.MatchString(host) {
		return fmt.Errorf("endpoint host %q is not valid", sanitize(host))
	}
	return nil
}

// isReserved reports the network and the broadcast address of an IPv4 range.
func isReserved(cidr netip.Prefix, a netip.Addr) bool {
	if a == cidr.Addr() {
		return true
	}
	return a == broadcast(cidr)
}

func broadcast(cidr netip.Prefix) netip.Addr {
	b := cidr.Addr().As4()
	host := uint32(0xffffffff) >> uint(cidr.Bits())
	b[0] |= byte(host >> 24)
	b[1] |= byte(host >> 16)
	b[2] |= byte(host >> 8)
	b[3] |= byte(host)
	return netip.AddrFrom4(b)
}

// sanitize keeps a value from the control plane printable and short before it appears in a message.
func sanitize(s string) string {
	var b strings.Builder
	for _, r := range s {
		if b.Len() >= 64 {
			break
		}
		switch {
		case r >= 'a' && r <= 'z', r >= 'A' && r <= 'Z', r >= '0' && r <= '9', r == '.', r == '-', r == '_', r == ':', r == '/':
			b.WriteRune(r)
		}
	}
	return b.String()
}
