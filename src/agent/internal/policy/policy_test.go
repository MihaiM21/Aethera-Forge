package policy

import (
	"context"
	"errors"
	"net/netip"
	"strings"
	"testing"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/config"
)

func newPolicy(t *testing.T, mutate func(*config.Config)) *Policy {
	t.Helper()
	cfg := config.Default()
	if mutate != nil {
		mutate(cfg)
		cfg.Finalize()
	}
	p := New(cfg)
	p.SetResolver(func(s string) (string, error) { return s, nil })
	return p
}

func TestBindSources(t *testing.T) {
	p := newPolicy(t, nil)
	cases := []struct {
		src  string
		want bool // allowed
	}{
		{"/var/lib/aethera/data/app", true},
		{"/var/lib/aethera/projects/web", true},
		{"/var/lib/aethera", false},           // would expose the key
		{"/var/lib/aethera/agent.key", false}, // agent credentials
		{"/var/lib/aethera/state/idempotency.jsonl", false},
		{"/var/lib/aethera/state", false},
		{"/var/run/docker.sock", false},
		{"/run/docker.sock", false},
		{"/var/run", false},
		{"/run/something", false},
		{"/", false},
		{"/etc/passwd", false},
		{"/root/.ssh", false},
		{"/home/user/data", false}, // not below an allowed prefix
		{"data/relative", false},
		{"/var/lib/aethera/../../etc", false},
		{"/var/lib", false}, // ancestor of the state dir
		{"/var/lib/docker/volumes", false},
	}
	for _, c := range cases {
		err := p.CheckBindSource(c.src)
		if (err == nil) != c.want {
			t.Errorf("CheckBindSource(%q) = %v, allowed want %v", c.src, err, c.want)
		}
		if err != nil {
			if _, ok := AsViolation(err); !ok {
				t.Errorf("%q: error is not a Violation: %T", c.src, err)
			}
		}
	}
}

func TestBindSourceSymlinkEscapeIsResolved(t *testing.T) {
	p := newPolicy(t, nil)
	p.SetResolver(func(s string) (string, error) {
		if s == "/var/lib/aethera/data/sneaky" {
			return "/etc", nil
		}
		return s, nil
	})
	if err := p.CheckBindSource("/var/lib/aethera/data/sneaky"); err == nil {
		t.Fatal("symlink to /etc must be refused after resolution")
	}
}

func TestAdminAllowlistExtendsButNeverOverridesHardDenials(t *testing.T) {
	p := newPolicy(t, func(c *config.Config) {
		c.Policy.AllowedBindPrefixes = []string{"/srv/apps", "/etc/myapp", "/var/run"}
	})
	for src, want := range map[string]bool{
		"/srv/apps/site":       true,
		"/etc/myapp/conf":      true,  // explicit allowlist below a denied directory
		"/etc/other":           false, // still denied
		"/var/run/docker.sock": false, // hard denial wins over any allowlist
		"/var/run/foo":         false,
	} {
		if err := p.CheckBindSource(src); (err == nil) != want {
			t.Errorf("%q: err=%v want allowed=%v", src, err, want)
		}
	}
}

func TestExtraDeniedSources(t *testing.T) {
	p := newPolicy(t, func(c *config.Config) {
		c.Policy.DeniedSources = []string{"/var/lib/aethera/secrets"}
	})
	if err := p.CheckBindSource("/var/lib/aethera/secrets/x"); err == nil {
		t.Fatal("configured denied source accepted")
	}
	if err := p.CheckBindSource("/var/lib/aethera/data"); err != nil {
		t.Fatal(err)
	}
}

func spec() *agentv1.ContainerSpec {
	return &agentv1.ContainerSpec{Image: "nginx:1.27", Name: "web"}
}

