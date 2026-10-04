package build

import (
	"bytes"
	"context"
	"encoding/base64"
	"fmt"
	"io"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"strings"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
)

var (
	scpLike = regexp.MustCompile(`^[A-Za-z0-9._-]+@[A-Za-z0-9.-]+:[^\s]+$`)
	shaRe   = regexp.MustCompile(`^[0-9a-fA-F]{7,64}$`)
)

// ValidateGitURL accepts https, http, ssh, git and scp-like URLs and rejects anything git could treat as an option or a
// local/transport-helper path (ext::, file://, leading dash).
func ValidateGitURL(raw string) error {
	if raw == "" || strings.HasPrefix(raw, "-") {
		return fmt.Errorf("invalid git url")
	}
	if scpLike.MatchString(raw) {
		return nil
	}
	u, err := url.Parse(raw)
	if err != nil || u.Host == "" {
		return fmt.Errorf("invalid git url")
	}
	switch u.Scheme {
	case "https", "http", "ssh", "git":
		return nil
	}
	return fmt.Errorf("unsupported git url scheme %q", u.Scheme)
}

func validateRef(ref string) error {
	if strings.HasPrefix(ref, "-") || strings.ContainsAny(ref, " \t\r\n\x00") || strings.Contains(ref, "..") {
		return fmt.Errorf("invalid git ref")
	}
	return nil
}

// Checkout is a cloned working tree.
type Checkout struct {
	Dir    string
	Commit string
}

// Clone fetches src into dir and returns the checked-out commit. Credentials reach git through the environment (HTTPS
// token) or a 0600 temp key file (SSH), so they never appear on a command line.
func Clone(ctx context.Context, r Runner, src *agentv1.GitSource, dir string, log io.Writer) (*Checkout, error) {
	if err := ValidateGitURL(src.GetUrl()); err != nil {
		return nil, err
	}
	if err := validateRef(src.GetRef()); err != nil {
		return nil, err
	}
	if c := src.GetCommit(); c != "" && !shaRe.MatchString(c) {
		return nil, fmt.Errorf("invalid commit sha")
	}

	env := []string{"GIT_TERMINAL_PROMPT=0"}
	var cleanups []func()
	defer func() {
		for _, c := range cleanups {
			c()
		}
	}()

	switch m := src.GetCredentials().GetMaterial().(type) {
	case *agentv1.GitCredentials_HttpsToken:
		user := m.HttpsToken.GetUsername()
		if user == "" {
			user = "x-access-token"
		}
		tok := base64.StdEncoding.EncodeToString([]byte(user + ":" + m.HttpsToken.GetToken().GetValue()))
		env = append(env, "GIT_CONFIG_COUNT=1", "GIT_CONFIG_KEY_0=http.extraHeader", "GIT_CONFIG_VALUE_0=Authorization: Basic "+tok)
	case *agentv1.GitCredentials_SshKey:
		f, err := os.CreateTemp("", "aethera-key-*")
		if err != nil {
			return nil, err
		}
		cleanups = append(cleanups, func() { os.Remove(f.Name()) })
		_ = f.Chmod(0o600)
		key := m.SshKey.GetPrivateKey().GetValue()
		if !strings.HasSuffix(key, "\n") {
			key += "\n"
		}
		_, werr := f.WriteString(key)
		f.Close()
		if werr != nil {
			return nil, werr
		}
		strict := "accept-new"
		ssh := "ssh -i " + filepath.ToSlash(f.Name()) + " -o IdentitiesOnly=yes -o BatchMode=yes"
		if kh := m.SshKey.GetKnownHosts(); kh != "" {
			khFile := f.Name() + ".known_hosts"
			if err := os.WriteFile(khFile, []byte(kh+"\n"), 0o600); err != nil {
				return nil, err
			}
			cleanups = append(cleanups, func() { os.Remove(khFile) })
			strict = "yes"
			ssh += " -o UserKnownHostsFile=" + filepath.ToSlash(khFile)
		}
		env = append(env, "GIT_SSH_COMMAND="+ssh+" -o StrictHostKeyChecking="+strict)
	}

	if err := os.MkdirAll(dir, 0o700); err != nil {
		return nil, err
	}
	run := func(args ...string) error {
		return r.Run(ctx, Cmd{Name: "git", Args: args, Dir: dir, Env: env, Stdout: log, Stderr: log})
	}
	if err := run("init", "-q"); err != nil {
		return nil, fmt.Errorf("git init: %w", err)
	}
	if err := run("remote", "add", "origin", src.GetUrl()); err != nil {
		return nil, fmt.Errorf("git remote: %w", err)
	}

	target := src.GetRef()
	if c := src.GetCommit(); c != "" {
		target = c
	}
	if target == "" {
		target = "HEAD"
	}
	fetch := []string{"fetch", "-q"}
	if d := src.GetDepth(); d > 0 && src.GetCommit() == "" {
		fetch = append(fetch, fmt.Sprintf("--depth=%d", d))
	}
	fetch = append(fetch, "origin", target)
	if err := run(fetch...); err != nil {
		return nil, fmt.Errorf("git fetch: %w", err)
	}
	if err := run("checkout", "-q", "--detach", "FETCH_HEAD"); err != nil {
		return nil, fmt.Errorf("git checkout: %w", err)
	}
	if src.GetSubmodules() {
		if err := run("submodule", "update", "--init", "--recursive", "--depth=1"); err != nil {
			return nil, fmt.Errorf("git submodule: %w", err)
		}
	}
	var out bytes.Buffer
	if err := r.Run(ctx, Cmd{Name: "git", Args: []string{"rev-parse", "HEAD"}, Dir: dir, Env: env, Stdout: &out, Stderr: log}); err != nil {
		return nil, fmt.Errorf("git rev-parse: %w", err)
	}
	return &Checkout{Dir: dir, Commit: strings.TrimSpace(out.String())}, nil
}

// ContextDir resolves a user-supplied context path inside root, rejecting escapes.
func ContextDir(root, rel string) (string, error) {
	if rel == "" || rel == "." {
		return root, nil
	}
	if filepath.IsAbs(rel) || strings.HasPrefix(rel, "/") || strings.HasPrefix(rel, `\`) {
		return "", fmt.Errorf("context path must be relative")
	}
	p := filepath.Join(root, filepath.FromSlash(rel))
	r, err := filepath.Rel(root, p)
	if err != nil || r == ".." || strings.HasPrefix(r, ".."+string(filepath.Separator)) {
		return "", fmt.Errorf("context path escapes the repository")
	}
	return p, nil
}
