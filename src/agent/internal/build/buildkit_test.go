package build

import (
	"bytes"
	"context"
	"errors"
	"strings"
	"testing"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/docker/dockertest"
)

const gib = int64(1) << 30

func limits(cpu float64, mem int64) *agentv1.ResourceLimits {
	return &agentv1.ResourceLimits{CpuLimitCores: cpu, MemoryLimitBytes: mem}
}

// daemon makes the recorder behave like a daemon: `docker run --name aethera-buildkit` adds the container, `buildx create` the builder.
func daemon(fake *dockertest.Fake, builderExists *bool, runs *[][]string) func(c Cmd) error {
	return func(c Cmd) error {
		if c.Name != "docker" || len(c.Args) == 0 {
			return nil
		}
		switch {
		case c.Args[0] == "run":
			*runs = append(*runs, c.Args)
			labels := map[string]string{}
			for i, a := range c.Args {
				if a == "--label" && strings.HasPrefix(c.Args[i+1], limitsLabel+"=") {
					labels[limitsLabel] = strings.TrimPrefix(c.Args[i+1], limitsLabel+"=")
				}
			}
			fake.AddContainer(&agentv1.ContainerInfo{Id: "bk1", Name: BuilderName, State: agentv1.ContainerState_CONTAINER_STATE_RUNNING, Labels: labels})
		case c.Args[0] == "buildx" && c.Args[1] == "inspect":
			if !*builderExists {
				return errors.New("no such builder")
			}
		case c.Args[0] == "buildx" && c.Args[1] == "create":
			*builderExists = true
		case c.Args[0] == "buildx" && c.Args[1] == "rm":
			*builderExists = false
		}
		return nil
	}
}

func TestQuotaRequestedNeedsACpuOrMemoryLimit(t *testing.T) {
	if QuotaRequested(nil) || QuotaRequested(&agentv1.ResourceLimits{}) || QuotaRequested(&agentv1.ResourceLimits{CpuReservationCores: 2}) {
		t.Fatal("no limit, no quota")
	}
	if !QuotaRequested(limits(1, 0)) || !QuotaRequested(limits(0, gib)) {
		t.Fatal("a cpu or memory limit is a quota")
	}
}

func TestBuilderContainerIsCreatedWithTheLimitsAsDockerResources(t *testing.T) {
	fake := dockertest.New()
	exists := false
	var runs [][]string
	bk := &BuildKit{Runner: &recorder{hook: daemon(fake, &exists, &runs)}, Docker: fake}

	name, err := bk.Ensure(context.Background(), limits(1.5, 2*gib))
	if err != nil {
		t.Fatal(err)
	}
	if name != BuilderName || len(runs) != 1 {
		t.Fatalf("name=%s runs=%v", name, runs)
	}
	joined := strings.Join(runs[0], " ")
	for _, want := range []string{"--name aethera-buildkit", "--privileged", "--cpus 1.5", "--memory 2147483648", "--memory-swap 2147483648", BuildKitImage} {
		if !strings.Contains(joined, want) {
			t.Errorf("missing %q in %s", want, joined)
		}
	}
	if strings.Contains(joined, "sh -c") || runs[0][len(runs[0])-1] != BuildKitImage {
		t.Errorf("the image must be the last argument and nothing is run through a shell: %s", joined)
	}
	if !exists {
		t.Error("the buildx builder was not created")
	}
}

func TestBuilderIsReusedWhenTheLimitsAreUnchanged(t *testing.T) {
	fake := dockertest.New()
	exists := false
	var runs [][]string
	bk := &BuildKit{Runner: &recorder{hook: daemon(fake, &exists, &runs)}, Docker: fake}
	for i := 0; i < 3; i++ {
		if _, err := bk.Ensure(context.Background(), limits(2, 4*gib)); err != nil {
			t.Fatal(err)
		}
	}
	if len(runs) != 1 {
		t.Fatalf("the container was started %d times", len(runs))
	}
}

func TestBuilderIsRecreatedWhenTheLimitsChange(t *testing.T) {
	fake := dockertest.New()
	exists := false
	var runs [][]string
	r := &recorder{hook: daemon(fake, &exists, &runs)}
	bk := &BuildKit{Runner: r, Docker: fake}
	if _, err := bk.Ensure(context.Background(), limits(2, 4*gib)); err != nil {
		t.Fatal(err)
	}
	if _, err := bk.Ensure(context.Background(), limits(1, 1*gib)); err != nil {
		t.Fatal(err)
	}
	if len(runs) != 2 {
		t.Fatalf("expected the container to be recreated, runs=%d", len(runs))
	}
	if !strings.Contains(strings.Join(runs[1], " "), "--cpus 1 ") {
		t.Errorf("new limits not applied: %v", runs[1])
	}
	removed := false
	for _, c := range fake.Calls {
		if strings.HasPrefix(c, "ContainerRemove aethera-buildkit force=true") {
			removed = true
		}
	}
	if !removed {
		t.Error("the old builder container was not removed")
	}
	if r.find("docker", "buildx") == nil {
		t.Error("the buildx builder was not recreated")
	}
}

func TestEnsureWithoutAQuotaIsRefused(t *testing.T) {
	bk := &BuildKit{Runner: &recorder{}, Docker: dockertest.New()}
	if _, err := bk.Ensure(context.Background(), &agentv1.ResourceLimits{}); err == nil {
		t.Fatal("expected an error")
	}
}

