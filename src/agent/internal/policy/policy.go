// Package policy enforces the agent-local authorization rules from agent.yaml
// (ADR 0002 "Agent-side policy", ADR 0006 "re-check, do not trust"). The control
// plane cannot override it. A refused command is acked REJECTED_POLICY.
package policy

import (
	"context"
	"errors"
	"fmt"
	"net"
	"net/netip"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"strings"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/config"
)

// Violation is a policy refusal.
type Violation struct {
	Code agentv1.ErrorCode
	Msg  string
}

func (v *Violation) Error() string { return v.Msg }

func violation(format string, a ...any) *Violation {
	return &Violation{Code: agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION, Msg: fmt.Sprintf(format, a...)}
}

func invalid(format string, a ...any) *Violation {
	return &Violation{Code: agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, Msg: fmt.Sprintf(format, a...)}
}

// AsViolation extracts a *Violation from err.
func AsViolation(err error) (*Violation, bool) {
	var v *Violation
	if errors.As(err, &v) {
		return v, true
	}
	return nil, false
}

var nameRE = regexp.MustCompile(`^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$`)

// ValidName reports whether s is a legal container/volume/network name.
func ValidName(s string) bool { return nameRE.MatchString(s) }

// Built-in always-denied host paths (ADR 0006).
var builtinDenied = []string{
	"/etc", "/root", "/proc", "/sys", "/dev", "/boot",
	"/var/lib/docker", "/var/lib/containerd",
}

// Docker sockets: never mountable, regardless of any allowlist.
var dockerSockets = []string{"/var/run/docker.sock", "/run/docker.sock", "/var/run", "/run"}

// Policy is the compiled policy.
type Policy struct {
	cfg          config.Policy
	stateDir     string
	allowed      []string
	denied       []string
	hardUnder    []string // equal or beneath is refused
	hardAncestor []string // equal or ancestor is refused

	// resolve returns the symlink-resolved path (injectable for tests).
	resolve func(string) (string, error)
	// lookup resolves host names (injectable for tests).
	lookup func(ctx context.Context, host string) ([]netip.Addr, error)
	// subnets lists Docker network subnets (injectable; may be nil).
	subnets func(ctx context.Context) ([]netip.Prefix, error)
}

// New compiles the policy for cfg.
func New(cfg *config.Config) *Policy {
	p := &Policy{
		cfg:      cfg.Policy,
		stateDir: cfg.StateDir,
		resolve:  resolvePath,
		lookup:   defaultLookup,
	}
	p.hardUnder = append([]string{}, dockerSockets...)
	p.hardAncestor = []string{"/var/run/docker.sock", "/run/docker.sock", cfg.StateDir}
	for _, f := range []string{"agent.key", "agent.crt", "ca.crt", "enrollment.json", "state"} {
		p.hardUnder = append(p.hardUnder, filepath.Join(cfg.StateDir, f))
		p.hardAncestor = append(p.hardAncestor, filepath.Join(cfg.StateDir, f))
	}
	p.denied = append(append([]string{}, builtinDenied...), cfg.Policy.DeniedSources...)
	for _, a := range cfg.Policy.AllowedBindPrefixes {
		p.allowed = append(p.allowed, filepath.Clean(a))
	}
	return p
}

// SetResolver overrides symlink resolution (tests).
func (p *Policy) SetResolver(f func(string) (string, error)) { p.resolve = f }

// SetLookup overrides name resolution (tests).
func (p *Policy) SetLookup(f func(ctx context.Context, host string) ([]netip.Addr, error)) {
	p.lookup = f
}

// SetSubnets installs the Docker network subnet provider used for probes.
func (p *Policy) SetSubnets(f func(ctx context.Context) ([]netip.Prefix, error)) { p.subnets = f }

func defaultLookup(ctx context.Context, host string) ([]netip.Addr, error) {
	ips, err := net.DefaultResolver.LookupNetIP(ctx, "ip", host)
	if err != nil {
		return nil, err
	}
	return ips, nil
}

// resolvePath resolves symlinks of the longest existing prefix and re-appends
// the not-yet-existing remainder.
func resolvePath(path string) (string, error) {
	path = filepath.Clean(path)
	rest := ""
	cur := path
	for {
		r, err := filepath.EvalSymlinks(cur)
		if err == nil {
			if rest == "" {
				return r, nil
			}
			return filepath.Join(r, rest), nil
		}
		if !errors.Is(err, os.ErrNotExist) {
			return "", err
		}
		parent := filepath.Dir(cur)
		if parent == cur {
			return path, nil
		}
		rest = filepath.Join(filepath.Base(cur), rest)
		cur = parent
	}
}

func isUnder(parent, child string) bool {
	if parent == "/" {
		return true
	}
	return child == parent || strings.HasPrefix(child, parent+"/")
}

