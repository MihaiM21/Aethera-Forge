package mesh

import (
	"errors"
	"net"
	"net/netip"
	"time"

	"golang.zx2c4.com/wireguard/wgctrl/wgtypes"
)

// ErrNoLink means the WireGuard interface does not exist.
var ErrNoLink = errors.New("the mesh interface does not exist")

// LinkConfig is everything a WireGuard interface should look like. The private key lives here for the length of
// one call and is never logged or put in an error.
type LinkConfig struct {
	Name       string
	MTU        int
	Address    netip.Prefix
	CIDR       netip.Prefix
	PrivateKey wgtypes.Key
	ListenPort int
	Peers      []PeerConfig
}

// PeerConfig is one peer with a resolved endpoint (nil = wait for the peer to dial in).
type PeerConfig struct {
	PublicKey  wgtypes.Key
	Endpoint   *net.UDPAddr
	AllowedIPs []netip.Prefix
	Keepalive  time.Duration
}

// LinkState is what the kernel reports about the interface.
type LinkState struct {
	Up         bool
	Addresses  []netip.Prefix
	ListenPort int
	Peers      []PeerState
}

// PeerState is the runtime state of one peer.
type PeerState struct {
	PublicKey     wgtypes.Key
	Endpoint      *net.UDPAddr
	LastHandshake time.Time // zero = never
	RxBytes       int64
	TxBytes       int64
}

// Backend is the operating system side of the mesh: netlink and the WireGuard kernel API on Linux, a stub
// elsewhere. It only ever touches the interface it is asked about.
type Backend interface {
	// Probe reports why WireGuard interfaces cannot be created on this host (nil = they can).
	Probe() error
	// Apply creates the interface when missing and brings it to exactly cfg: MTU, address, key, port and peers
	// (peers not listed are removed), then up. It refuses an existing interface that is not a WireGuard one.
	Apply(cfg LinkConfig) error
	// Read reports the interface; ErrNoLink when it does not exist.
	Read(name string) (*LinkState, error)
	// Remove deletes the interface; a missing one is not an error.
	Remove(name string) error
}
