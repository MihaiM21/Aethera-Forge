package compose

import (
	"context"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/build"
)

// defaultComposeFiles are tried in this order when ComposeProject.compose_path is empty.
var defaultComposeFiles = []string{"docker-compose.yml", "compose.yaml", "docker-compose.yaml", "compose.yml"}

// maxComposeFile bounds what is read to hash and vet; a compose file is a few kilobytes.
const maxComposeFile = 4 << 20

// checkoutDirName is the checkout below the project directory (<projects dir>/<project>/src).
const checkoutDirName = "src"

// cleanRepoPath validates a path relative to the repository root and returns it with forward slashes. Absolute paths (any
// style), backslash tricks, NUL and ".." segments are refused; so is the .git directory.
func cleanRepoPath(p string) (string, error) {
	if p == "" {
		return "", nil
	}
	if strings.ContainsRune(p, 0) || strings.ContainsAny(p, "\r\n") {
		return "", bad("compose_path contains control characters")
	}
	if strings.HasPrefix(p, "/") || strings.HasPrefix(p, `\`) || strings.HasPrefix(p, "~") || (len(p) > 1 && p[1] == ':') {
		return "", bad("compose_path %q must be relative to the repository root", p)
	}
	var segs []string
	for _, seg := range strings.FieldsFunc(p, func(r rune) bool { return r == '/' || r == '\\' }) {
		switch seg {
		case "..":
			return "", bad("compose_path %q must not contain '..'", p)
		case ".":
			continue
		}
		segs = append(segs, seg)
	}
	if len(segs) == 0 {
		return "", bad("compose_path %q does not name a file", p)
	}
	if strings.EqualFold(segs[0], ".git") {
		return "", bad("compose_path %q points into the git directory", p)
	}
	return strings.Join(segs, "/"), nil
}

// findComposeFile locates the compose file in the checkout and returns its path and content. The file, symlinks followed,
// must be a regular file inside the checkout.
func findComposeFile(root, composePath string) (string, []byte, error) {
	rel, err := cleanRepoPath(composePath)
	if err != nil {
		return "", nil, err
	}
	candidates := defaultComposeFiles
	if rel != "" {
		candidates = []string{rel}
	}
	rootReal := realPath(root)
	for _, c := range candidates {
		p := filepath.Join(root, filepath.FromSlash(c))
		fi, err := os.Stat(p)
		if err != nil || !fi.Mode().IsRegular() {
			continue
		}
		if !within(rootReal, realPath(p)) {
			return "", nil, bad("compose file %q resolves outside the repository", c)
		}
		if fi.Size() > maxComposeFile {
			return "", nil, bad("compose file %q is larger than %d bytes", c, maxComposeFile)
		}
		b, err := os.ReadFile(p)
		if err != nil {
			return "", nil, err
		}
		return p, b, nil
	}
	if rel != "" {
		return "", nil, invalid("the repository has no compose file at %q", rel)
	}
	return "", nil, invalid("the repository has none of %s at its root", strings.Join(defaultComposeFiles, ", "))
}

// checkout replaces <dir>/src with a fresh checkout of the project's Git source. Credentials and URL validation are the
// build engine's (build.Clone): a token travels in the environment of git, an SSH key in a 0600 temporary file, never in
// an argument vector.
func (s *Service) checkout(ctx context.Context, g *agentv1.GitSource, dir string, out, errw io.Writer) (*build.Checkout, error) {
	src := filepath.Join(dir, checkoutDirName)
	if err := os.RemoveAll(src); err != nil {
		return nil, err
	}
	clone := build.Clone
	if s.allowLocalGit {
		clone = build.CloneAllowingLocal
	}
	co, err := clone(ctx, s.Runner, build.ShallowByDefault(g), src, errw)
	if err != nil {
		_ = os.RemoveAll(src)
		if ctx.Err() != nil {
			return nil, ctx.Err()
		}
		return nil, &build.Failure{Code: agentv1.ErrorCode_ERROR_CODE_GIT_FAILED, Err: err}
	}
	// The remote URL (which a caller may have written with credentials in it) must not outlive the clone in .git/config.
	_ = s.Runner.Run(ctx, build.Cmd{Name: "git", Args: []string{"remote", "remove", "origin"}, Dir: src, Stdout: io.Discard, Stderr: io.Discard})
	fmt.Fprintf(out, "checked out %s\n", co.Commit)
	return co, nil
}
