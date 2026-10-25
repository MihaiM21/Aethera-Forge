package compose

import (
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
)

// resolved builds the configuration docker compose would print, services only.
func resolved(services map[string]map[string]any) *resolvedConfig {
	return &resolvedConfig{Services: services}
}

func svc(kv ...any) map[string]any {
	m := map[string]any{"image": "x"}
	for i := 0; i+1 < len(kv); i += 2 {
		m[kv[i].(string)] = kv[i+1]
	}
	return m
}

func bind(src string) map[string]any {
	return map[string]any{"type": "bind", "source": src, "target": "/t"}
}

func findingsContain(t *testing.T, name string, findings []string, want string) {
	t.Helper()
	for _, f := range findings {
		if strings.Contains(f, want) {
			return
		}
	}
	t.Errorf("%s: no finding mentions %q; findings: %v", name, want, findings)
}

func TestCheckResolvedRefusesEachForbiddenConstruct(t *testing.T) {
	root := t.TempDir()
	outside := filepath.Dir(root)
	cases := []struct {
		name string
		cfg  *resolvedConfig
		want string
	}{
		{"privileged", resolved(map[string]map[string]any{"a": svc("privileged", true)}), "privileged"},
		{"host network", resolved(map[string]map[string]any{"a": svc("network_mode", "host")}), "host networking"},
		{"network by name", resolved(map[string]map[string]any{"a": svc("network_mode", "aethera-proxy")}), "network_mode"},
		{"host pid", resolved(map[string]map[string]any{"a": svc("pid", "host")}), "pid: host"},
		{"host ipc", resolved(map[string]map[string]any{"a": svc("ipc", "host")}), "ipc: host"},
		{"host uts", resolved(map[string]map[string]any{"a": svc("uts", "host")}), "uts: host"},
		{"host userns", resolved(map[string]map[string]any{"a": svc("userns_mode", "host")}), "userns_mode: host"},
		{"pid of a container outside", resolved(map[string]map[string]any{"a": svc("pid", "container:aethera-agent")}), "outside the project"},
		{"network of a service outside", resolved(map[string]map[string]any{"a": svc("network_mode", "service:ghost")}), "outside the project"},
		{"volumes_from a container", resolved(map[string]map[string]any{"a": svc("volumes_from", []any{"container:aethera-agent:ro"})}), "outside the project"},
		{"devices", resolved(map[string]map[string]any{"a": svc("devices", []any{map[string]any{"source": "/dev/sda"}})}), "devices"},
		{"cap SYS_ADMIN", resolved(map[string]map[string]any{"a": svc("cap_add", []any{"SYS_ADMIN"})}), "SYS_ADMIN"},
		{"cap with prefix", resolved(map[string]map[string]any{"a": svc("cap_add", []any{"cap_sys_module"})}), "SYS_MODULE"},
		{"cap ALL", resolved(map[string]map[string]any{"a": svc("cap_add", []any{"ALL"})}), "ALL"},
		{"unconfined", resolved(map[string]map[string]any{"a": svc("security_opt", []any{"seccomp=unconfined"})}), "unconfined"},
		{"label disable", resolved(map[string]map[string]any{"a": svc("security_opt", []any{"label=disable"})}), "unconfined"},
		{"api socket", resolved(map[string]map[string]any{"a": svc("use_api_socket", true)}), "use_api_socket"},
		{"provider", resolved(map[string]map[string]any{"a": svc("provider", map[string]any{"type": "/bin/x"})}), "provider"},
		{"privileged hook", resolved(map[string]map[string]any{"a": svc("post_start", []any{map[string]any{"privileged": true}})}), "hooks"},
		{"docker socket", resolved(map[string]map[string]any{"a": svc("volumes", []any{bind("/var/run/docker.sock")})}), "socket"},
		{"bind refused by the policy", resolved(map[string]map[string]any{"a": svc("volumes", []any{bind("/etc")})}), "denied /etc"},
		{"bind outside the root", resolved(map[string]map[string]any{"a": svc("volumes", []any{bind(outside)})}), "denied"},
		{"build context outside", resolved(map[string]map[string]any{"a": svc("build", map[string]any{"context": outside})}), "build context"},
		{"dockerfile outside", resolved(map[string]map[string]any{"a": svc("build", map[string]any{"context": root, "dockerfile": filepath.Join(outside, "Dockerfile")})}), "dockerfile"},
		{"dockerfile escaping by dots", resolved(map[string]map[string]any{"a": svc("build", map[string]any{"context": root, "dockerfile": "../Dockerfile"})}), "dockerfile"},
		{"additional context outside", resolved(map[string]map[string]any{"a": svc("build", map[string]any{"context": root, "additional_contexts": map[string]any{"x": outside}})}), "additional build context"},
		{"privileged build", resolved(map[string]map[string]any{"a": svc("build", map[string]any{"context": root, "privileged": true})}), "privileged builds"},
		{"host network build", resolved(map[string]map[string]any{"a": svc("build", map[string]any{"context": root, "network": "host"})}), "host network"},
		{"entitlements", resolved(map[string]map[string]any{"a": svc("build", map[string]any{"context": root, "entitlements": []any{"security.insecure"}})}), "entitlements"},
		{"env file outside", resolved(map[string]map[string]any{"a": svc("env_file", []any{map[string]any{"path": filepath.Join(outside, "secrets.env")}})}), "env_file"},
		{"external network", &resolvedConfig{Services: map[string]map[string]any{"a": svc()}, Networks: map[string]map[string]any{"other": {"external": true, "name": "other"}}}, "external networks"},
		{"external volume", &resolvedConfig{Services: map[string]map[string]any{"a": svc()}, Volumes: map[string]map[string]any{"data": {"external": true, "name": "data"}}}, "external volumes"},
		{"reserved volume", &resolvedConfig{Services: map[string]map[string]any{"a": svc()}, Volumes: map[string]map[string]any{"pg": {"name": "aethera_pgdata"}}}, "reserved"},
		{"reserved volume dash", &resolvedConfig{Services: map[string]map[string]any{"a": svc()}, Volumes: map[string]map[string]any{"pg": {"name": "aethera-pgdata"}}}, "reserved"},
		{"volume as a bind", &resolvedConfig{Services: map[string]map[string]any{"a": svc()}, Volumes: map[string]map[string]any{"d": {"name": "p_d", "driver_opts": map[string]any{"type": "none", "o": "bind", "device": "/"}}}}, "denied /"},
		{"secret file outside", &resolvedConfig{Services: map[string]map[string]any{"a": svc()}, Secrets: map[string]map[string]any{"s": {"file": filepath.Join(outside, "s")}}}, "secret"},
		{"host network driver", &resolvedConfig{Services: map[string]map[string]any{"a": svc()}, Networks: map[string]map[string]any{"h": {"driver": "host"}}}, "driver host"},
	}
	for _, c := range cases {
		got := CheckResolved(c.cfg, ResolvedPolicy{Root: root, Binds: denyBinds{}})
		if len(got) == 0 {
			t.Errorf("%s: accepted", c.name)
			continue
		}
		findingsContain(t, c.name, got, c.want)
	}
}