func TestContainerSpecChecks(t *testing.T) {
	p := newPolicy(t, func(c *config.Config) {
		c.Policy.ForbiddenOptions = []string{config.OptRunAsRoot, config.OptPrivilegedPorts, config.OptExtraHosts}
	})
	mk := func(f func(*agentv1.ContainerSpec)) *agentv1.ContainerSpec {
		s := spec()
		s.User = "1000"
		f(s)
		return s
	}
	ok := []*agentv1.ContainerSpec{
		mk(func(*agentv1.ContainerSpec) {}),
		mk(func(s *agentv1.ContainerSpec) {
			s.Ports = []*agentv1.PortMapping{{ContainerPort: 80, HostPort: 8080}}
			s.Mounts = []*agentv1.VolumeMount{{Type: agentv1.MountType_MOUNT_TYPE_VOLUME, Source: "pgdata", Target: "/data"}}
			s.LogConfig = &agentv1.LogConfig{Driver: "json-file"}
		}),
	}
	for i, s := range ok {
		if err := p.CheckContainerSpec(s); err != nil {
			t.Errorf("ok[%d]: %v", i, err)
		}
	}
	bad := map[string]*agentv1.ContainerSpec{
		"nil":              nil,
		"no image":         mk(func(s *agentv1.ContainerSpec) { s.Image = "" }),
		"option injection": mk(func(s *agentv1.ContainerSpec) { s.Image = "--privileged" }),
		"space in image":   mk(func(s *agentv1.ContainerSpec) { s.Image = "nginx latest" }),
		"bad name":         mk(func(s *agentv1.ContainerSpec) { s.Name = "../x" }),
		"root user":        mk(func(s *agentv1.ContainerSpec) { s.User = "" }),
		"root user 0":      mk(func(s *agentv1.ContainerSpec) { s.User = "0:0" }),
		"low port":         mk(func(s *agentv1.ContainerSpec) { s.Ports = []*agentv1.PortMapping{{ContainerPort: 80, HostPort: 80}} }),
		"extra hosts":      mk(func(s *agentv1.ContainerSpec) { s.ExtraHosts = []string{"a:1.2.3.4"} }),
		"host network":     mk(func(s *agentv1.ContainerSpec) { s.Networks = []*agentv1.NetworkAttachment{{Network: "host"}} }),
		"container ns":     mk(func(s *agentv1.ContainerSpec) { s.Networks = []*agentv1.NetworkAttachment{{Network: "container:abc"}} }),
		"bad log driver":   mk(func(s *agentv1.ContainerSpec) { s.LogConfig = &agentv1.LogConfig{Driver: "syslog"} }),
		"sock bind": mk(func(s *agentv1.ContainerSpec) {
			s.Mounts = []*agentv1.VolumeMount{{Type: agentv1.MountType_MOUNT_TYPE_BIND, Source: "/var/run/docker.sock", Target: "/s"}}
		}),
		"relative target": mk(func(s *agentv1.ContainerSpec) {
			s.Mounts = []*agentv1.VolumeMount{{Type: agentv1.MountType_MOUNT_TYPE_TMPFS, Target: "tmp"}}
		}),
		"abs volume name": mk(func(s *agentv1.ContainerSpec) {
			s.Mounts = []*agentv1.VolumeMount{{Type: agentv1.MountType_MOUNT_TYPE_VOLUME, Source: "/etc", Target: "/x"}}
		}),
	}
	for name, s := range bad {
		if err := p.CheckContainerSpec(s); err == nil {
			t.Errorf("%s: accepted", name)
		}
	}
}

func TestPublishedPortsForbiddenOption(t *testing.T) {
	p := newPolicy(t, func(c *config.Config) { c.Policy.ForbiddenOptions = []string{config.OptPublishedPorts} })
	s := spec()
	s.Ports = []*agentv1.PortMapping{{ContainerPort: 80, HostPort: 8080}}
	if p.CheckContainerSpec(s) == nil {
		t.Fatal("published port accepted")
	}
	s.Ports = []*agentv1.PortMapping{{ContainerPort: 80}} // internal only
	if err := p.CheckContainerSpec(s); err != nil {
		t.Fatal(err)
	}
}

