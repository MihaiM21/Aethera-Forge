package compose

import (
	"encoding/json"
	"fmt"
	"path/filepath"
	"sort"
	"strconv"
	"strings"

	"gopkg.in/yaml.v3"
)

// dangerousCaps are capabilities that give a container control over the host kernel, its clock, its devices or other
// containers' memory. NET_ADMIN is on the list because it also lets a container rewrite the host's firewall when it shares
// the host network (and Aethera never lets it, but the capability alone is refused as well).
var dangerousCaps = map[string]bool{
	"ALL": true, "SYS_ADMIN": true, "SYS_MODULE": true, "SYS_PTRACE": true, "NET_ADMIN": true, "SYS_RAWIO": true,
	"SYS_BOOT": true, "SYS_TIME": true, "DAC_READ_SEARCH": true, "MAC_ADMIN": true, "MAC_OVERRIDE": true, "BPF": true,
	"PERFMON": true, "SYSLOG": true,
}

func dangerousCap(c string) (string, bool) {
	cs := strings.TrimPrefix(strings.ToUpper(strings.TrimSpace(c)), "CAP_")
	return cs, dangerousCaps[cs]
}

// Validate refuses compose content that asks for host-level power: privileged containers, host namespaces, devices,
// dangerous capabilities and bind mounts the container policy would refuse (the docker socket among them). It looks at the
// text of one file; the configuration docker compose resolves from the files together (includes, extends, merged overrides,
// interpolation) is vetted by CheckResolved. Every finding is listed in the error.
func Validate(content string, binds BindChecker) error {
	if f, _ := rawFindings(content, binds); len(f) > 0 {
		return fmt.Errorf("%s", strings.Join(f, "; "))
	}
	return nil
}

// rawFindings is Validate's list of findings; parsed is false when the text is not YAML (docker compose could not read it
// either).
func rawFindings(content string, binds BindChecker) (findings []string, parsed bool) {
	var doc struct {
		Include  any                       `yaml:"include"`
		Services map[string]map[string]any `yaml:"services"`
		Secrets  map[string]map[string]any `yaml:"secrets"`
		Configs  map[string]map[string]any `yaml:"configs"`
	}
	if err := yaml.Unmarshal([]byte(content), &doc); err != nil {
		return []string{fmt.Sprintf("invalid compose file: %v", err)}, false
	}
	var out []string
	add := func(format string, a ...any) { out = append(out, fmt.Sprintf(format, a...)) }

	for _, f := range includePaths(doc.Include) {
		if err := projectLocal(f); err != nil {
			add("include: %v", err)
		}
	}
	for _, grp := range []struct {
		kind string
		defs map[string]map[string]any
	}{{"secret", doc.Secrets}, {"config", doc.Configs}} {
		for _, name := range sortedKeys(grp.defs) {
			if f, ok := grp.defs[name]["file"].(string); ok {
				if err := projectLocal(f); err != nil {
					add("%s %q: file %v", grp.kind, name, err)
				}
			}
		}
	}
	for _, name := range sortedKeys(doc.Services) {
		svc := doc.Services[name]
		fail := func(format string, a ...any) { add("service %q: "+format, append([]any{name}, a...)...) }
		if v, _ := svc["privileged"].(bool); v {
			fail("privileged containers are not allowed")
		}
		for _, k := range []string{"network_mode", "pid", "ipc", "userns_mode", "uts", "cgroup"} {
			if s, _ := svc[k].(string); s == "host" || strings.HasPrefix(s, "container:") && k != "network_mode" {
				fail("%s: %s is not allowed", k, s)
			}
		}
		if s, _ := svc["network_mode"].(string); s == "host" {
			fail("host networking is not allowed")
		}
		if d, ok := svc["devices"].([]any); ok && len(d) > 0 {
			fail("host devices are not allowed")
		}
		if caps, ok := svc["cap_add"].([]any); ok {
			for _, c := range caps {
				if cs, bad := dangerousCap(fmt.Sprint(c)); bad {
					fail("capability %s is not allowed", cs)
				}
			}
		}
		if opts, ok := svc["security_opt"].([]any); ok {
			for _, o := range opts {
				if unconfinedOption(fmt.Sprint(o)) {
					fail("unconfined security options are not allowed")
				}
			}
		}
		if ext, ok := svc["extends"].(map[string]any); ok {
			if f, ok := ext["file"].(string); ok {
				if err := projectLocal(f); err != nil {
					fail("extends file %v", err)
				}
			}
		}
		if err := buildContextLocal(svc["build"]); err != nil {
			fail("build context %v", err)
		}
		for _, f := range envFiles(svc["env_file"]) {
			if err := projectLocal(f); err != nil {
				fail("env_file %v", err)
			}
		}
		vols, _ := svc["volumes"].([]any)
		for _, v := range vols {
			src := bindSource(v)
			if src == "" {
				continue
			}
			if strings.HasPrefix(src, "./") || src == "." {
				continue // inside the managed project directory
			}
			if strings.Contains(src, "..") {
				fail("bind mount %q must not contain '..'", src)
				continue
			}
			if strings.HasPrefix(src, "/") || strings.HasPrefix(src, "~") {
				if binds == nil {
					fail("bind mounts are disabled")
				} else if err := binds.CheckBindSource(src); err != nil {
					fail("%v", err)
				}
			}
		}
	}
	return out, true
}

