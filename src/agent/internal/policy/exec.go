package policy

import (
	"context"
	"net/netip"
	"net/url"
	"os"
	"path"
	"path/filepath"
	"regexp"
	"strings"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
)

// Labels and names the exec and backup checks rely on (the control plane sets
// them on every workload container, see ADR 0007 and ADR 0010).
const (
	LabelManaged  = "aethera.managed"
	LabelWorkload = "aethera.workload"
	// ProxyContainer is the proxy container of every server; nothing runs in it.
	ProxyContainer = "aethera-traefik"
	// DefaultHelperImage runs `tar` for volume backups and restores.
	DefaultHelperImage = "busybox:1.37"
	// BackupSubdir is the local backup directory below the state directory.
	BackupSubdir = "backups"
)

var envNameRE = regexp.MustCompile(`^[A-Za-z_][A-Za-z0-9_]*$`)

// SetSelfContainer overrides the identifiers (full or short container ids) the
// agent's own container is known by (tests). Without it they are read from
// the hostname and the cgroup/mount tables of the process.
func (p *Policy) SetSelfContainer(ids ...string) {
	p.selfOnce.Do(func() {})
	p.self = ids
}

func (p *Policy) selfContainerIDs() []string {
	p.selfOnce.Do(func() { p.self = detectSelfContainer() })
	return p.self
}

var hexIDRE = regexp.MustCompile(`[0-9a-f]{64}`)

// detectSelfContainer collects the id this process runs under when it is
// itself in a container: the default hostname is the short id, and cgroup
// paths and mount sources name the full id.
func detectSelfContainer() []string {
	var ids []string
	if h, err := os.Hostname(); err == nil && len(h) >= 12 && len(h) <= 64 && regexp.MustCompile(`^[0-9a-f]+$`).MatchString(h) {
		ids = append(ids, h)
	}
	for _, f := range []string{"/proc/self/cgroup", "/proc/self/mountinfo"} {
		data, err := os.ReadFile(f)
		if err != nil {
			continue
		}
		seen := map[string]bool{}
		for _, m := range hexIDRE.FindAllString(string(data), -1) {
			if !seen[m] {
				seen[m] = true
				ids = append(ids, m)
			}
		}
	}
	return ids
}

// CheckExecTarget decides whether a program may run inside the container
// (ADR 0010): only containers Aethera manages for a workload, never the proxy
// and never the agent itself. The control plane resolves the container from a
// workload of the caller's organization; this is the agent's own check.
func (p *Policy) CheckExecTarget(c *agentv1.ContainerInfo) error {
	name := strings.TrimPrefix(c.GetName(), "/")
	if name == ProxyContainer {
		return violation("running programs in the proxy container is not allowed")
	}
	for _, id := range p.selfContainerIDs() {
		if id != "" && (strings.HasPrefix(c.GetId(), id) || strings.HasPrefix(id, c.GetId()) && c.GetId() != "") {
			return violation("running programs in the agent's own container is not allowed")
		}
	}
	labels := c.GetLabels()
	if labels[LabelManaged] != "true" || labels[LabelWorkload] == "" {
		return violation("container %q is not a workload managed by Aethera (labels %s and %s are required)", name, LabelManaged, LabelWorkload)
	}
	return nil
}

// CheckExecArgs validates the shape of an exec request.
func CheckExecArgs(container string, argv []string, env []*agentv1.EnvVar) error {
	if container == "" || !ValidName(container) {
		return invalid("invalid container reference")
	}
	if len(argv) == 0 || argv[0] == "" {
		return invalid("argv must name a program")
	}
	for _, a := range argv {
		if strings.ContainsRune(a, 0) {
			return invalid("argv must not contain NUL bytes")
		}
	}
	for _, e := range env {
		if !envNameRE.MatchString(e.GetName()) {
			return invalid("invalid environment variable name")
		}
	}
	return nil
}

// CheckBackupVolume validates the volume of a VolumeArchive or VolumeRestore.
// Bind mounts (paths) are not volumes, and the installation's own volumes
// (database, data, ACME store: names starting aethera_ or aethera-) are never
// archived or overwritten.
func CheckBackupVolume(name string) error {
	if name == "" || !ValidName(name) {
		return invalid("invalid volume name (only named Docker volumes can be backed up)")
	}
	l := strings.ToLower(name)
	if strings.HasPrefix(l, "aethera_") || strings.HasPrefix(l, "aethera-") {
		return violation("volume %q belongs to the Aethera installation and cannot be backed up or restored by an agent command", name)
	}
	return nil
}

// CheckHelperImage validates the image of a volume helper container ("" = default).
func CheckHelperImage(img string) error {
	if img == "" {
		return nil
	}
	if strings.HasPrefix(img, "-") || strings.ContainsAny(img, " \t\r\n") || hasControl(img) {
		return invalid("invalid helper image reference")
	}
	return nil
}

// BackupsDir is the local backup directory (<state dir>/backups).
func (p *Policy) BackupsDir() string { return filepath.Join(p.stateDir, BackupSubdir) }

// ResolveLocalPath maps the relative path of a LocalLocation to a file below
// the backup directory. Absolute paths, ".." and anything that would leave the
// directory are refused.
func (p *Policy) ResolveLocalPath(rel string) (string, error) {
	return ResolveBelow(p.BackupsDir(), rel)
}

