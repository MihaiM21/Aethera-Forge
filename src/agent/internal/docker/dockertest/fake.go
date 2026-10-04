// Package dockertest provides an in-memory docker.API for tests; no daemon is
// needed. Behaviour hooks let tests block calls or inject failures.
package dockertest

import (
	"context"
	"fmt"
	"io"
	"strings"
	"sync"
	"time"

	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/docker"
)

// LogLine is one scripted log write.
type LogLine struct {
	Stream agentv1.LogStream
	Data   string
}

// Fake is an in-memory Docker daemon.
type Fake struct {
	mu sync.Mutex

	StatusValue agentv1.DockerStatus
	Containers  map[string]*agentv1.ContainerInfo // key: id
	Images      map[string]*agentv1.ImageInfo     // key: id
	Volumes     map[string]*agentv1.VolumeInfo
	Networks    map[string]*agentv1.NetworkInfo // key: name
	Calls       []string
	Created     []*agentv1.ContainerSpec
	PullAuths   []*agentv1.RegistryAuth

	// BlockList, when non-nil, makes ContainerList wait until it is closed or
	// the context ends.
	BlockList chan struct{}
	// PullErr, when set, fails ImagePull.
	PullErr error
	// InitialLogs are written first by ContainerLogs.
	InitialLogs []LogLine
	logCh       chan LogLine
	EventCh     chan docker.Event
	nextID      int
}

// New creates a fake with a running daemon.
func New() *Fake {
	return &Fake{
		StatusValue: agentv1.DockerStatus_DOCKER_STATUS_RUNNING,
		Containers:  map[string]*agentv1.ContainerInfo{},
		Images:      map[string]*agentv1.ImageInfo{},
		Volumes:     map[string]*agentv1.VolumeInfo{},
		Networks:    map[string]*agentv1.NetworkInfo{},
		logCh:       make(chan LogLine, 1024),
		EventCh:     make(chan docker.Event, 64),
	}
}

var _ docker.API = (*Fake)(nil)

func (f *Fake) call(format string, a ...any) {
	f.Calls = append(f.Calls, fmt.Sprintf(format, a...))
}

// CallLog returns a copy of the recorded calls.
func (f *Fake) CallLog() []string {
	f.mu.Lock()
	defer f.mu.Unlock()
	return append([]string(nil), f.Calls...)
}

// SetStatus changes the daemon status.
func (f *Fake) SetStatus(s agentv1.DockerStatus) {
	f.mu.Lock()
	f.StatusValue = s
	f.mu.Unlock()
}

// PushLog feeds a line to followers of ContainerLogs.
func (f *Fake) PushLog(l LogLine) { f.logCh <- l }

// AddContainer registers a container.
func (f *Fake) AddContainer(c *agentv1.ContainerInfo) {
	f.mu.Lock()
	f.Containers[c.GetId()] = c
	f.mu.Unlock()
}

func (f *Fake) unavailable() error {
	if f.StatusValue != agentv1.DockerStatus_DOCKER_STATUS_RUNNING {
		return fmt.Errorf("%w: fake daemon is %s", docker.ErrUnavailable, f.StatusValue)
	}
	return nil
}

func (f *Fake) find(ref string) *agentv1.ContainerInfo {
	if c, ok := f.Containers[ref]; ok {
		return c
	}
	for _, c := range f.Containers {
		if c.GetName() == ref {
			return c
		}
	}
	return nil
}

func (f *Fake) Status(context.Context) (agentv1.DockerStatus, string) {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.StatusValue, ""
}

func (f *Fake) Info(context.Context) (*agentv1.DockerInfo, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if err := f.unavailable(); err != nil {
		return nil, err
	}
	return &agentv1.DockerInfo{Status: f.StatusValue, Version: "27.0-fake", ContainersRunning: uint32(len(f.Containers))}, nil
}