func unconfinedOption(o string) bool {
	o = strings.ToLower(strings.ReplaceAll(o, " ", ""))
	return strings.Contains(o, "unconfined") || o == "label=disable" || o == "label:disable"
}

func sortedKeys[V any](m map[string]V) []string {
	keys := make([]string, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	return keys
}

// includePaths flattens the spellings of the top-level include: a list of paths or of {path: ...} (path itself a string or
// a list).
func includePaths(v any) []string {
	list, _ := v.([]any)
	var out []string
	for _, e := range list {
		switch x := e.(type) {
		case string:
			out = append(out, x)
		case map[string]any:
			switch p := x["path"].(type) {
			case string:
				out = append(out, p)
			case []any:
				for _, q := range p {
					if s, ok := q.(string); ok {
						out = append(out, s)
					}
				}
			}
			if s, ok := x["project_directory"].(string); ok {
				out = append(out, s)
			}
		}
	}
	return out
}

// bindSource extracts the host path of a volume entry, or "" for named volumes.
func bindSource(v any) string {
	switch t := v.(type) {
	case string:
		parts := strings.Split(t, ":")
		if len(parts) < 2 {
			return ""
		}
		src := parts[0]
		if strings.HasPrefix(src, "/") || strings.HasPrefix(src, ".") || strings.HasPrefix(src, "~") {
			return src
		}
	case map[string]any:
		if t["type"] == "bind" {
			s, _ := t["source"].(string)
			return s
		}
	}
	return ""
}

// projectLocal accepts only paths inside the project directory: absolute paths and ".." would read host files (/etc, other
// projects) into an image, an environment or a secret. URLs are left to docker.
func projectLocal(p string) error {
	if strings.Contains(p, "://") {
		return nil
	}
	if strings.HasPrefix(p, "/") || strings.HasPrefix(p, "~") || strings.HasPrefix(p, `\`) || (len(p) > 1 && p[1] == ':') {
		return fmt.Errorf("%q must be relative to the project directory", p)
	}
	for _, seg := range strings.FieldsFunc(p, func(r rune) bool { return r == '/' || r == '\\' }) {
		if seg == ".." {
			return fmt.Errorf("%q must not contain '..'", p)
		}
	}
	return nil
}

func buildContextLocal(v any) error {
	switch t := v.(type) {
	case string:
		return projectLocal(t)
	case map[string]any:
		if c, ok := t["context"].(string); ok {
			if err := projectLocal(c); err != nil {
				return err
			}
		}
		if df, ok := t["dockerfile"].(string); ok {
			return projectLocal(df)
		}
	}
	return nil
}

// envFiles flattens the three spellings of env_file: a string, a list of strings, a list of {path: ...}.
func envFiles(v any) []string {
	switch t := v.(type) {
	case string:
		return []string{t}
	case []any:
		var out []string
		for _, e := range t {
			switch x := e.(type) {
			case string:
				out = append(out, x)
			case map[string]any:
				if p, ok := x["path"].(string); ok {
					out = append(out, p)
				}
			}
		}
		return out
	case map[string]any:
		if p, ok := t["path"].(string); ok {
			return []string{p}
		}
	}
	return nil
}

// ---- the resolved configuration ------------------------------------------------------------------------------------

// ResolvedPolicy is what CheckResolved needs besides the configuration.
type ResolvedPolicy struct {
	// Root is the project directory (inline compose files) or the repository checkout. Every local path of the
	// configuration (build contexts, env and secret files, bind mounts below it) must stay inside it, symlinks followed.
	Root  string
	Binds BindChecker
	// ExternalNetworks are the networks the Aethera override file declares as external (compose key -> docker name): the
	// proxy network and the environment network. No other external network or volume is allowed.
	ExternalNetworks map[string]string
}

// resolvedConfig is the part of `docker compose config --format json` the policy reads.
type resolvedConfig struct {
	Services map[string]map[string]any `json:"services"`
	Networks map[string]map[string]any `json:"networks"`
	Volumes  map[string]map[string]any `json:"volumes"`
	Secrets  map[string]map[string]any `json:"secrets"`
	Configs  map[string]map[string]any `json:"configs"`
}

func parseResolved(b []byte) (*resolvedConfig, error) {
	var c resolvedConfig
	if err := json.Unmarshal(b, &c); err != nil {
		return nil, fmt.Errorf("unreadable output of docker compose config: %w", err)
	}
	return &c, nil
}

// CheckResolved vets the configuration docker compose resolved from the project's files: the findings (empty = allowed)
// name the service and the construct, never a value.
func CheckResolved(c *resolvedConfig, pol ResolvedPolicy) []string {
	var out []string
	add := func(format string, a ...any) { out = append(out, fmt.Sprintf(format, a...)) }
	root := realPath(pol.Root)

	// Local paths: inside the root after symlinks, or (for binds) accepted by the agent's bind policy.
	local := func(what, p string) {
		// Compose makes every local path absolute; what is left (URLs, git contexts, docker-image://, service:) is docker's.
		if p == "" || strings.Contains(p, "://") || !(filepath.IsAbs(p) || strings.HasPrefix(p, "/")) {
			return
		}
		if !within(root, realPath(p)) {
			add("%s %q is outside the project directory", what, displayPath(pol.Root, p))
		}
	}

	services := make(map[string]bool, len(c.Services))
	containers := map[string]bool{}
	for name, svc := range c.Services {
		services[name] = true
		if cn, _ := svc["container_name"].(string); cn != "" {
			containers[cn] = true
		}
	}
	// A reference to another container or service is fine only when it stays inside the project.
	ref := func(svc, key, v string) {
		switch {
		case strings.HasPrefix(v, "service:"):
			if !services[strings.TrimPrefix(v, "service:")] {
				add("service %q: %s: %s refers to a service outside the project", svc, key, v)
			}
		case strings.HasPrefix(v, "container:"):
			if !containers[strings.TrimPrefix(v, "container:")] {
				add("service %q: %s: %s refers to a container outside the project", svc, key, v)
			}
		}
	}

	for _, name := range sortedKeys(c.Services) {
		svc := c.Services[name]
		fail := func(format string, a ...any) { add("service %q: "+format, append([]any{name}, a...)...) }
		if truthy(svc["privileged"]) {
			fail("privileged containers are not allowed")
		}
		if truthy(svc["use_api_socket"]) {
			fail("use_api_socket would hand the docker socket to the container")
		}
		if _, ok := svc["provider"]; ok {
			fail("provider services run host programs and are not allowed")
		}
		if s, _ := svc["network_mode"].(string); s != "" {
			switch s {
			case "host":
				fail("host networking is not allowed")
			case "bridge", "none", "default":
			default:
				if strings.HasPrefix(s, "service:") || strings.HasPrefix(s, "container:") {
					ref(name, "network_mode", s)
				} else {
					fail("network_mode %q is not allowed (use networks)", s)
				}
			}
		}
		for _, k := range []string{"pid", "ipc", "uts", "userns_mode", "cgroup"} {
			if s, _ := svc[k].(string); s != "" {
				if s == "host" {
					fail("%s: host is not allowed", k)
				}
				ref(name, k, s)
			}
		}
		if d, ok := svc["devices"].([]any); ok && len(d) > 0 {
			fail("host devices are not allowed")
		}
		if d, ok := svc["device_cgroup_rules"].([]any); ok && len(d) > 0 {
			fail("device cgroup rules are not allowed")
		}
		if caps, ok := svc["cap_add"].([]any); ok {
			for _, cp := range caps {
				if cs, bad := dangerousCap(fmt.Sprint(cp)); bad {
					fail("capability %s is not allowed", cs)
				}
			}
		}
		if opts, ok := svc["security_opt"].([]any); ok {
			for _, o := range opts {
				if unconfinedOption(fmt.Sprint(o)) {
					fail("unconfined security options are not allowed")
				}
			}
		}
		if vf, ok := svc["volumes_from"].([]any); ok {
			for _, e := range vf {
				s := fmt.Sprint(e)
				if strings.HasPrefix(s, "container:") {
					ref(name, "volumes_from", strings.Join(strings.SplitN(s, ":", 3)[:2], ":"))
				} else {
					ref(name, "volumes_from", "service:"+strings.SplitN(s, ":", 2)[0])
				}
			}
		}
		for _, hook := range []string{"post_start", "pre_stop"} {
			if hs, ok := svc[hook].([]any); ok {
				for _, h := range hs {
					if m, ok := h.(map[string]any); ok && truthy(m["privileged"]) {
						fail("%s hooks must not be privileged", hook)
					}
				}
			}
		}
		if b := svc["build"]; b != nil {
			checkBuild(name, b, local, add)
		}
		for _, f := range envFiles(svc["env_file"]) {
			local(fmt.Sprintf("service %q: env_file", name), f)
		}
		if ports, ok := svc["ports"].([]any); ok {
			for _, e := range ports {
				if hp := publishedPort(e); hp == 80 || hp == 443 {
					fail("host port %d belongs to the reverse proxy: remove the port mapping and give the service a domain instead", hp)
				}
			}
		}
		vols, _ := svc["volumes"].([]any)
		for _, v := range vols {
			m, ok := v.(map[string]any)
			if !ok || m["type"] != "bind" {
				continue
			}
			src, _ := m["source"].(string)
			if msg := checkBind(src, root, pol.Binds); msg != "" {
				fail("%s", msg)
			}
		}
	}

	for _, name := range sortedKeys(c.Networks) {
		n := c.Networks[name]
		dockerName, _ := n["name"].(string)
		if dockerName == "" {
			dockerName = name
		}
		if truthy(n["external"]) {
			if want, ok := pol.ExternalNetworks[name]; !ok || want != dockerName {
				add("network %q: external networks are not allowed", name)
			}
		}
		if d, _ := n["driver"].(string); d == "host" || d == "none" {
			add("network %q: driver %s is not allowed", name, d)
		}
	}
	for _, name := range sortedKeys(c.Volumes) {
		v := c.Volumes[name]
		dockerName, _ := v["name"].(string)
		if dockerName == "" {
			dockerName = name
		}
		if truthy(v["external"]) {
			add("volume %q: external volumes are not allowed", name)
		}
		if strings.HasPrefix(dockerName, "aethera_") || strings.HasPrefix(dockerName, "aethera-") {
			add("volume %q: volume names starting with aethera_ or aethera- are reserved", name)
		}
		// A "named" volume can be a bind mount in disguise: driver local with o=bind and a device path.
		if opts, ok := v["driver_opts"].(map[string]any); ok {
			o, _ := opts["o"].(string)
			t, _ := opts["type"].(string)
			if dev, _ := opts["device"].(string); dev != "" && (strings.Contains(o, "bind") || t == "none") {
				if msg := checkBind(dev, root, pol.Binds); msg != "" {
					add("volume %q: %s", name, msg)
				}
			}
		}
	}
	for _, grp := range []struct {
		kind string
		defs map[string]map[string]any
	}{{"secret", c.Secrets}, {"config", c.Configs}} {
		for _, name := range sortedKeys(grp.defs) {
			if f, ok := grp.defs[name]["file"].(string); ok {
				local(fmt.Sprintf("%s %q: file", grp.kind, name), f)
			}
		}
	}
	return out
}

// publishedPort is the host port of one resolved `ports:` entry (long form, published as a string or a number); 0 when
// the entry publishes none or a range.
func publishedPort(e any) int {
	m, ok := e.(map[string]any)
	if !ok {
		return 0
	}
	switch t := m["published"].(type) {
	case string:
		n, err := strconv.Atoi(strings.TrimSpace(t))
		if err != nil {
			return 0
		}
		return n
	case float64:
		return int(t)
	}
	return 0
}

// checkBuild vets one service's build section as docker compose resolved it (context absolute).
func checkBuild(svc string, b any, local func(what, p string), add func(format string, a ...any)) {
	m, ok := b.(map[string]any)
	if !ok {
		return
	}
	what := fmt.Sprintf("service %q: build context", svc)
	ctx, _ := m["context"].(string)
	local(what, ctx)
	if df, _ := m["dockerfile"].(string); df != "" && ctx != "" && !strings.Contains(ctx, "://") {
		p := df
		if !filepath.IsAbs(p) {
			p = filepath.Join(ctx, filepath.FromSlash(df))
		}
		local(fmt.Sprintf("service %q: dockerfile", svc), p)
	}
	switch ac := m["additional_contexts"].(type) {
	case map[string]any:
		for _, k := range sortedKeys(ac) {
			if s, ok := ac[k].(string); ok {
				local(fmt.Sprintf("service %q: additional build context %q", svc, k), s)
			}
		}
	case []any:
		for _, e := range ac {
			if s, ok := e.(string); ok {
				if _, v, ok := strings.Cut(s, "="); ok {
					local(fmt.Sprintf("service %q: additional build context", svc), v)
				}
			}
		}
	}
	if truthy(m["privileged"]) {
		add("service %q: privileged builds are not allowed", svc)
	}
	if n, _ := m["network"].(string); n == "host" {
		add("service %q: building on the host network is not allowed", svc)
	}
	if e, ok := m["entitlements"].([]any); ok && len(e) > 0 {
		add("service %q: build entitlements are not allowed", svc)
	}
}

// checkBind vets one bind mount source of the resolved configuration: below the root (a repository or project file) it is
// fine, anything else must pass the agent's bind policy; the Docker socket never does.
func checkBind(src, root string, binds BindChecker) string {
	if src == "" {
		return ""
	}
	norm := strings.ReplaceAll(src, `\`, "/")
	if strings.HasSuffix(norm, "/docker.sock") || strings.HasSuffix(norm, "/podman.sock") || norm == "/var/run" || norm == "/run" {
		return fmt.Sprintf("bind mount %q is the container runtime socket", src)
	}
	if within(root, realPath(src)) {
		return ""
	}
	if binds == nil {
		return "bind mounts are disabled"
	}
	if err := binds.CheckBindSource(src); err != nil {
		return err.Error()
	}
	return ""
}

func truthy(v any) bool {
	switch t := v.(type) {
	case bool:
		return t
	case string:
		return strings.EqualFold(t, "true")
	}
	return false
}

// realPath resolves symlinks as far as the path exists (a bind source docker would create is judged by its parents).
func realPath(p string) string {
	p = filepath.Clean(p)
	rest := ""
	for cur := p; ; {
		if r, err := filepath.EvalSymlinks(cur); err == nil {
			if rest == "" {
				return r
			}
			return filepath.Join(r, rest)
		}
		parent := filepath.Dir(cur)
		if parent == cur {
			return p
		}
		rest = filepath.Join(filepath.Base(cur), rest)
		cur = parent
	}
}

// within reports whether p is root or below it (both already cleaned and symlink-free).
func within(root, p string) bool {
	if root == "" {
		return false
	}
	rel, err := filepath.Rel(root, p)
	if err != nil {
		return false
	}
	return rel == "." || (rel != ".." && !strings.HasPrefix(rel, ".."+string(filepath.Separator)) && !filepath.IsAbs(rel))
}

// displayPath shows a path relative to the root when it is below it.
func displayPath(root, p string) string {
	if rel, err := filepath.Rel(root, p); err == nil && !strings.HasPrefix(rel, "..") {
		return rel
	}
	return p
}

// overrideNetworks reads the networks the Aethera override declares as external: compose key -> docker network name.
func overrideNetworks(override string) map[string]string {
	out := map[string]string{}
	if strings.TrimSpace(override) == "" {
		return out
	}
	var doc struct {
		Networks map[string]map[string]any `yaml:"networks"`
	}
	if yaml.Unmarshal([]byte(override), &doc) != nil {
		return out
	}
	for k, n := range doc.Networks {
		if !truthy(n["external"]) {
			continue
		}
		name, _ := n["name"].(string)
		if name == "" {
			name = k
		}
		out[k] = name
	}
	return out
}
