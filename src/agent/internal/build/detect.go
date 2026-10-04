package build

import (
	"encoding/json"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
)

var exposeRe = regexp.MustCompile(`(?im)^\s*EXPOSE\s+(.+)$`)

func exists(dir, name string) bool {
	_, err := os.Stat(filepath.Join(dir, name))
	return err == nil
}

// Detect inspects a checked-out context directory and proposes build methods, best first (spec 4.2).
func Detect(dir string) []*agentv1.BuildCandidate {
	var out []*agentv1.BuildCandidate

	if exists(dir, "Dockerfile") {
		c := &agentv1.BuildCandidate{
			Engine: agentv1.BuildEngine_BUILD_ENGINE_DOCKERFILE, Confidence: 0.95, DockerfilePath: "Dockerfile",
			Reason: "Dockerfile found at ./Dockerfile",
		}
		if b, err := os.ReadFile(filepath.Join(dir, "Dockerfile")); err == nil {
			c.SuggestedPorts = exposedPorts(string(b))
		}
		out = append(out, c)
	}

	if pkg, ok := readPackageJSON(dir); ok {
		install := installCommand(dir)
		_, hasBuild := pkg.Scripts["build"]
		static := isStaticFramework(pkg)
		buildCmd := ""
		if hasBuild {
			buildCmd = runScript(dir, "build")
		}
		if static && hasBuild {
			out = append(out, &agentv1.BuildCandidate{
				Engine: agentv1.BuildEngine_BUILD_ENGINE_STATIC, Confidence: 0.8, Language: "node",
				Reason: "package.json with a static-site framework and a build script", InstallCommand: install,
				BuildCommand: buildCmd, OutputDir: staticOutputDir(pkg), SuggestedPorts: []uint32{80},
			})
		}
		conf := 0.7
		if static {
			conf = 0.5
		}
		start := ""
		if _, ok := pkg.Scripts["start"]; ok {
			start = runScript(dir, "start")
		}
		out = append(out, &agentv1.BuildCandidate{
			Engine: agentv1.BuildEngine_BUILD_ENGINE_NIXPACKS, Confidence: conf, Language: "node",
			Reason: "package.json found", InstallCommand: install, BuildCommand: buildCmd, StartCommand: start, SuggestedPorts: []uint32{3000},
		})
	} else if lang, reason, port := otherLanguage(dir); lang != "" {
		out = append(out, &agentv1.BuildCandidate{
			Engine: agentv1.BuildEngine_BUILD_ENGINE_NIXPACKS, Confidence: 0.6, Language: lang, Reason: reason, SuggestedPorts: port,
		})
	}

	if exists(dir, "index.html") && len(out) == 0 {
		out = append(out, &agentv1.BuildCandidate{
			Engine: agentv1.BuildEngine_BUILD_ENGINE_STATIC, Confidence: 0.5, Reason: "index.html found, no build tooling",
			OutputDir: ".", SuggestedPorts: []uint32{80},
		})
	}

	sort.SliceStable(out, func(i, j int) bool { return out[i].Confidence > out[j].Confidence })
	return out
}

func exposedPorts(dockerfile string) []uint32 {
	var ports []uint32
	for _, m := range exposeRe.FindAllStringSubmatch(dockerfile, -1) {
		for _, f := range strings.Fields(m[1]) {
			f, _, _ = strings.Cut(f, "/")
			if n, err := strconv.ParseUint(f, 10, 16); err == nil {
				ports = append(ports, uint32(n))
			}
		}
	}
	return ports
}

type packageJSON struct {
	Scripts         map[string]string `json:"scripts"`
	Dependencies    map[string]string `json:"dependencies"`
	DevDependencies map[string]string `json:"devDependencies"`
}

func readPackageJSON(dir string) (packageJSON, bool) {
	var p packageJSON
	b, err := os.ReadFile(filepath.Join(dir, "package.json"))
	if err != nil || json.Unmarshal(b, &p) != nil {
		return p, false
	}
	return p, true
}

func (p packageJSON) has(name string) bool {
	_, a := p.Dependencies[name]
	_, b := p.DevDependencies[name]
	return a || b
}

// isStaticFramework is true for tooling that emits a static bundle and has no server of its own.
func isStaticFramework(p packageJSON) bool {
	if p.has("next") || p.has("express") || p.has("fastify") || p.has("@nestjs/core") || p.has("nuxt") {
		return false
	}
	return p.has("vite") || p.has("react-scripts") || p.has("@angular/cli") || p.has("@vue/cli-service") || p.has("parcel")
}

func staticOutputDir(p packageJSON) string {
	if p.has("react-scripts") || p.has("parcel") {
		return "build"
	}
	return "dist"
}

func installCommand(dir string) string {
	switch {
	case exists(dir, "pnpm-lock.yaml"):
		return "corepack enable && pnpm install --frozen-lockfile"
	case exists(dir, "yarn.lock"):
		return "corepack enable && yarn install --frozen-lockfile"
	case exists(dir, "package-lock.json"):
		return "npm ci"
	}
	return "npm install"
}

func runScript(dir, name string) string {
	switch {
	case exists(dir, "pnpm-lock.yaml"):
		return "pnpm run " + name
	case exists(dir, "yarn.lock"):
		return "yarn " + name
	}
	return "npm run " + name
}

func otherLanguage(dir string) (lang, reason string, ports []uint32) {
	switch {
	case exists(dir, "go.mod"):
		return "go", "go.mod found", []uint32{8080}
	case exists(dir, "requirements.txt"), exists(dir, "pyproject.toml"):
		return "python", "Python project files found", []uint32{8000}
	case exists(dir, "Gemfile"):
		return "ruby", "Gemfile found", []uint32{3000}
	case exists(dir, "Cargo.toml"):
		return "rust", "Cargo.toml found", []uint32{8080}
	case exists(dir, "composer.json"):
		return "php", "composer.json found", []uint32{80}
	}
	if m, _ := filepath.Glob(filepath.Join(dir, "*.csproj")); len(m) > 0 {
		return "dotnet", ".csproj found", []uint32{8080}
	}
	return "", "", nil
}