func (f *Fake) ContainerList(ctx context.Context, all bool, labels []string, name string) ([]*agentv1.ContainerInfo, error) {
	f.mu.Lock()
	f.call("ContainerList all=%v", all)
	block := f.BlockList
	f.mu.Unlock()
	if block != nil {
		select {
		case <-block:
		case <-ctx.Done():
			return nil, ctx.Err()
		}
	}
	f.mu.Lock()
	defer f.mu.Unlock()
	if err := f.unavailable(); err != nil {
		return nil, err
	}
	var out []*agentv1.ContainerInfo
	for _, c := range f.Containers {
		if !all && c.GetState() != agentv1.ContainerState_CONTAINER_STATE_RUNNING {
			continue
		}
		if name != "" && !strings.Contains(c.GetName(), name) {
			continue
		}
		out = append(out, proto.Clone(c).(*agentv1.ContainerInfo))
	}
	return out, nil
}

func (f *Fake) ContainerInspect(_ context.Context, ref string, raw bool) (*agentv1.ContainerInfo, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.call("ContainerInspect %s", ref)
	if err := f.unavailable(); err != nil {
		return nil, err
	}
	c := f.find(ref)
	if c == nil {
		return nil, fmt.Errorf("%w: container %s", docker.ErrNotFound, ref)
	}
	out := proto.Clone(c).(*agentv1.ContainerInfo)
	if raw {
		out.InspectJson = []byte(`{"Config":{"Env":["A="]}}`)
	}
	return out, nil
}

func (f *Fake) ContainerCreate(_ context.Context, spec *agentv1.ContainerSpec) (string, []string, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.call("ContainerCreate %s", spec.GetName())
	if err := f.unavailable(); err != nil {
		return "", nil, err
	}
	if spec.GetName() != "" && f.find(spec.GetName()) != nil {
		return "", nil, fmt.Errorf("%w: name in use", docker.ErrConflict)
	}
	if _, ok := f.Images[spec.GetImage()]; !ok && !f.hasImageRef(spec.GetImage()) {
		return "", nil, fmt.Errorf("%w: image %s", docker.ErrNotFound, spec.GetImage())
	}
	f.nextID++
	id := fmt.Sprintf("c%03d", f.nextID)
	f.Containers[id] = &agentv1.ContainerInfo{
		Id: id, Name: spec.GetName(), Image: spec.GetImage(), State: agentv1.ContainerState_CONTAINER_STATE_CREATED,
		CreatedAt: timestamppb.Now(), Labels: spec.GetLabels(),
	}
	f.Created = append(f.Created, proto.Clone(spec).(*agentv1.ContainerSpec))
	return id, nil, nil
}

func (f *Fake) hasImageRef(ref string) bool {
	for _, im := range f.Images {
		for _, t := range im.GetRepoTags() {
			if t == ref {
				return true
			}
		}
	}
	return false
}

func (f *Fake) setState(ref string, st agentv1.ContainerState, verb string) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.call("%s %s", verb, ref)
	if err := f.unavailable(); err != nil {
		return err
	}
	c := f.find(ref)
	if c == nil {
		return fmt.Errorf("%w: container %s", docker.ErrNotFound, ref)
	}
	c.State = st
	return nil
}

func (f *Fake) ContainerStart(_ context.Context, ref string) error {
	return f.setState(ref, agentv1.ContainerState_CONTAINER_STATE_RUNNING, "ContainerStart")
}

func (f *Fake) ContainerStop(_ context.Context, ref string, _ *time.Duration) error {
	return f.setState(ref, agentv1.ContainerState_CONTAINER_STATE_EXITED, "ContainerStop")
}

func (f *Fake) ContainerRestart(_ context.Context, ref string, _ *time.Duration) error {
	return f.setState(ref, agentv1.ContainerState_CONTAINER_STATE_RUNNING, "ContainerRestart")
}

func (f *Fake) ContainerRemove(_ context.Context, ref string, force, _ bool) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.call("ContainerRemove %s force=%v", ref, force)
	if err := f.unavailable(); err != nil {
		return err
	}
	c := f.find(ref)
	if c == nil {
		return fmt.Errorf("%w: container %s", docker.ErrNotFound, ref)
	}
	delete(f.Containers, c.GetId())
	return nil
}

