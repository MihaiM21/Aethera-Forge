package compose

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"io"
	"sort"
	"strconv"
	"strings"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
)

// Inspect resolves the project (checking the repository out when it has a Git source) and reports its services and the
// policy findings that would make Up refuse it. It never builds, pulls or starts anything.
func (s *Service) Inspect(ctx context.Context, p *agentv1.ComposeProject, errw io.Writer) (*agentv1.ComposeInspectResult, error) {
	if err := checkName(p); err != nil {
		return nil, err
	}
	unlock, err := s.lock(ctx, p.GetProjectName())
	if err != nil {
		return nil, err
	}
	defer unlock()
	pr, err := s.prepare(ctx, p, modeInspect, true, io.Discard, errw)
	if err != nil {
		return nil, err
	}
	defer pr.cleanup()

	sum := sha256.Sum256(pr.userFile)
	res := &agentv1.ComposeInspectResult{CommitSha: pr.commit, ComposeSha256: hex.EncodeToString(sum[:])}
	if !pr.parsed {
		res.Violations = dedupe(pr.findings)
		return res, nil
	}
	cfg, findings, err := s.config(ctx, pr, errw)
	var ce *configError
	switch {
	case errors.As(err, &ce):
		res.Violations = append(dedupe(pr.findings), ce.Error())
		return res, nil
	case err != nil:
		return nil, err
	}
	res.Violations = findings
	for _, name := range sortedKeys(cfg.Services) {
		svc := cfg.Services[name]
		image, _ := svc["image"].(string)
		networkMode, _ := svc["network_mode"].(string)
		res.Services = append(res.Services, &agentv1.ComposeServiceSpec{
			Name: name, Image: image, HasBuild: svc["build"] != nil, Ports: containerPorts(svc), NetworkMode: networkMode,
		})
	}
	return res, nil
}

// containerPorts collects the container-side ports of `ports:` (resolved to long form by compose) and `expose:`.
func containerPorts(svc map[string]any) []uint32 {
	set := map[uint32]bool{}
	add := func(v any) {
		switch t := v.(type) {
		case float64:
			if t > 0 && t < 65536 {
				set[uint32(t)] = true
			}
		case string:
			for _, n := range expandPorts(t) {
				set[n] = true
			}
		}
	}
	if ports, ok := svc["ports"].([]any); ok {
		for _, e := range ports {
			switch t := e.(type) {
			case map[string]any:
				add(t["target"])
			case string:
				// short form "host:container[/proto]": the container side is the last colon-separated part.
				parts := strings.Split(t, ":")
				add(parts[len(parts)-1])
			case float64:
				add(t)
			}
		}
	}
	if exp, ok := svc["expose"].([]any); ok {
		for _, e := range exp {
			add(e)
		}
	}
	out := make([]uint32, 0, len(set))
	for n := range set {
		out = append(out, n)
	}
	sort.Slice(out, func(i, j int) bool { return out[i] < out[j] })
	return out
}

// expandPorts reads "3000", "3000/tcp" and "3000-3010" (at most 256 ports of a range).
func expandPorts(s string) []uint32 {
	s, _, _ = strings.Cut(s, "/")
	lo, hi, isRange := strings.Cut(strings.TrimSpace(s), "-")
	a, err := strconv.ParseUint(lo, 10, 16)
	if err != nil || a == 0 {
		return nil
	}
	b := a
	if isRange {
		if b, err = strconv.ParseUint(hi, 10, 16); err != nil || b < a {
			return nil
		}
	}
	var out []uint32
	for n := a; n <= b && len(out) < 256; n++ {
		out = append(out, uint32(n))
	}
	return out
}