// CheckBindSource validates a bind-mount host path.
func (p *Policy) CheckBindSource(src string) error {
	if !filepath.IsAbs(src) {
		return violation("bind mount source %q must be an absolute path", src)
	}
	for _, seg := range strings.Split(src, "/") {
		if seg == ".." {
			return violation("bind mount source %q must not contain '..'", src)
		}
	}
	clean := filepath.Clean(src)
	resolved, err := p.resolve(clean)
	if err != nil {
		return violation("bind mount source %q cannot be resolved: %v", src, err)
	}
	for _, c := range []string{clean, resolved} {
		if c == "/" {
			return violation("bind mounting / is not allowed")
		}
		for _, h := range p.hardUnder {
			if isUnder(h, c) {
				return violation("bind mount source %q is always refused (docker socket or agent state)", src)
			}
		}
		for _, h := range p.hardAncestor {
			if isUnder(c, h) {
				return violation("bind mount source %q is always refused (it contains the docker socket or agent state)", src)
			}
		}
	}
	for _, c := range []string{clean, resolved} {
		for _, d := range p.denied {
			d = filepath.Clean(d)
			if isUnder(c, d) && c != d || isUnder(d, c) {
				if !p.allowedBeneath(d, c) {
					return violation("bind mount source %q is under denied path %q", src, d)
				}
			}
		}
	}
	if len(p.allowed) == 0 {
		return violation("bind mounts are disabled on this agent")
	}
	for _, a := range p.allowed {
		ra, _ := p.resolve(a)
		if isUnder(a, resolved) || (ra != "" && isUnder(ra, resolved)) {
			return nil
		}
	}
	return violation("bind mount source %q is not below an allowed prefix (see allowed_bind_prefixes in agent.yaml)", src)
}

// allowedBeneath reports whether an admin-allowed prefix lies strictly below
// the denied directory d and contains c.
func (p *Policy) allowedBeneath(d, c string) bool {
	for _, a := range p.allowed {
		if a != d && isUnder(d, a) && isUnder(a, c) {
			return true
		}
	}
	return false
}

func hasControl(s string) bool {
	for _, r := range s {
		if r < 0x20 || r == 0x7f {
			return true
		}
	}
	return false
}

func (p *Policy) hasOption(o string) bool {
	for _, f := range p.cfg.ForbiddenOptions {
		if f == o {
			return true
		}
	}
	return false
}

// CheckContainerSpec validates a ContainerSpec.
func (p *Policy) CheckContainerSpec(spec *agentv1.ContainerSpec) error {
	if spec == nil {
		return invalid("container spec is missing")
	}
	img := spec.GetImage()
	if img == "" || strings.HasPrefix(img, "-") || strings.ContainsAny(img, " \t\r\n") || hasControl(img) {
		return invalid("invalid image reference")
	}
	if n := spec.GetName(); n != "" && !ValidName(n) {
		return invalid("invalid container name %q", n)
	}
	for _, m := range spec.GetMounts() {
		t := m.GetTarget()
		if !filepath.IsAbs(t) || hasControl(t) {
			return invalid("mount target %q must be an absolute path", t)
		}
		switch m.GetType() {
		case agentv1.MountType_MOUNT_TYPE_BIND:
			if err := p.CheckBindSource(m.GetSource()); err != nil {
				return err
			}
		case agentv1.MountType_MOUNT_TYPE_VOLUME:
			if !ValidName(m.GetSource()) {
				return invalid("invalid volume name %q", m.GetSource())
			}
		case agentv1.MountType_MOUNT_TYPE_TMPFS:
		default:
			return invalid("mount type is required")
		}
	}
	for _, n := range spec.GetNetworks() {
		if err := checkNetworkName(n.GetNetwork()); err != nil {
			return err
		}
	}
	if lc := spec.GetLogConfig(); lc != nil && lc.GetDriver() != "" && !contains(p.cfg.AllowedLogDrivers, lc.GetDriver()) {
		return violation("log driver %q is not allowed on this agent", lc.GetDriver())
	}
	if p.hasOption(config.OptRunAsRoot) {
		switch u := spec.GetUser(); u {
		case "", "root", "0", "0:0", "root:root":
			return violation("containers must not run as root on this agent (forbidden_options: run_as_root)")
		}
	}
	if p.hasOption(config.OptUserNamespace) && spec.GetUser() != "" {
		return violation("setting a container user is forbidden on this agent")
	}
	if p.hasOption(config.OptExtraHosts) && len(spec.GetExtraHosts()) > 0 {
		return violation("extra_hosts is forbidden on this agent")
	}
	for _, pm := range spec.GetPorts() {
		if pm.GetHostPort() == 0 {
			continue
		}
		if p.hasOption(config.OptPublishedPorts) {
			return violation("publishing host ports is forbidden on this agent")
		}
		if p.hasOption(config.OptPrivilegedPorts) && pm.GetHostPort() < 1024 {
			return violation("publishing host port %d (<1024) is forbidden on this agent", pm.GetHostPort())
		}
	}
	return nil
}

func checkNetworkName(n string) error {
	if n == "host" || strings.HasPrefix(n, "container:") {
		return violation("attaching to network %q (host namespace) is not allowed", n)
	}
	if !ValidName(n) {
		return invalid("invalid network name %q", n)
	}
	return nil
}

func contains(list []string, s string) bool {
	for _, x := range list {
		if x == s {
			return true
		}
	}
	return false
}

