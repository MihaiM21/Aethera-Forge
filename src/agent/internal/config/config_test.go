package config

import (
	"crypto/ed25519"
	"crypto/rand"
	"encoding/base64"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestMissingFileGivesSafeDefaults(t *testing.T) {
	c, err := Load(filepath.Join(t.TempDir(), "absent.yaml"))
	if err != nil {
		t.Fatal(err)
	}
	if c.StateDir != "/var/lib/aethera" || c.MaxConcurrentCommands != 8 {
		t.Fatalf("defaults = %+v", c)
	}
	if len(c.Policy.AllowedBindPrefixes) != 1 || c.Policy.AllowedBindPrefixes[0] != "/var/lib/aethera" {
		t.Fatalf("default bind allowlist = %v", c.Policy.AllowedBindPrefixes)
	}
	if strings.Join(c.Policy.AllowedLogDrivers, ",") != "json-file,local,none" {
		t.Fatalf("log drivers = %v", c.Policy.AllowedLogDrivers)
	}
}

func TestParseFullFile(t *testing.T) {
	pub, _, _ := ed25519.GenerateKey(rand.Reader)
	yaml := `
state_dir: /srv/aethera
docker_host: unix:///run/user/1000/docker.sock
trust_system_roots: true
max_concurrent_commands: 3
log_level: debug
release_public_key: ` + base64.StdEncoding.EncodeToString(pub) + `
policy:
  allowed_bind_prefixes: [/srv/aethera, /mnt/data]
  denied_sources: [/mnt/data/private]
  allowed_log_drivers: [local]
  probe_allowlist: [10.0.0.0/8, build.internal]
  forbidden_options: [run_as_root, privileged_ports]
`
	path := filepath.Join(t.TempDir(), "agent.yaml")
	if err := os.WriteFile(path, []byte(yaml), 0o600); err != nil {
		t.Fatal(err)
	}
	c, err := Load(path)
	if err != nil {
		t.Fatal(err)
	}
	if c.StateDir != "/srv/aethera" || !c.TrustSystemRoots || c.MaxConcurrentCommands != 3 || c.Policy.AllowedBindPrefixes[1] != "/mnt/data" {
		t.Fatalf("parsed = %+v", c)
	}
	if k, err := c.ReleaseKey(); err != nil || len(k) != ed25519.PublicKeySize {
		t.Fatalf("release key: %v %v", k, err)
	}
}

func TestStrictParsingFailsClosed(t *testing.T) {
	bad := map[string]string{
		"unknown key":       "state_dirr: /x\n",
		"unknown option":    "policy:\n  forbidden_options: [made_up]\n",
		"relative prefix":   "policy:\n  allowed_bind_prefixes: [data]\n",
		"bad release key":   "release_public_key: not-base64!!\n",
		"short release key": "release_public_key: " + base64.StdEncoding.EncodeToString([]byte("short")) + "\n",
		"not yaml":          "{{{",
	}
	for name, y := range bad {
		if err := Parse([]byte(y), Default()); err == nil {
			t.Errorf("%s: accepted", name)
		}
	}
}

// The shipped example must always parse under the strict loader.
func TestShippedExampleConfigParses(t *testing.T) {
	path := filepath.Join("..", "..", "..", "..", "deploy", "agent", "agent.yaml")
	if _, err := os.Stat(path); err != nil {
		t.Skip("deploy/agent/agent.yaml not found (agent module built outside the repository)")
	}
	c, err := Load(path)
	if err != nil {
		t.Fatal(err)
	}
	if c.StateDir != "/var/lib/aethera" || c.MaxConcurrentCommands != 8 {
		t.Fatalf("example = %+v", c)
	}
}

func TestFinalizeFollowsStateDirOverride(t *testing.T) {
	c := Default()
	c.StateDir = "/data/aeth"
	c.Policy.AllowedBindPrefixes = nil
	c.Finalize()
	if c.Policy.AllowedBindPrefixes[0] != "/data/aeth" {
		t.Fatalf("allowlist = %v", c.Policy.AllowedBindPrefixes)
	}
}
