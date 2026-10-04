package ops

import (
	"context"
	"errors"
	"strings"
	"testing"
	"time"

	"google.golang.org/protobuf/types/known/durationpb"
	"google.golang.org/protobuf/types/known/timestamppb"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/build"
	"github.com/mihaim21/aethera-forge/agent/internal/compose"
	"github.com/mihaim21/aethera-forge/agent/internal/dispatch"
	"github.com/mihaim21/aethera-forge/agent/internal/docker"
	"github.com/mihaim21/aethera-forge/agent/internal/docker/dockertest"
	"github.com/mihaim21/aethera-forge/agent/internal/proxy"
)

func newDeps() (*Deps, *dockertest.Fake) {
	f := dockertest.New()
	return &Deps{Docker: f, BaseContext: context.Background(), Now: time.Now}, f
}

func envFor(cmd *agentv1.Command) *dispatch.Env {
	return &dispatch.Env{Cmd: cmd, Progress: func(string, string, int32) {}}
}

func addImage(f *dockertest.Fake, id string, tags []string, age time.Duration, labels map[string]string) {
	f.Images[id] = &agentv1.ImageInfo{
		Id: id, RepoTags: tags, SizeBytes: 100, Labels: labels, CreatedAt: timestamppb.New(time.Now().Add(-age)), ContainersUsing: -1,
	}
}

func TestImagePruneKeepsUsedAndKeptImages(t *testing.T) {
	d, f := newDeps()
	addImage(f, "sha256:used", []string{"web:1"}, time.Hour, nil)
	addImage(f, "sha256:keep", []string{"web:0"}, time.Hour, nil)
	addImage(f, "sha256:unused", []string{"old:1"}, time.Hour, nil)
	addImage(f, "sha256:dangling", nil, time.Hour, nil)
	f.AddContainer(&agentv1.ContainerInfo{Id: "c1", Name: "web", Image: "web:1", ImageId: "sha256:used", State: agentv1.ContainerState_CONTAINER_STATE_RUNNING})

	// Dangling only (default).
	res, err := d.pruneImages(context.Background(), false, 0, nil, []string{"web:0"})
	if err != nil {
		t.Fatal(err)
	}
	if len(res.Deleted) != 1 || res.Deleted[0] != "sha256:dangling" {
		t.Fatalf("dangling prune deleted %v", res.Deleted)
	}
	// All unused, but rollback points stay.
	res, err = d.pruneImages(context.Background(), true, 0, nil, []string{"web:0"})
	if err != nil {
		t.Fatal(err)
	}
	if len(res.Deleted) != 1 || res.Deleted[0] != "sha256:unused" {
		t.Fatalf("unused prune deleted %v", res.Deleted)
	}
	if _, ok := f.Images["sha256:used"]; !ok {
		t.Fatal("image used by a container was pruned")
	}
	if _, ok := f.Images["sha256:keep"]; !ok {
		t.Fatal("kept rollback image was pruned")
	}
	if res.SpaceReclaimedBytes != 100 {
		t.Fatalf("reclaimed = %d", res.SpaceReclaimedBytes)
	}
}

func TestImagePruneHonoursOlderThanAndLabels(t *testing.T) {
	d, f := newDeps()
	addImage(f, "sha256:new", []string{"a:1"}, time.Minute, map[string]string{"aethera.managed": "true"})
	addImage(f, "sha256:old", []string{"b:1"}, 48*time.Hour, map[string]string{"aethera.managed": "true"})
	addImage(f, "sha256:oldforeign", []string{"c:1"}, 48*time.Hour, nil)
	res, err := d.pruneImages(context.Background(), true, 24*time.Hour, []string{"aethera.managed=true"}, nil)
	if err != nil {
		t.Fatal(err)
	}
	if len(res.Deleted) != 1 || res.Deleted[0] != "sha256:old" {
		t.Fatalf("deleted %v", res.Deleted)
	}
}