func (f *Fake) ContainerStats(context.Context, string) (*docker.Stats, error) {
	return &docker.Stats{CPUPercent: 12.5, MemUsedBytes: 1 << 20, MemLimitBytes: 1 << 30, PIDs: 3}, nil
}

func (f *Fake) ContainerLogs(ctx context.Context, ref string, opts docker.LogOptions, stdout, stderr io.Writer) error {
	f.mu.Lock()
	f.call("ContainerLogs %s follow=%v", ref, opts.Follow)
	if f.find(ref) == nil {
		f.mu.Unlock()
		return fmt.Errorf("%w: container %s", docker.ErrNotFound, ref)
	}
	initial := append([]LogLine(nil), f.InitialLogs...)
	f.mu.Unlock()
	write := func(l LogLine) {
		w := stdout
		if l.Stream == agentv1.LogStream_LOG_STREAM_STDERR {
			w = stderr
		}
		_, _ = w.Write([]byte(l.Data))
	}
	for _, l := range initial {
		write(l)
	}
	if !opts.Follow {
		return nil
	}
	for {
		select {
		case l := <-f.logCh:
			write(l)
		case <-ctx.Done():
			return ctx.Err()
		}
	}
}

func (f *Fake) ImagePull(ctx context.Context, ref string, auth *agentv1.RegistryAuth, _ string, progress func(string)) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.call("ImagePull %s", ref)
	f.PullAuths = append(f.PullAuths, auth)
	if f.PullErr != nil {
		return f.PullErr
	}
	if progress != nil {
		progress("Pulling fs layer")
	}
	id := "sha256:" + strings.Repeat("a", 60) + fmt.Sprintf("%04d", len(f.Images))
	if existing := f.imageByRef(ref); existing != nil {
		return nil
	}
	repo := ref
	if i := strings.LastIndex(repo, ":"); i > strings.LastIndex(repo, "/") {
		repo = repo[:i]
	}
	f.Images[id] = &agentv1.ImageInfo{
		Id: id, RepoTags: []string{ref}, RepoDigests: []string{repo + "@sha256:" + strings.Repeat("b", 64)},
		SizeBytes: 1000, CreatedAt: timestamppb.Now(), ContainersUsing: -1,
	}
	return nil
}

func (f *Fake) imageByRef(ref string) *agentv1.ImageInfo {
	if im, ok := f.Images[ref]; ok {
		return im
	}
	for _, im := range f.Images {
		for _, t := range im.GetRepoTags() {
			if t == ref {
				return im
			}
		}
	}
	return nil
}

func (f *Fake) ImageList(context.Context, bool, []string, string) ([]*agentv1.ImageInfo, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	var out []*agentv1.ImageInfo
	for _, im := range f.Images {
		out = append(out, proto.Clone(im).(*agentv1.ImageInfo))
	}
	return out, nil
}

func (f *Fake) ImageInspect(_ context.Context, ref string) (*agentv1.ImageInfo, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if err := f.unavailable(); err != nil {
		return nil, err
	}
	im := f.imageByRef(ref)
	if im == nil {
		return nil, fmt.Errorf("%w: image %s", docker.ErrNotFound, ref)
	}
	return proto.Clone(im).(*agentv1.ImageInfo), nil
}

func (f *Fake) ImageRemove(_ context.Context, ref string, force bool) ([]string, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.call("ImageRemove %s", ref)
	im := f.imageByRef(ref)
	if im == nil {
		return nil, fmt.Errorf("%w: image %s", docker.ErrNotFound, ref)
	}
	delete(f.Images, im.GetId())
	return []string{im.GetId()}, nil
}

func (f *Fake) VolumeList(context.Context, []string) ([]*agentv1.VolumeInfo, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	var out []*agentv1.VolumeInfo
	for _, v := range f.Volumes {
		out = append(out, proto.Clone(v).(*agentv1.VolumeInfo))
	}
	return out, nil
}