func TestCommandLevelChecks(t *testing.T) {
	p := newPolicy(t, nil)
	ctx := context.Background()
	cases := map[string]struct {
		cmd  *agentv1.Command
		want bool
	}{
		"empty prune":  {&agentv1.Command{Request: &agentv1.Command_SystemPrune{SystemPrune: &agentv1.SystemPrune{}}}, false},
		"prune":        {&agentv1.Command{Request: &agentv1.Command_SystemPrune{SystemPrune: &agentv1.SystemPrune{DanglingImages: true}}}, true},
		"http update":  {&agentv1.Command{Request: &agentv1.Command_AgentSelfUpdate{AgentSelfUpdate: &agentv1.AgentSelfUpdate{Url: "http://x/y", Sha256: strings.Repeat("a", 64)}}}, false},
		"https update": {&agentv1.Command{Request: &agentv1.Command_AgentSelfUpdate{AgentSelfUpdate: &agentv1.AgentSelfUpdate{Url: "https://x/y", Sha256: strings.Repeat("a", 64)}}}, true},
		"no checksum":  {&agentv1.Command{Request: &agentv1.Command_AgentSelfUpdate{AgentSelfUpdate: &agentv1.AgentSelfUpdate{Url: "https://x/y"}}}, false},
		"host driver":  {&agentv1.Command{Request: &agentv1.Command_NetworkCreate{NetworkCreate: &agentv1.NetworkCreate{Name: "n", Driver: "host"}}}, false},
		"bad volume":   {&agentv1.Command{Request: &agentv1.Command_VolumeCreate{VolumeCreate: &agentv1.VolumeCreate{Name: "a b"}}}, false},
		"connect host": {&agentv1.Command{Request: &agentv1.Command_NetworkConnect{NetworkConnect: &agentv1.NetworkConnect{Network: "host", Container: "c"}}}, false},
	}
	for name, c := range cases {
		if err := p.CheckCommand(ctx, c.cmd); (err == nil) != c.want {
			t.Errorf("%s: err=%v want ok=%v", name, err, c.want)
		}
	}
}

func TestProbeTargets(t *testing.T) {
	p := newPolicy(t, func(c *config.Config) { c.Policy.ProbeAllowlist = []string{"10.9.0.0/16", "build.internal"} })
	p.SetLookup(func(_ context.Context, host string) ([]netip.Addr, error) {
		switch host {
		case "build.internal":
			return []netip.Addr{netip.MustParseAddr("192.168.5.5")}, nil
		case "evil.example":
			return []netip.Addr{netip.MustParseAddr("169.254.169.254")}, nil
		case "public.example":
			return []netip.Addr{netip.MustParseAddr("93.184.216.34")}, nil
		}
		return nil, errors.New("nxdomain")
	})
	p.SetSubnets(func(context.Context) ([]netip.Prefix, error) {
		return []netip.Prefix{netip.MustParsePrefix("172.20.0.0/16")}, nil
	})
	ctx := context.Background()
	cases := map[string]bool{
		"127.0.0.1":       true,
		"localhost-ip6":   false, // unresolvable stub
		"::1":             true,
		"172.20.3.4":      true,  // docker network
		"172.30.3.4":      false, // private but not a docker network
		"10.9.1.1":        true,  // allowlisted CIDR
		"build.internal":  true,  // allowlisted host
		"evil.example":    false, // resolves to link-local metadata
		"public.example":  false,
		"169.254.169.254": false,
		"0.0.0.0":         false,
	}
	for host, want := range cases {
		_, err := p.ResolveProbeHost(ctx, host)
		if (err == nil) != want {
			t.Errorf("%s: err=%v want ok=%v", host, err, want)
		}
	}
	// Metadata stays refused even when allowlisted by name or CIDR.
	p2 := newPolicy(t, func(c *config.Config) { c.Policy.ProbeAllowlist = []string{"169.254.0.0/16"} })
	if _, err := p2.ResolveProbeHost(ctx, "169.254.169.254"); err == nil {
		t.Fatal("link-local must never be probeable")
	}
}

func TestProbeCommandValidation(t *testing.T) {
	p := newPolicy(t, nil)
	ctx := context.Background()
	bad := []*agentv1.HealthProbe{
		{Target: &agentv1.HealthProbe_Http{Http: &agentv1.HttpProbe{Url: "ftp://127.0.0.1/"}}},
		{Target: &agentv1.HealthProbe_Http{Http: &agentv1.HttpProbe{Url: "http://127.0.0.1/", Method: "POST"}}},
		{Target: &agentv1.HealthProbe_Tcp{Tcp: &agentv1.TcpProbe{Host: "127.0.0.1", Port: 0}}},
	}
	for i, hp := range bad {
		if p.CheckProbe(ctx, hp) == nil {
			t.Errorf("bad[%d] accepted", i)
		}
	}
	good := &agentv1.HealthProbe{Target: &agentv1.HealthProbe_Http{Http: &agentv1.HttpProbe{Url: "http://127.0.0.1:8080/health"}}}
	if err := p.CheckProbe(ctx, good); err != nil {
		t.Fatal(err)
	}
}