func TestCheckResolvedListsEveryFinding(t *testing.T) {
	cfg := resolved(map[string]map[string]any{
		"web": svc("privileged", true, "network_mode", "host"),
		"db":  svc("cap_add", []any{"SYS_ADMIN"}),
	})
	got := CheckResolved(cfg, ResolvedPolicy{Root: t.TempDir(), Binds: denyBinds{}})
	if len(got) != 3 {
		t.Fatalf("want three findings (privileged, host network, cap), got %v", got)
	}
	for _, f := range got {
		if !strings.HasPrefix(f, `service "`) {
			t.Errorf("finding without a service: %q", f)
		}
	}
}

func TestCheckResolvedAcceptsOrdinaryProjects(t *testing.T) {
	root := t.TempDir()
	if err := os.MkdirAll(filepath.Join(root, "app"), 0o755); err != nil {
		t.Fatal(err)
	}
	cfg := &resolvedConfig{
		Services: map[string]map[string]any{
			"web": svc("build", map[string]any{"context": filepath.Join(root, "app"), "dockerfile": "Dockerfile"},
				"cap_add", []any{"NET_BIND_SERVICE", "CHOWN"}, "network_mode", "service:db",
				"env_file", []any{map[string]any{"path": filepath.Join(root, "a.env")}},
				"volumes", []any{bind(filepath.Join(root, "conf", "nginx.conf")), map[string]any{"type": "volume", "source": "data", "target": "/d"}}),
			"db":  svc("container_name", "shop-db"),
			"job": svc("pid", "container:shop-db", "volumes_from", []any{"db:ro"}),
		},
		Volumes:  map[string]map[string]any{"data": {"name": "shop_data"}},
		Networks: map[string]map[string]any{"default": {"name": "shop_default"}},
		Secrets:  map[string]map[string]any{"s": {"file": filepath.Join(root, "secret.txt")}},
	}
	if got := CheckResolved(cfg, ResolvedPolicy{Root: root, Binds: denyBinds{}}); len(got) != 0 {
		t.Fatalf("findings for an ordinary project: %v", got)
	}
}