// CheckCommand validates the policy-relevant parts of a command. It must be
// cheap: it runs before the command is acked.
func (p *Policy) CheckCommand(ctx context.Context, cmd *agentv1.Command) error {
	switch r := cmd.GetRequest().(type) {
	case *agentv1.Command_ContainerCreate:
		return p.CheckContainerSpec(r.ContainerCreate.GetSpec())
	case *agentv1.Command_NetworkCreate:
		if d := r.NetworkCreate.GetDriver(); d == "host" || d == "none" {
			return violation("network driver %q is not allowed", d)
		}
		if !ValidName(r.NetworkCreate.GetName()) {
			return invalid("invalid network name %q", r.NetworkCreate.GetName())
		}
	case *agentv1.Command_NetworkConnect:
		if err := checkNetworkName(r.NetworkConnect.GetNetwork()); err != nil {
			return err
		}
	case *agentv1.Command_VolumeCreate:
		if !ValidName(r.VolumeCreate.GetName()) {
			return invalid("invalid volume name %q", r.VolumeCreate.GetName())
		}
	case *agentv1.Command_SystemPrune:
		sp := r.SystemPrune
		if !(sp.GetStoppedContainers() || sp.GetDanglingImages() || sp.GetUnusedImages() || sp.GetUnusedNetworks() || sp.GetBuildCache() || sp.GetVolumes()) {
			return invalid("system prune needs at least one scope flag")
		}
	case *agentv1.Command_AgentSelfUpdate:
		u, err := url.Parse(r.AgentSelfUpdate.GetUrl())
		if err != nil || u.Scheme != "https" || u.Host == "" {
			return violation("agent self-update requires an https URL")
		}
		if len(r.AgentSelfUpdate.GetSha256()) != 64 {
			return invalid("agent self-update requires a hex SHA-256")
		}
	case *agentv1.Command_HealthProbe:
		return p.CheckProbe(ctx, r.HealthProbe)
	}
	return nil
}

// CheckProbe validates an HTTP/TCP probe target against the SSRF guard.
func (p *Policy) CheckProbe(ctx context.Context, hp *agentv1.HealthProbe) error {
	switch t := hp.GetTarget().(type) {
	case *agentv1.HealthProbe_Http:
		u, err := url.Parse(t.Http.GetUrl())
		if err != nil || (u.Scheme != "http" && u.Scheme != "https") || u.Hostname() == "" {
			return invalid("probe URL must be an absolute http(s) URL")
		}
		if m := t.Http.GetMethod(); m != "" && m != "GET" && m != "HEAD" {
			return invalid("probe method must be GET or HEAD")
		}
		_, err = p.ResolveProbeHost(ctx, u.Hostname())
		return err
	case *agentv1.HealthProbe_Tcp:
		if t.Tcp.GetPort() == 0 || t.Tcp.GetPort() > 65535 {
			return invalid("invalid tcp probe port")
		}
		_, err := p.ResolveProbeHost(ctx, t.Tcp.GetHost())
		return err
	}
	return nil
}

// ResolveProbeHost resolves host and returns the addresses that pass the SSRF
// guard (loopback, Docker network subnets, allowlist). Dial those addresses
// directly so DNS cannot change between check and use.
func (p *Policy) ResolveProbeHost(ctx context.Context, host string) ([]netip.Addr, error) {
	host = strings.Trim(host, "[]")
	if host == "" {
		return nil, invalid("probe host is empty")
	}
	var addrs []netip.Addr
	if a, err := netip.ParseAddr(host); err == nil {
		addrs = []netip.Addr{a.Unmap()}
	} else {
		lctx := ctx
		if lctx == nil {
			lctx = context.Background()
		}
		res, lerr := p.lookup(lctx, host)
		if lerr != nil || len(res) == 0 {
			return nil, invalid("probe host %q cannot be resolved", host)
		}
		for _, a := range res {
			addrs = append(addrs, a.Unmap())
		}
	}
	var subnets []netip.Prefix
	if p.subnets != nil {
		subnets, _ = p.subnets(ctx)
	}
	hostAllowed := false
	for _, entry := range p.cfg.ProbeAllowlist {
		if strings.EqualFold(entry, host) {
			hostAllowed = true
		}
	}
	for _, a := range addrs {
		if a.IsUnspecified() || a.IsLinkLocalUnicast() || a.IsMulticast() {
			return nil, violation("probe target %s is never allowed (link-local, unspecified or multicast)", a)
		}
		if a.IsLoopback() || hostAllowed || p.ipAllowlisted(a) {
			continue
		}
		inDocker := false
		for _, s := range subnets {
			if s.Contains(a) {
				inDocker = true
				break
			}
		}
		if !inDocker {
			return nil, violation("probe target %s is not loopback, a Docker network, or in probe_allowlist", a)
		}
	}
	return addrs, nil
}

func (p *Policy) ipAllowlisted(a netip.Addr) bool {
	for _, entry := range p.cfg.ProbeAllowlist {
		if pf, err := netip.ParsePrefix(entry); err == nil && pf.Contains(a) {
			return true
		}
		if ip, err := netip.ParseAddr(entry); err == nil && ip == a {
			return true
		}
	}
	return false
}
