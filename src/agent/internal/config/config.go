// Package config loads the root-owned agent.yaml (ADR 0002 "Agent-side
// policy"). It is local policy: the control plane cannot change it.
package config

import (
	"bytes"
	"crypto/ed25519"
	"encoding/base64"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"

	"gopkg.in/yaml.v3"
)

// DefaultPath is where the packaged service reads its configuration.
const DefaultPath = "/etc/aethera/agent.yaml"

// DefaultStateDir is the agent's data directory.
const DefaultStateDir = "/var/lib/aethera"

// Known values of Policy.ForbiddenOptions.
const (
	OptRunAsRoot       = "run_as_root"       // user empty/root/0
	OptPublishedPorts  = "published_ports"   // any host port mapping
	OptPrivilegedPorts = "privileged_ports"  // host ports below 1024
	OptExtraHosts      = "extra_hosts"       // extra_hosts entries
	OptUserNamespace   = "custom_user_group" // any non-empty user
)

var knownOptions = map[string]bool{
	OptRunAsRoot: true, OptPublishedPorts: true, OptPrivilegedPorts: true,
	OptExtraHosts: true, OptUserNamespace: true,
}

// Policy is the agent-side authorization policy.
type Policy struct {
	// Host path prefixes bind mounts may live under (default: the state dir).
	AllowedBindPrefixes []string `yaml:"allowed_bind_prefixes"`
	// Extra always-denied bind sources, on top of the built-in list.
	DeniedSources []string `yaml:"denied_sources"`
	// Container log drivers that may be requested.
	AllowedLogDrivers []string `yaml:"allowed_log_drivers"`
	// Health probe targets in addition to loopback and Docker networks: host
	// names, IPs or CIDRs.
	ProbeAllowlist []string `yaml:"probe_allowlist"`
	// Container options to refuse, see Opt* constants.
	ForbiddenOptions []string `yaml:"forbidden_options"`
}

// Config is the whole agent.yaml.
type Config struct {
	StateDir              string `yaml:"state_dir"`
	DockerHost            string `yaml:"docker_host"`
	TrustSystemRoots      bool   `yaml:"trust_system_roots"`
	MaxConcurrentCommands int    `yaml:"max_concurrent_commands"`
	LogLevel              string `yaml:"log_level"`
	// Base64 Ed25519 public key; when set, self-updates must carry a signature.
	ReleasePublicKey string `yaml:"release_public_key"`
	Policy           Policy `yaml:"policy"`
}

// Default returns the built-in configuration.
func Default() *Config {
	c := &Config{
		StateDir:              DefaultStateDir,
		MaxConcurrentCommands: 8,
		LogLevel:              "info",
	}
	c.Finalize()
	return c
}

// Finalize fills defaults that depend on other fields. Call it again after
// overriding StateDir.
func (c *Config) Finalize() {
	if c.StateDir == "" {
		c.StateDir = DefaultStateDir
	}
	c.StateDir = filepath.Clean(c.StateDir)
	if c.MaxConcurrentCommands <= 0 {
		c.MaxConcurrentCommands = 8
	}
	if len(c.Policy.AllowedBindPrefixes) == 0 {
		c.Policy.AllowedBindPrefixes = []string{c.StateDir}
	}
	if len(c.Policy.AllowedLogDrivers) == 0 {
		c.Policy.AllowedLogDrivers = []string{"json-file", "local", "none"}
	}
}

// Load reads path. A missing file yields the defaults (the agent must work
// out of the box); a malformed or unknown-key file is an error (fail closed).
func Load(path string) (*Config, error) {
	c := Default()
	data, err := os.ReadFile(path)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return c, nil
		}
		return nil, fmt.Errorf("read %s: %w", path, err)
	}
	if err := Parse(data, c); err != nil {
		return nil, fmt.Errorf("%s: %w", path, err)
	}
	return c, nil
}

// Parse decodes YAML into c (strict: unknown keys are errors) and validates.
func Parse(data []byte, c *Config) error {
	dec := yaml.NewDecoder(bytes.NewReader(data))
	dec.KnownFields(true)
	if err := dec.Decode(c); err != nil && !errors.Is(err, io.EOF) {
		return err
	}
	c.Finalize()
	return c.Validate()
}

// Validate rejects unusable values.
func (c *Config) Validate() error {
	for _, o := range c.Policy.ForbiddenOptions {
		if !knownOptions[o] {
			return fmt.Errorf("policy.forbidden_options: unknown option %q", o)
		}
	}
	for _, p := range append(append([]string{}, c.Policy.AllowedBindPrefixes...), c.Policy.DeniedSources...) {
		if !filepath.IsAbs(p) {
			return fmt.Errorf("policy path %q must be absolute", p)
		}
	}
	if _, err := c.ReleaseKey(); err != nil {
		return err
	}
	return nil
}

// ReleaseKey decodes release_public_key (nil when unset).
func (c *Config) ReleaseKey() (ed25519.PublicKey, error) {
	if c.ReleasePublicKey == "" {
		return nil, nil
	}
	raw, err := base64.StdEncoding.DecodeString(c.ReleasePublicKey)
	if err != nil || len(raw) != ed25519.PublicKeySize {
		return nil, errors.New("release_public_key must be a base64 encoded Ed25519 public key")
	}
	return ed25519.PublicKey(raw), nil
}