func (f *Fake) VolumeInspect(_ context.Context, name string) (*agentv1.VolumeInfo, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	v, ok := f.Volumes[name]
	if !ok {
		return nil, fmt.Errorf("%w: volume %s", docker.ErrNotFound, name)
	}
	return proto.Clone(v).(*agentv1.VolumeInfo), nil
}

func (f *Fake) VolumeCreate(_ context.Context, req *agentv1.VolumeCreate) (*agentv1.VolumeInfo, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.call("VolumeCreate %s", req.GetName())
	if v, ok := f.Volumes[req.GetName()]; ok {
		return proto.Clone(v).(*agentv1.VolumeInfo), nil
	}
	v := &agentv1.VolumeInfo{Name: req.GetName(), Driver: "local", Labels: req.GetLabels(), SizeBytes: -1, RefCount: -1}
	f.Volumes[req.GetName()] = v
	return proto.Clone(v).(*agentv1.VolumeInfo), nil
}

func (f *Fake) VolumeRemove(_ context.Context, name string, _ bool) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.call("VolumeRemove %s", name)
	if _, ok := f.Volumes[name]; !ok {
		return fmt.Errorf("%w: volume %s", docker.ErrNotFound, name)
	}
	delete(f.Volumes, name)
	return nil
}

func (f *Fake) NetworkList(context.Context, []string) ([]*agentv1.NetworkInfo, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	var out []*agentv1.NetworkInfo
	for _, n := range f.Networks {
		out = append(out, proto.Clone(n).(*agentv1.NetworkInfo))
	}
	return out, nil
}

func (f *Fake) NetworkInspect(_ context.Context, ref string) (*agentv1.NetworkInfo, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if n, ok := f.Networks[ref]; ok {
		return proto.Clone(n).(*agentv1.NetworkInfo), nil
	}
	for _, n := range f.Networks {
		if n.GetId() == ref {
			return proto.Clone(n).(*agentv1.NetworkInfo), nil
		}
	}
	return nil, fmt.Errorf("%w: network %s", docker.ErrNotFound, ref)
}

func (f *Fake) NetworkCreate(_ context.Context, req *agentv1.NetworkCreate) (*agentv1.NetworkInfo, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.call("NetworkCreate %s", req.GetName())
	n := &agentv1.NetworkInfo{Id: "n-" + req.GetName(), Name: req.GetName(), Driver: "bridge", Labels: req.GetLabels(), CreatedAt: timestamppb.Now()}
	f.Networks[req.GetName()] = n
	return proto.Clone(n).(*agentv1.NetworkInfo), nil
}

func (f *Fake) NetworkRemove(_ context.Context, ref string) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.call("NetworkRemove %s", ref)
	for name, n := range f.Networks {
		if name == ref || n.GetId() == ref {
			delete(f.Networks, name)
			return nil
		}
	}
	return fmt.Errorf("%w: network %s", docker.ErrNotFound, ref)
}

func (f *Fake) NetworkConnect(_ context.Context, req *agentv1.NetworkConnect) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.call("NetworkConnect %s %s", req.GetNetwork(), req.GetContainer())
	return nil
}

func (f *Fake) NetworkDisconnect(_ context.Context, net, ctr string, _ bool) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.call("NetworkDisconnect %s %s", net, ctr)
	return nil
}

func (f *Fake) BuildCachePrune(context.Context) (int64, error) { return 4096, nil }

func (f *Fake) Events(ctx context.Context) (<-chan docker.Event, <-chan error) {
	out := make(chan docker.Event)
	errc := make(chan error, 1)
	go func() {
		defer close(out)
		for {
			select {
			case ev := <-f.EventCh:
				select {
				case out <- ev:
				case <-ctx.Done():
					return
				}
			case <-ctx.Done():
				return
			}
		}
	}()
	return out, errc
}

func (f *Fake) Close() error { return nil }