func TestCheckResolvedExternalNetworksOnlyThoseOfTheOverride(t *testing.T) {
	override := "services:\n  web:\n    networks: [default, aethera-proxy, env-net]\nnetworks:\n  aethera-proxy:\n    external: true\n    name: aethera-proxy\n  env-net:\n    external: true\n    name: aethera-env-1234\n"
	allowed := overrideNetworks(override)
	if len(allowed) != 2 || allowed["aethera-proxy"] != "aethera-proxy" || allowed["env-net"] != "aethera-env-1234" {
		t.Fatalf("override networks = %v", allowed)
	}
	root := t.TempDir()
	pol := ResolvedPolicy{Root: root, Binds: denyBinds{}, ExternalNetworks: allowed}
	ok := &resolvedConfig{Services: map[string]map[string]any{"web": svc()}, Networks: map[string]map[string]any{
		"aethera-proxy": {"external": true, "name": "aethera-proxy"},
		"env-net":       {"external": true, "name": "aethera-env-1234"},
		"default":       {"name": "p_default"},
	}}
	if got := CheckResolved(ok, pol); len(got) != 0 {
		t.Fatalf("the override's own networks were refused: %v", got)
	}
	// The user's file cannot re-point the key at a different docker network, nor add another external one.
	hijack := &resolvedConfig{Services: map[string]map[string]any{"web": svc()}, Networks: map[string]map[string]any{
		"aethera-proxy": {"external": true, "name": "some-other-network"},
		"agent-net":     {"external": true, "name": "aethera-agent"},
	}}
	got := CheckResolved(hijack, pol)
	if len(got) != 2 {
		t.Fatalf("findings = %v", got)
	}
}

func TestCheckResolvedFollowsSymlinks(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("symlinks need privileges on Windows")
	}
	root, outside := t.TempDir(), t.TempDir()
	if err := os.Symlink(outside, filepath.Join(root, "link")); err != nil {
		t.Fatal(err)
	}
	cfg := resolved(map[string]map[string]any{
		"a": svc("volumes", []any{bind(filepath.Join(root, "link", "data"))}, "build", map[string]any{"context": filepath.Join(root, "link")}),
	})
	got := CheckResolved(cfg, ResolvedPolicy{Root: root, Binds: denyBinds{}})
	findingsContain(t, "bind through a symlink", got, "denied")
	findingsContain(t, "context through a symlink", got, "build context")
}

func TestRawValidateAlsoCoversIncludeAndExtends(t *testing.T) {
	for name, doc := range map[string]string{
		"include":      "include:\n  - ../other/compose.yaml\nservices: {}\n",
		"include path": "include:\n  - path: /etc/compose.yaml\nservices: {}\n",
		"extends":      "services:\n  a:\n    extends:\n      file: ../../x.yaml\n      service: y\n",
		"label off":    "services:\n  a:\n    image: x\n    security_opt: [\"label:disable\"]\n",
	} {
		if err := Validate(doc, denyBinds{}); err == nil {
			t.Errorf("%s: accepted", name)
		}
	}
	if err := Validate("include:\n  - ./sub/compose.yaml\nservices: {}\n", denyBinds{}); err != nil {
		t.Errorf("a project-local include was refused: %v", err)
	}
	err := Validate("services:\n  a:\n    privileged: true\n  b:\n    network_mode: host\n", denyBinds{})
	if err == nil || !strings.Contains(err.Error(), `service "a"`) || !strings.Contains(err.Error(), `service "b"`) {
		t.Errorf("Validate must list every finding, got %v", err)
	}
}