// ResolveBelow joins rel below dir; rel must be a clean relative path.
func ResolveBelow(dir, rel string) (string, error) {
	if rel == "" || hasControl(rel) || strings.ContainsRune(rel, '\\') {
		return "", invalid("invalid backup path")
	}
	if strings.HasPrefix(rel, "/") || filepath.IsAbs(rel) || filepath.VolumeName(rel) != "" {
		return "", violation("backup path must be relative")
	}
	for _, seg := range strings.Split(rel, "/") {
		if seg == ".." {
			return "", violation("backup path must not contain '..'")
		}
	}
	clean := path.Clean(rel)
	if clean == "." || clean == "" {
		return "", invalid("invalid backup path")
	}
	full := filepath.Join(dir, filepath.FromSlash(clean))
	if r, err := filepath.Rel(dir, full); err != nil || r == "." || strings.HasPrefix(r, "..") {
		return "", violation("backup path escapes the backup directory")
	}
	return full, nil
}

// CheckEndpoint validates the endpoint of an S3Location: https, or http for
// loopback and private addresses only (a local MinIO). lookup resolves host
// names (p.lookup in the agent).
func CheckEndpoint(ctx context.Context, endpoint string, lookup func(ctx context.Context, host string) ([]netip.Addr, error)) error {
	u, err := url.Parse(endpoint)
	if err != nil || u.Host == "" || u.Hostname() == "" {
		return invalid("invalid S3 endpoint")
	}
	if u.User != nil || (u.Path != "" && u.Path != "/") || u.RawQuery != "" || u.Fragment != "" {
		return invalid("the S3 endpoint must be a bare scheme://host[:port]")
	}
	switch u.Scheme {
	case "https":
		return nil
	case "http":
	default:
		return invalid("the S3 endpoint must use https")
	}
	host := strings.Trim(u.Hostname(), "[]")
	if strings.EqualFold(host, "localhost") {
		return nil
	}
	var addrs []netip.Addr
	if a, err := netip.ParseAddr(host); err == nil {
		addrs = []netip.Addr{a.Unmap()}
	} else {
		res, lerr := lookup(ctx, host)
		if lerr != nil || len(res) == 0 {
			return invalid("the S3 endpoint host %q cannot be resolved", host)
		}
		for _, a := range res {
			addrs = append(addrs, a.Unmap())
		}
	}
	for _, a := range addrs {
		if !(a.IsLoopback() || a.IsPrivate()) {
			return violation("http is only accepted for loopback and private S3 endpoints; use https")
		}
	}
	return nil
}

// CheckS3 validates an S3Location.
func (p *Policy) CheckS3(ctx context.Context, s *agentv1.S3Location) error {
	if err := CheckEndpoint(ctx, s.GetEndpoint(), p.lookup); err != nil {
		return err
	}
	if s.GetBucket() == "" || strings.ContainsAny(s.GetBucket(), "/ \t\r\n") || hasControl(s.GetBucket()) {
		return invalid("invalid S3 bucket")
	}
	k := s.GetKey()
	if k == "" || len(k) > 1024 || hasControl(k) || strings.HasPrefix(k, "/") {
		return invalid("invalid S3 object key")
	}
	if s.GetAccessKeyId() == "" || s.GetSecretAccessKey().GetValue() == "" {
		return invalid("S3 credentials are required")
	}
	return nil
}

// CheckBackupLocation validates a backup location of any kind.
func (p *Policy) CheckBackupLocation(ctx context.Context, l *agentv1.BackupLocation) error {
	switch k := l.GetKind().(type) {
	case *agentv1.BackupLocation_S3:
		return p.CheckS3(ctx, k.S3)
	case *agentv1.BackupLocation_Local:
		_, err := p.ResolveLocalPath(k.Local.GetPath())
		return err
	}
	return invalid("a backup location is required")
}

// CheckBackupSource validates a backup source.
func (p *Policy) CheckBackupSource(s *agentv1.BackupSource) error {
	switch k := s.GetKind().(type) {
	case *agentv1.BackupSource_Exec:
		return CheckExecArgs(k.Exec.GetContainer(), k.Exec.GetArgv(), k.Exec.GetEnv())
	case *agentv1.BackupSource_Volume:
		if err := CheckBackupVolume(k.Volume.GetVolume()); err != nil {
			return err
		}
		return CheckHelperImage(k.Volume.GetHelperImage())
	}
	return invalid("a backup source is required")
}

// CheckBackupSink validates a restore sink.
func (p *Policy) CheckBackupSink(s *agentv1.BackupSink) error {
	switch k := s.GetKind().(type) {
	case *agentv1.BackupSink_Exec:
		return CheckExecArgs(k.Exec.GetContainer(), k.Exec.GetArgv(), k.Exec.GetEnv())
	case *agentv1.BackupSink_Volume:
		if err := CheckBackupVolume(k.Volume.GetVolume()); err != nil {
			return err
		}
		return CheckHelperImage(k.Volume.GetHelperImage())
	}
	return invalid("a restore sink is required")
}

// checkExecCommands is the CheckCommand part of the exec and backup arms.
func (p *Policy) checkExecCommands(ctx context.Context, cmd *agentv1.Command) (handled bool, err error) {
	switch r := cmd.GetRequest().(type) {
	case *agentv1.Command_ContainerExec:
		return true, CheckExecArgs(r.ContainerExec.GetContainer(), r.ContainerExec.GetArgv(), r.ContainerExec.GetEnv())
	case *agentv1.Command_BackupRun:
		if err := p.CheckBackupSource(r.BackupRun.GetSource()); err != nil {
			return true, err
		}
		return true, p.CheckBackupLocation(ctx, r.BackupRun.GetTarget())
	case *agentv1.Command_BackupRestore:
		if err := p.CheckBackupSink(r.BackupRestore.GetSink()); err != nil {
			return true, err
		}
		return true, p.CheckBackupLocation(ctx, r.BackupRestore.GetSource())
	case *agentv1.Command_BackupDelete:
		for _, l := range r.BackupDelete.GetLocations() {
			if err := p.CheckBackupLocation(ctx, l); err != nil {
				return true, err
			}
		}
		return true, nil
	}
	return false, nil
}
