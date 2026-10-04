// Package build implements the agent's build subsystem (spec section 4, WP3.1): cloning a repository, detecting how to
// build it and running one engine per build method. External programs (git, docker, nixpacks) are only ever started
// with an argument vector, never through a shell, and only from the fixed set the engines use.
package build

import (
	"context"
	"io"
	"os"
	"os/exec"
)

// Cmd is one external program invocation.
type Cmd struct {
	Name   string
	Args   []string
	Dir    string
	Env    []string // appended to the process environment
	Stdout io.Writer
	Stderr io.Writer
}

// Runner starts external programs. Tests replace it with a recorder.
type Runner interface {
	Run(ctx context.Context, c Cmd) error
}

// OSRunner runs programs with os/exec.
type OSRunner struct{}

// Run implements Runner.
func (OSRunner) Run(ctx context.Context, c Cmd) error {
	cmd := exec.CommandContext(ctx, c.Name, c.Args...)
	cmd.Dir = c.Dir
	cmd.Env = append(os.Environ(), c.Env...)
	cmd.Stdout = c.Stdout
	cmd.Stderr = c.Stderr
	return cmd.Run()
}
