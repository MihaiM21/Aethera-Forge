package proxy

import (
	"context"
	"os"
	"path/filepath"
	"strings"
	"testing"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/docker/dockertest"
)

func newManager(t *testing.T) (*Manager, *dockertest.Fake) {
	f := dockertest.New()
	return &Manager{Docker: f, Dir: t.TempDir()}, f
}

func calls(f *dockertest.Fake, prefix string) int {
	n := 0
	for _, c := range f.CallLog() {
		if strings.HasPrefix(c, prefix) {
			n++
		}
	}
	return n
}

func TestEnsureCreatesProxyOnceAndIsIdempotent(t *testing.T) {
	m, f := newManager(t)
	req := &agentv1.ProxyEnsure{Provider: "traefik", Config: []byte("entryPoints: {}\n"), RestartOnChange: true}

	first, err := m.Ensure(context.Background(), req)
	if err != nil || !first.Running || !first.Changed || first.ContainerId == "" {
		t.Fatalf("first = %v, %v", first, err)
	}
	if len(f.Created) != 1 {
		t.Fatalf("created %d containers", len(f.Created))
	}
	spec := f.Created[0]
	if spec.Name != ContainerName || spec.Image != DefaultImage+":"+DefaultVersion || len(spec.Ports) != 2 {
		t.Fatalf("spec = %v", spec)
	}
	if spec.Labels[configLabel] != first.ConfigSha256 {
		t.Error("config hash label missing")
	}

	second, err := m.Ensure(context.Background(), req)
	if err != nil || second.Changed || len(f.Created) != 1 {
		t.Fatalf("second = %v, %v, created=%d", second, err, len(f.Created))
	}
}

func TestEnsureRecreatesWhenConfigChanges(t *testing.T) {
	m, f := newManager(t)
	if _, err := m.Ensure(context.Background(), &agentv1.ProxyEnsure{Config: []byte("a: 1\n"), RestartOnChange: true}); err != nil {
		t.Fatal(err)
	}
	res, err := m.Ensure(context.Background(), &agentv1.ProxyEnsure{Config: []byte("a: 2\n"), RestartOnChange: true})
	if err != nil || !res.Changed || len(f.Created) != 2 {
		t.Fatalf("res = %v, %v, created=%d", res, err, len(f.Created))
	}
}

func TestEnsureRejectsBadInput(t *testing.T) {
	m, _ := newManager(t)
	for _, req := range []*agentv1.ProxyEnsure{
		{Provider: "nginx", Config: []byte("x")},
		{Config: nil},
		{Config: []byte("x"), Image: "bad image;rm"},
		{Config: []byte("x"), Version: "--evil"},
	} {
		if _, err := m.Ensure(context.Background(), req); err == nil {
			t.Errorf("%v accepted", req)
		}
	}
}

func TestEnsureWritesDynamicConfigWithoutRecreating(t *testing.T) {
	m, f := newManager(t)
	static := []byte("providers: {}\n")
	if _, err := m.Ensure(context.Background(), &agentv1.ProxyEnsure{Config: static, RestartOnChange: true}); err != nil {
		t.Fatal(err)
	}
	mounts := f.Created[0].Mounts
	if !hasMount(mounts, DynamicDir) {
		t.Fatalf("dynamic directory not mounted: %v", mounts)
	}

	dyn := []byte("http:\n  routers: {}\n")
	res, err := m.Ensure(context.Background(), &agentv1.ProxyEnsure{Config: static, DynamicConfig: dyn, RestartOnChange: true})
	if err != nil || !res.Changed || len(f.Created) != 1 {
		t.Fatalf("res = %v, %v, created=%d", res, err, len(f.Created))
	}
	got, err := os.ReadFile(filepath.Join(m.Dir, "dynamic", dynamicFile))
	if err != nil || string(got) != string(dyn) {
		t.Fatalf("dynamic file = %q, %v", got, err)
	}

	again, err := m.Ensure(context.Background(), &agentv1.ProxyEnsure{Config: static, DynamicConfig: dyn, RestartOnChange: true})
	if err != nil || again.Changed {
		t.Fatalf("again = %v, %v", again, err)
	}

	cleared, err := m.Ensure(context.Background(), &agentv1.ProxyEnsure{Config: static, RestartOnChange: true})
	if err != nil || !cleared.Changed || len(f.Created) != 1 {
		t.Fatalf("cleared = %v, %v", cleared, err)
	}
	if _, err := os.Stat(filepath.Join(m.Dir, "dynamic", dynamicFile)); !os.IsNotExist(err) {
		t.Fatalf("dynamic file still present: %v", err)
	}
}

func hasMount(mounts []*agentv1.VolumeMount, target string) bool {
	for _, mt := range mounts {
		if mt.GetTarget() == target {
			return true
		}
	}
	return false
}