func TestVolumePruneNeverTouchesUnlabelledOrUsedVolumes(t *testing.T) {
	d, f := newDeps()
	f.Volumes["data-labelled"] = &agentv1.VolumeInfo{Name: "data-labelled", Labels: map[string]string{"aethera.managed": "true"}}
	f.Volumes["used"] = &agentv1.VolumeInfo{Name: "used", Labels: map[string]string{"aethera.managed": "true"}}
	f.Volumes["precious-unlabelled"] = &agentv1.VolumeInfo{Name: "precious-unlabelled"}
	f.AddContainer(&agentv1.ContainerInfo{Id: "c1", Name: "db", State: agentv1.ContainerState_CONTAINER_STATE_EXITED,
		Mounts: []*agentv1.VolumeMount{{Type: agentv1.MountType_MOUNT_TYPE_VOLUME, Source: "used", Target: "/d"}}})

	cmd := &agentv1.Command{Request: &agentv1.Command_VolumePrune{VolumePrune: &agentv1.VolumePrune{}}}
	r, err := d.volumePrune(context.Background(), envFor(cmd))
	if err != nil {
		t.Fatal(err)
	}
	if got := r.GetPrune().GetDeleted(); len(got) != 1 || got[0] != "data-labelled" {
		t.Fatalf("deleted %v", got)
	}
	if _, ok := f.Volumes["precious-unlabelled"]; !ok {
		t.Fatal("unlabelled volume pruned without include_unlabelled")
	}
	if _, ok := f.Volumes["used"]; !ok {
		t.Fatal("volume referenced by a (stopped) container pruned")
	}

	cmd = &agentv1.Command{Request: &agentv1.Command_VolumePrune{VolumePrune: &agentv1.VolumePrune{IncludeUnlabelled: true}}}
	if _, err := d.volumePrune(context.Background(), envFor(cmd)); err != nil {
		t.Fatal(err)
	}
	if _, ok := f.Volumes["precious-unlabelled"]; ok {
		t.Fatal("include_unlabelled had no effect")
	}
	if _, ok := f.Volumes["used"]; !ok {
		t.Fatal("used volume pruned")
	}
}

func TestSystemPruneOnlyRemovesAnonymousVolumes(t *testing.T) {
	d, f := newDeps()
	anon := strings.Repeat("a1", 32)
	f.Volumes[anon] = &agentv1.VolumeInfo{Name: anon}
	f.Volumes["named-data"] = &agentv1.VolumeInfo{Name: "named-data"}
	f.AddContainer(&agentv1.ContainerInfo{Id: "dead", Name: "old", State: agentv1.ContainerState_CONTAINER_STATE_EXITED})
	f.AddContainer(&agentv1.ContainerInfo{Id: "live", Name: "live", State: agentv1.ContainerState_CONTAINER_STATE_RUNNING})
	f.Networks["unused-net"] = &agentv1.NetworkInfo{Id: "n1", Name: "unused-net", CreatedAt: timestamppb.Now()}
	f.Networks["bridge"] = &agentv1.NetworkInfo{Id: "n0", Name: "bridge"}

	cmd := &agentv1.Command{Request: &agentv1.Command_SystemPrune{SystemPrune: &agentv1.SystemPrune{
		StoppedContainers: true, UnusedNetworks: true, BuildCache: true, Volumes: true,
	}}}
	r, err := d.systemPrune(context.Background(), envFor(cmd))
	if err != nil {
		t.Fatal(err)
	}
	if _, ok := f.Volumes["named-data"]; !ok {
		t.Fatal("named volume pruned by system prune")
	}
	if _, ok := f.Volumes[anon]; ok {
		t.Fatal("anonymous volume not pruned")
	}
	if _, ok := f.Containers["live"]; !ok {
		t.Fatal("running container removed")
	}
	if _, ok := f.Containers["dead"]; ok {
		t.Fatal("stopped container not removed")
	}
	if _, ok := f.Networks["bridge"]; !ok {
		t.Fatal("default network removed")
	}
	if _, ok := f.Networks["unused-net"]; ok {
		t.Fatal("unused network kept")
	}
	if r.GetPrune().GetSpaceReclaimedBytes() != 4096 {
		t.Fatalf("build cache bytes missing: %d", r.GetPrune().GetSpaceReclaimedBytes())
	}
}

func TestNetworkPruneSkipsNetworksWithEndpointsAndYoungOnes(t *testing.T) {
	d, f := newDeps()
	f.Networks["busy"] = &agentv1.NetworkInfo{Id: "1", Name: "busy", Endpoints: []*agentv1.NetworkEndpoint{{ContainerId: "c"}}}
	f.Networks["fresh"] = &agentv1.NetworkInfo{Id: "2", Name: "fresh", CreatedAt: timestamppb.Now()}
	f.Networks["stale"] = &agentv1.NetworkInfo{Id: "3", Name: "stale", CreatedAt: timestamppb.New(time.Now().Add(-48 * time.Hour))}
	res, err := d.pruneNetworks(context.Background(), 24*time.Hour, nil)
	if err != nil {
		t.Fatal(err)
	}
	if len(res.Deleted) != 1 || res.Deleted[0] != "stale" {
		t.Fatalf("deleted %v", res.Deleted)
	}
}

