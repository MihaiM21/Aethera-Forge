package compose

import (
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
)

// persistDirName holds the bind-mounted data of a Git project (<projects dir>/<project>/persist): unlike the checkout it
// survives every deployment.
const persistDirName = "persist"

// placeholderFiles may sit in a directory that a repository ships only so that it exists (`data/.gitkeep`): such a
// directory is data, not source.
var placeholderFiles = map[string]bool{".gitkeep": true, ".keep": true, ".gitignore": true, ".empty": true}

// persistBinds keeps the data of relative bind mounts across deployments. Each deployment of a Git project replaces the
// checkout, so `./data:/var/lib/postgresql/data` would start empty every time. A bind source below the checkout that the
// repository does not provide (or provides only as an empty placeholder directory) is redirected to the same relative path
// under the project's persist directory, through one more override file; a source the repository does provide (a config
// file, a directory of templates) still comes from the fresh checkout.
func (s *Service) persistBinds(pr *project, cfg *resolvedConfig, out io.Writer) error {
	if pr.root == pr.dir { // an inline file: the project directory is never replaced
		return nil
	}
	root := filepath.Clean(pr.root) // compose made the sources absolute against this exact path
	persist := filepath.Join(pr.dir, persistDirName)
	override := map[string]any{}
	for _, name := range sortedKeys(cfg.Services) {
		vols, _ := cfg.Services[name]["volumes"].([]any)
		var moved []any
		for _, v := range vols {
			m, ok := v.(map[string]any)
			if !ok || m["type"] != "bind" {
				continue
			}
			src, _ := m["source"].(string)
			target, _ := m["target"].(string)
			if src == "" || target == "" || !filepath.IsAbs(src) {
				continue
			}
			rel, err := filepath.Rel(root, filepath.Clean(src))
			if err != nil || rel == "." || rel == ".." || strings.HasPrefix(rel, ".."+string(filepath.Separator)) {
				continue // outside the checkout: the bind policy decided about it already
			}
			if providedByRepository(src) {
				continue
			}
			dst := filepath.Join(persist, rel)
			if err := os.MkdirAll(dst, 0o755); err != nil {
				return fmt.Errorf("persistent directory for %s: %w", filepath.ToSlash(rel), err)
			}
			entry := map[string]any{"type": "bind", "source": dst, "target": target}
			if ro, _ := m["read_only"].(bool); ro {
				entry["read_only"] = true
			}
			moved = append(moved, entry)
			fmt.Fprintf(out, "compose: service %s keeps ./%s across deployments\n", name, filepath.ToSlash(rel))
		}
		if len(moved) > 0 {
			override[name] = map[string]any{"volumes": moved}
		}
	}
	if len(override) == 0 {
		return nil
	}
	b, err := json.Marshal(map[string]any{"services": override})
	if err != nil {
		return err
	}
	file := filepath.Join(pr.dir, "compose.aethera-persist.yaml")
	if err := os.WriteFile(file, b, 0o600); err != nil {
		return err
	}
	pr.base = append(pr.base, "-f", file)
	return nil
}

// providedByRepository reports whether the checkout has src as a file, or as a directory with real content.
func providedByRepository(src string) bool {
	fi, err := os.Stat(src)
	if err != nil {
		return false
	}
	if !fi.IsDir() {
		return true
	}
	entries, err := os.ReadDir(src)
	if err != nil {
		return true
	}
	for _, e := range entries {
		if !placeholderFiles[e.Name()] {
			return true
		}
	}
	return false
}