func TestBuildWithAQuotaRunsThroughTheBuildxBuilder(t *testing.T) {
	fake := dockertest.New()
	exists := false
	var runs [][]string
	hook := daemon(fake, &exists, &runs)
	r := &recorder{hook: func(c Cmd) error {
		if c.Name == "git" && c.Args[0] == "rev-parse" {
			c.Stdout.Write([]byte("deadbeef\n"))
		}
		if c.Name == "docker" && len(c.Args) > 1 && c.Args[0] == "buildx" && c.Args[1] == "build" {
			fake.Images["sha256:img"] = &agentv1.ImageInfo{Id: "sha256:img", RepoTags: []string{"aethera/app:1"}, Os: "linux", Architecture: "amd64"}
		}
		return hook(c)
	}}
	svc := newService(r, fake)
	svc.Builders = &BuildKit{Runner: r, Docker: fake}
	req := &agentv1.BuildRequest{
		BuildId: "b1", Engine: agentv1.BuildEngine_BUILD_ENGINE_DOCKERFILE, ImageTags: []string{"aethera/app:1"},
		Git: &agentv1.GitSource{Url: "https://github.com/a/b.git", Ref: "main"}, BuilderLimits: limits(2, 2*gib),
	}
	var out bytes.Buffer
	if _, err := svc.Run(context.Background(), req, &out, &bytes.Buffer{}, progressNoop); err != nil {
		t.Fatal(err)
	}
	b := r.find("docker", "buildx")
	var build *Cmd
	for i := range r.calls {
		if r.calls[i].Name == "docker" && len(r.calls[i].Args) > 1 && r.calls[i].Args[1] == "build" {
			build = &r.calls[i]
		}
	}
	if b == nil || build == nil {
		t.Fatalf("no buildx build: %v", r.calls)
	}
	joined := strings.Join(build.Args, " ")
	for _, want := range []string{"buildx build --builder aethera-buildkit --load", "-t aethera/app:1"} {
		if !strings.Contains(joined, want) {
			t.Errorf("missing %q in %s", want, joined)
		}
	}
	if !strings.Contains(out.String(), "aethera-buildkit") {
		t.Errorf("the build log should say where it builds: %q", out.String())
	}
}

func TestBuildWithoutAQuotaUsesTheDaemonsBuilder(t *testing.T) {
	fake := dockertest.New()
	r := &recorder{hook: func(c Cmd) error {
		if c.Name == "git" && c.Args[0] == "rev-parse" {
			c.Stdout.Write([]byte("deadbeef\n"))
		}
		if c.Name == "docker" && c.Args[0] == "build" {
			fake.Images["sha256:img"] = &agentv1.ImageInfo{Id: "sha256:img", RepoTags: []string{"aethera/app:1"}, Os: "linux", Architecture: "amd64"}
		}
		return nil
	}}
	svc := newService(r, fake)
	svc.Builders = &BuildKit{Runner: r, Docker: fake}
	req := &agentv1.BuildRequest{
		BuildId: "b1", Engine: agentv1.BuildEngine_BUILD_ENGINE_DOCKERFILE, ImageTags: []string{"aethera/app:1"},
		Git: &agentv1.GitSource{Url: "https://github.com/a/b.git", Ref: "main"},
	}
	if _, err := svc.Run(context.Background(), req, &bytes.Buffer{}, &bytes.Buffer{}, progressNoop); err != nil {
		t.Fatal(err)
	}
	if r.find("docker", "build") == nil || r.find("docker", "buildx") != nil || r.find("docker", "run") != nil {
		t.Fatalf("expected the plain docker build only: %v", r.calls)
	}
}

func TestAQuotaOnAnAgentWithoutBuildersOrWithManyPlatformsIsRefused(t *testing.T) {
	fake := dockertest.New()
	r := &recorder{hook: func(c Cmd) error {
		if c.Name == "git" && c.Args[0] == "rev-parse" {
			c.Stdout.Write([]byte("deadbeef\n"))
		}
		return nil
	}}
	req := &agentv1.BuildRequest{
		BuildId: "b1", Engine: agentv1.BuildEngine_BUILD_ENGINE_DOCKERFILE, ImageTags: []string{"aethera/app:1"},
		Git: &agentv1.GitSource{Url: "https://github.com/a/b.git", Ref: "main"}, BuilderLimits: limits(1, 0),
	}
	_, err := newService(r, fake).Run(context.Background(), req, &bytes.Buffer{}, &bytes.Buffer{}, progressNoop)
	var f *Failure
	if !errors.As(err, &f) || f.Code != agentv1.ErrorCode_ERROR_CODE_UNSUPPORTED {
		t.Fatalf("err = %v", err)
	}

	svc := newService(r, fake)
	svc.Builders = &BuildKit{Runner: r, Docker: fake}
	req.TargetPlatforms = []string{"linux/amd64", "linux/arm64"}
	req.Push = true
	_, err = svc.Run(context.Background(), req, &bytes.Buffer{}, &bytes.Buffer{}, progressNoop)
	if !errors.As(err, &f) || f.Code != agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT {
		t.Fatalf("err = %v", err)
	}
}