func TestRemovalsAreIdempotent(t *testing.T) {
	d, _ := newDeps()
	ctx := context.Background()
	cmds := []*agentv1.Command{
		{Request: &agentv1.Command_ContainerRemove{ContainerRemove: &agentv1.ContainerRemove{Container: "ghost"}}},
		{Request: &agentv1.Command_ImageRemove{ImageRemove: &agentv1.ImageRemove{Image: "ghost:1"}}},
		{Request: &agentv1.Command_VolumeRemove{VolumeRemove: &agentv1.VolumeRemove{Name: "ghost"}}},
		{Request: &agentv1.Command_NetworkRemove{NetworkRemove: &agentv1.NetworkRemove{Network: "ghost"}}},
	}
	handlers := []dispatch.Handler{d.containerRemove, d.imageRemove, d.volumeRemove, d.networkRemove}
	for i, h := range handlers {
		if _, err := h(ctx, envFor(cmds[i])); err != nil {
			t.Errorf("remove #%d of a missing object failed: %v", i, err)
		}
	}
	// ...but stopping a missing container is an error.
	_, err := d.containerStop(ctx, envFor(&agentv1.Command{Request: &agentv1.Command_ContainerStop{ContainerStop: &agentv1.ContainerStop{Container: "ghost"}}}))
	if !errors.Is(err, docker.ErrNotFound) {
		t.Fatalf("stop of missing container: %v", err)
	}
}

func TestNetworkCreateIfNotExists(t *testing.T) {
	d, f := newDeps()
	ctx := context.Background()
	mk := func(ifNot bool) *agentv1.Command {
		return &agentv1.Command{Request: &agentv1.Command_NetworkCreate{NetworkCreate: &agentv1.NetworkCreate{Name: "proj-net", IfNotExists: ifNot}}}
	}
	if _, err := d.networkCreate(ctx, envFor(mk(false))); err != nil {
		t.Fatal(err)
	}
	r, err := d.networkCreate(ctx, envFor(mk(true)))
	if err != nil || r.GetNetworkCreate().GetNetwork().GetName() != "proj-net" {
		t.Fatalf("if_not_exists: %v %v", r, err)
	}
	_, err = d.networkCreate(ctx, envFor(mk(false)))
	var de *dispatch.Error
	if !errors.As(err, &de) || de.Code != agentv1.ErrorCode_ERROR_CODE_ALREADY_EXISTS {
		t.Fatalf("duplicate create: %v", err)
	}
	creates := 0
	for _, c := range f.CallLog() {
		if strings.HasPrefix(c, "NetworkCreate") {
			creates++
		}
	}
	if creates != 1 {
		t.Fatalf("docker create called %d times", creates)
	}
}

func TestContainerCreateReplaceExisting(t *testing.T) {
	d, f := newDeps()
	ctx := context.Background()
	addImage(f, "sha256:img", []string{"app:1"}, time.Hour, nil)
	f.AddContainer(&agentv1.ContainerInfo{Id: "old", Name: "app", Image: "app:1", State: agentv1.ContainerState_CONTAINER_STATE_RUNNING})
	mk := func(replace bool) *agentv1.Command {
		return &agentv1.Command{Request: &agentv1.Command_ContainerCreate{ContainerCreate: &agentv1.ContainerCreate{
			Spec: &agentv1.ContainerSpec{Image: "app:1", Name: "app", StopTimeout: durationpb.New(time.Second)}, Start: true, ReplaceExisting: replace,
		}}}
	}
	_, err := d.containerCreate(ctx, envFor(mk(false)))
	var de *dispatch.Error
	if !errors.As(err, &de) || de.Code != agentv1.ErrorCode_ERROR_CODE_ALREADY_EXISTS {
		t.Fatalf("create over existing: %v", err)
	}
	r, err := d.containerCreate(ctx, envFor(mk(true)))
	if err != nil {
		t.Fatal(err)
	}
	if r.GetContainerCreate().GetState() != agentv1.ContainerState_CONTAINER_STATE_RUNNING {
		t.Fatalf("state = %v", r.GetContainerCreate().GetState())
	}
	if _, ok := f.Containers["old"]; ok {
		t.Fatal("old container not replaced")
	}
}

