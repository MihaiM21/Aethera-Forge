package docker

import (
	"context"
	"errors"
	"fmt"
	"io"
	"time"

	"github.com/docker/docker/api/types"
	"github.com/docker/docker/api/types/container"
	"github.com/docker/docker/api/types/mount"
	"github.com/docker/docker/pkg/stdcopy"
)

// HelperVolumeTarget is where RunHelper mounts the volume inside the helper.
const HelperVolumeTarget = "/data"

// HelperLabel marks helper containers so a leftover one can be recognised.
const HelperLabel = "aethera.helper"

// ExecSpec is one program run inside a running container.
type ExecSpec struct {
	Container string
	Argv      []string
	// Env entries are "NAME=value"; values may be secret: never log a spec.
	Env        []string
	User       string
	WorkingDir string
	// Stdin is copied to the process and closed at EOF (nil = no stdin).
	Stdin io.Reader
	// Stdout and Stderr receive the demultiplexed output (nil = discarded).
	Stdout, Stderr io.Writer
}

// HelperSpec is a short-lived helper container with one named volume.
type HelperSpec struct {
	Image string
	Cmd   []string
	// Volume is mounted at HelperVolumeTarget.
	Volume   string
	ReadOnly bool
	Stdin    io.Reader
	Stdout   io.Writer
	Stderr   io.Writer
}

func orDiscard(w io.Writer) io.Writer {
	if w == nil {
		return io.Discard
	}
	return w
}

// ContainerExec implements API with the Docker exec API (create, attach, inspect).
func (s *SDK) ContainerExec(ctx context.Context, spec ExecSpec) (int, error) {
	created, err := s.cli.ContainerExecCreate(ctx, spec.Container, container.ExecOptions{
		User: spec.User, WorkingDir: spec.WorkingDir, Env: spec.Env, Cmd: spec.Argv,
		AttachStdin: spec.Stdin != nil, AttachStdout: true, AttachStderr: true,
	})
	if err != nil {
		return 0, mapErr(err)
	}
	att, err := s.cli.ContainerExecAttach(ctx, created.ID, container.ExecAttachOptions{})
	if err != nil {
		return 0, mapErr(err)
	}
	defer att.Close()

	if spec.Stdin != nil {
		go pumpStdin(att, spec.Stdin)
	}
	done := make(chan error, 1)
	go func() {
		_, cerr := stdcopy.StdCopy(orDiscard(spec.Stdout), orDiscard(spec.Stderr), att.Reader)
		done <- cerr
	}()
	select {
	case cerr := <-done:
		if cerr != nil && ctx.Err() == nil {
			return 0, fmt.Errorf("read exec output: %w", cerr)
		}
	case <-ctx.Done():
		s.killExec(created.ID)
		att.Close()
		<-done
		return 0, ctx.Err()
	}
	if ctx.Err() != nil {
		s.killExec(created.ID)
		return 0, ctx.Err()
	}
	// The stream ends when the process closed its output; the exit code is
	// published a moment later.
	for i := 0; i < 100; i++ {
		ins, ierr := s.cli.ContainerExecInspect(context.WithoutCancel(ctx), created.ID)
		if ierr != nil {
			return 0, mapErr(ierr)
		}
		if !ins.Running {
			return ins.ExitCode, nil
		}
		select {
		case <-time.After(50 * time.Millisecond):
		case <-ctx.Done():
			s.killExec(created.ID)
			return 0, ctx.Err()
		}
	}
	return 0, errors.New("exec did not report an exit code")
}

// pumpStdin copies r to the hijacked connection and half-closes it at EOF.
func pumpStdin(att types.HijackedResponse, r io.Reader) {
	_, _ = io.Copy(att.Conn, r)
	_ = att.CloseWrite()
}

// killExec stops the process of an exec the daemon cannot stop itself (the
// Docker API has no exec kill): best effort, only when the process is
// visible to the agent as a descendant of the container (see killProcessTree).
func (s *SDK) killExec(execID string) {
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	ins, err := s.cli.ContainerExecInspect(ctx, execID)
	if err != nil || !ins.Running || ins.Pid <= 0 {
		return
	}
	killProcessTree(ins.Pid, ins.ContainerID)
}

// VolumeOptions implements API.
func (s *SDK) VolumeOptions(ctx context.Context, name string) (map[string]string, error) {
	v, err := s.cli.VolumeInspect(ctx, name)
	if err != nil {
		return nil, mapErr(err)
	}
	return v.Options, nil
}

// RunHelper implements API.
func (s *SDK) RunHelper(ctx context.Context, spec HelperSpec) (int, error) {
	if _, err := s.ImageInspect(ctx, spec.Image); err != nil {
		if !errors.Is(err, ErrNotFound) {
			return 0, err
		}
		if perr := s.ImagePull(ctx, spec.Image, nil, "", nil); perr != nil {
			return 0, perr
		}
	}
	cfg := &container.Config{
		Image: spec.Image, Cmd: spec.Cmd, Labels: map[string]string{HelperLabel: "true"},
		AttachStdin: spec.Stdin != nil, OpenStdin: spec.Stdin != nil, StdinOnce: spec.Stdin != nil,
		AttachStdout: true, AttachStderr: true,
	}
	host := &container.HostConfig{
		NetworkMode: "none",
		Mounts: []mount.Mount{{
			Type: mount.TypeVolume, Source: spec.Volume, Target: HelperVolumeTarget, ReadOnly: spec.ReadOnly,
			VolumeOptions: &mount.VolumeOptions{NoCopy: true},
		}},
	}
	created, err := s.cli.ContainerCreate(ctx, cfg, host, nil, nil, "")
	if err != nil {
		return 0, mapErr(err)
	}
	// The helper never outlives the call, whatever happened to ctx.
	defer func() {
		rctx, cancel := context.WithTimeout(context.WithoutCancel(ctx), 30*time.Second)
		defer cancel()
		_ = s.cli.ContainerRemove(rctx, created.ID, container.RemoveOptions{Force: true})
	}()

	att, err := s.cli.ContainerAttach(ctx, created.ID, container.AttachOptions{
		Stream: true, Stdin: spec.Stdin != nil, Stdout: true, Stderr: true,
	})
	if err != nil {
		return 0, mapErr(err)
	}
	defer att.Close()

	waitCh, errCh := s.cli.ContainerWait(ctx, created.ID, container.WaitConditionNextExit)
	if err := s.cli.ContainerStart(ctx, created.ID, container.StartOptions{}); err != nil {
		return 0, mapErr(err)
	}
	if spec.Stdin != nil {
		go pumpStdin(att, spec.Stdin)
	}
	copied := make(chan error, 1)
	go func() {
		_, cerr := stdcopy.StdCopy(orDiscard(spec.Stdout), orDiscard(spec.Stderr), att.Reader)
		copied <- cerr
	}()

	var exit int
	select {
	case res := <-waitCh:
		exit = int(res.StatusCode)
		if res.Error != nil && res.Error.Message != "" {
			return 0, fmt.Errorf("helper container: %s", res.Error.Message)
		}
	case werr := <-errCh:
		return 0, mapErr(werr)
	case <-ctx.Done():
		return 0, ctx.Err()
	}
	// The container is gone: drain what is left of its output.
	select {
	case <-copied:
	case <-time.After(15 * time.Second):
		att.Close()
	}
	return exit, nil
}