func TestImagePullResultDigestAndAlreadyPresent(t *testing.T) {
	d, f := newDeps()
	ctx := context.Background()
	cmd := &agentv1.Command{Request: &agentv1.Command_ImagePull{ImagePull: &agentv1.ImagePull{Reference: "ghcr.io/acme/api:1.4"}}}
	r, err := d.imagePull(ctx, envFor(cmd))
	if err != nil {
		t.Fatal(err)
	}
	p := r.GetImagePull()
	if p.GetAlreadyPresent() || !strings.HasPrefix(p.GetDigest(), "sha256:") || p.GetImageId() == "" {
		t.Fatalf("first pull = %v", p)
	}
	r2, _ := d.imagePull(ctx, envFor(cmd))
	if !r2.GetImagePull().GetAlreadyPresent() {
		t.Fatalf("second pull = %v", r2.GetImagePull())
	}
	f.PullErr = docker.ErrPullFailed
	if _, err := d.imagePull(ctx, envFor(&agentv1.Command{Request: &agentv1.Command_ImagePull{ImagePull: &agentv1.ImagePull{Reference: "other:1"}}})); !errors.Is(err, docker.ErrPullFailed) {
		t.Fatalf("err = %v", err)
	}
	if _, err := d.imagePull(ctx, envFor(&agentv1.Command{Request: &agentv1.Command_ImagePull{ImagePull: &agentv1.ImagePull{Reference: "--all-tags"}}})); err == nil {
		t.Fatal("option-looking reference accepted")
	}
}

func TestImageListReportsContainersUsing(t *testing.T) {
	d, f := newDeps()
	addImage(f, "sha256:a", []string{"a:1"}, time.Hour, nil)
	f.AddContainer(&agentv1.ContainerInfo{Id: "1", Name: "x", ImageId: "sha256:a"})
	f.AddContainer(&agentv1.ContainerInfo{Id: "2", Name: "y", ImageId: "sha256:a"})
	r, err := d.imageList(context.Background(), envFor(&agentv1.Command{Request: &agentv1.Command_ImageList{ImageList: &agentv1.ImageList{}}}))
	if err != nil {
		t.Fatal(err)
	}
	if got := r.GetImageList().GetImages()[0].GetContainersUsing(); got != 2 {
		t.Fatalf("containers_using = %d", got)
	}
}

func TestRegisterOffersOnlyImplementedCommands(t *testing.T) {
	d, _ := newDeps()
	disp := dispatch.New(dispatch.Config{Sender: nopSender{}, Capabilities: d.Capabilities()})
	Register(disp, d)
	for _, c := range d.Capabilities() {
		if strings.Contains(c, "build") || strings.Contains(c, "compose") || strings.Contains(c, "proxy") {
			t.Errorf("capability %s must not be advertised", c)
		}
		if c == CapSelfUpdate {
			t.Error("self-update advertised without an updater")
		}
	}
}

type nopSender struct{}

func (nopSender) Control(*agentv1.AgentMessage) bool { return true }

func TestPhase3CapabilitiesUseTheProtocolNames(t *testing.T) {
	d, _ := newDeps()
	d.Builder, d.Compose, d.Proxy = &build.Service{}, &compose.Service{}, &proxy.Manager{}
	d.LookPath = func(string) (string, error) { return "", errors.New("not installed") }
	got := strings.Join(d.Capabilities(), " ")
	for _, want := range []string{"build.dockerfile", "compose.v2", "proxy.traefik"} {
		if !strings.Contains(got, want) {
			t.Errorf("capability %s is not advertised: %s", want, got)
		}
	}
	if strings.Contains(got, "build.nixpacks") {
		t.Errorf("nixpacks advertised without the CLI: %s", got)
	}

	d.LookPath = func(file string) (string, error) { return "/usr/local/bin/" + file, nil }
	if got := strings.Join(d.Capabilities(), " "); !strings.Contains(got, "build.nixpacks") {
		t.Errorf("nixpacks not advertised although the CLI is installed: %s", got)
	}

	// Every handler is registered under a capability that is advertised, or the dispatcher would refuse its own commands.
	disp := dispatch.New(dispatch.Config{Sender: nopSender{}, Capabilities: d.Capabilities()})
	Register(disp, d)
	advertised := map[string]bool{}
	for _, c := range d.Capabilities() {
		advertised[c] = true
	}
	for _, c := range []string{CapBuilds, CapCompose, CapProxy} {
		if !advertised[c] {
			t.Errorf("handlers use %s but it is not advertised", c)
		}
	}
}
