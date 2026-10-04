// Command aethera-agent is the Aethera server agent.
//
//	aethera-agent enroll --endpoint host:9443 --token <join token> --ca-sha256 <hex>
//	aethera-agent run [--config /etc/aethera/agent.yaml]
//	aethera-agent --version
package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"io"
	"log/slog"
	"os"
	"os/signal"
	"path/filepath"
	"strings"
	"sync"
	"syscall"

	"github.com/mihaim21/aethera-forge/agent/internal/agent"
	"github.com/mihaim21/aethera-forge/agent/internal/config"
	"github.com/mihaim21/aethera-forge/agent/internal/docker"
	"github.com/mihaim21/aethera-forge/agent/internal/enroll"
	"github.com/mihaim21/aethera-forge/agent/internal/metrics"
	"github.com/mihaim21/aethera-forge/agent/internal/state"
	"github.com/mihaim21/aethera-forge/agent/internal/version"
)

func main() {
	os.Exit(run(os.Args[1:], os.Stdin, os.Stdout, os.Stderr))
}

const usage = `usage:
  aethera-agent enroll --endpoint <host:port> --token <join token> --ca-sha256 <hex> [--name <name>] [--state-dir <dir>]
  aethera-agent run [--config <agent.yaml>] [--state-dir <dir>]
  aethera-agent --version

The join token can also be passed via the AETHERA_JOIN_TOKEN environment
variable, or on stdin with --token -, to keep it out of the process list.
`

func run(args []string, stdin io.Reader, stdout, stderr io.Writer) int {
	if len(args) > 0 {
		switch args[0] {
		case "enroll":
			return runEnroll(args[1:], stdin, stdout, stderr)
		case "run":
			return runAgent(args[1:], stdout, stderr)
		case "version":
			fmt.Fprintln(stdout, version.String())
			return 0
		}
	}
	fs := flag.NewFlagSet("aethera-agent", flag.ContinueOnError)
	fs.SetOutput(stderr)
	showVersion := fs.Bool("version", false, "print version and exit")
	if err := fs.Parse(args); err != nil {
		return 2
	}
	if *showVersion {
		fmt.Fprintln(stdout, version.String())
		return 0
	}
	fmt.Fprint(stderr, usage)
	return 2
}

func loadConfig(path, stateDir string) (*config.Config, error) {
	cfg, err := config.Load(path)
	if err != nil {
		return nil, err
	}
	if stateDir != "" {
		cfg.StateDir = stateDir
		// A state dir override moves the default allowlist with it.
		cfg.Policy.AllowedBindPrefixes = nil
		cfg.Finalize()
		if err := cfg.Validate(); err != nil {
			return nil, err
		}
	}
	return cfg, nil
}

func runEnroll(args []string, stdin io.Reader, stdout, stderr io.Writer) int {
	fs := flag.NewFlagSet("enroll", flag.ContinueOnError)
	fs.SetOutput(stderr)
	endpoint := fs.String("endpoint", "", "control plane gRPC endpoint host:port")
	token := fs.String("token", "", "one-time join token ('-' reads stdin; default $AETHERA_JOIN_TOKEN)")
	pin := fs.String("ca-sha256", "", "SHA-256 fingerprint of the control plane CA (hex)")
	name := fs.String("name", "", "optional display name for this server")
	stateDir := fs.String("state-dir", "", "state directory (default from config, /var/lib/aethera)")
	cfgPath := fs.String("config", config.DefaultPath, "path to agent.yaml")
	if err := fs.Parse(args); err != nil {
		return 2
	}
	tok := *token
	switch {
	case tok == "-":
		b, err := io.ReadAll(io.LimitReader(stdin, 4096))
		if err != nil {
			fmt.Fprintln(stderr, "cannot read token from stdin:", err)
			return 1
		}
		tok = strings.TrimSpace(string(b))
	case tok == "":
		tok = os.Getenv("AETHERA_JOIN_TOKEN")
	}
	if *endpoint == "" || tok == "" || *pin == "" {
		fmt.Fprintln(stderr, "enroll needs --endpoint, --ca-sha256 and a join token (--token, --token -, or $AETHERA_JOIN_TOKEN)")
		return 2
	}
	cfg, err := loadConfig(*cfgPath, *stateDir)
	if err != nil {
		fmt.Fprintln(stderr, "config:", err)
		return 1
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	id, err := enroll.Enroll(ctx, enroll.Params{
		Endpoint: *endpoint, Token: tok, CASHA256: *pin, Name: *name,
		Dir: state.Dir(cfg.StateDir), Version: version.Version,
	})
	if err != nil {
		fmt.Fprintln(stderr, "enrollment failed:", err)
		return 1
	}
	fmt.Fprintf(stdout, "enrolled as server %s; certificate valid until %s\nstate stored in %s\n",
		id.ServerID, id.NotAfter.UTC().Format("2006-01-02"), cfg.StateDir)
	return 0
}

func parseLevel(s string) slog.Level {
	var l slog.Level
	if err := l.UnmarshalText([]byte(s)); err != nil {
		return slog.LevelInfo
	}
	return l
}

// restarter exits the process so systemd starts the (new or previous) binary.
type restarter struct {
	once sync.Once
	stop context.CancelFunc
}

func (r *restarter) Restart() { r.once.Do(r.stop) }

func writableDir(dir string) bool {
	f, err := os.CreateTemp(dir, ".probe")
	if err != nil {
		return false
	}
	name := f.Name()
	f.Close()
	_ = os.Remove(name)
	return true
}

func runAgent(args []string, stdout, stderr io.Writer) int {
	fs := flag.NewFlagSet("run", flag.ContinueOnError)
	fs.SetOutput(stderr)
	stateDir := fs.String("state-dir", "", "state directory")
	cfgPath := fs.String("config", config.DefaultPath, "path to agent.yaml")
	if err := fs.Parse(args); err != nil {
		return 2
	}
	cfg, err := loadConfig(*cfgPath, *stateDir)
	if err != nil {
		fmt.Fprintln(stderr, "config:", err)
		return 1
	}
	inner := slog.NewTextHandler(stderr, &slog.HandlerOptions{Level: parseLevel(cfg.LogLevel)})
	fwd := agent.NewLogForwarder(inner)
	log := slog.New(fwd)

	dk, err := docker.NewSDK(cfg.DockerHost)
	if err != nil {
		log.Error("cannot create docker client", "error", err.Error())
		return 1
	}
	defer dk.Close()

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	exe := ""
	if p, perr := os.Executable(); perr == nil {
		if p, perr = filepath.EvalSymlinks(p); perr == nil && writableDir(filepath.Dir(p)) {
			exe = p // self-update only works where the binary directory is writable
		}
	}
	a, err := agent.New(agent.Options{
		Config: cfg, Dir: state.Dir(cfg.StateDir), Docker: dk, Collector: &metrics.Collector{Docker: dk},
		Version: version.Version, Logger: log, Exe: exe, DeployTools: true, Restarter: &restarter{stop: stop}, LogForwarder: fwd,
	})
	if err != nil {
		if errors.Is(err, state.ErrNotEnrolled) {
			fmt.Fprintln(stderr, err)
		} else {
			fmt.Fprintln(stderr, "start:", err)
		}
		return 1
	}
	log.Info("aethera-agent starting", "version", version.Version, "server_id", a.Identity().ServerID)
	if err := a.Run(ctx); err != nil {
		log.Error("agent stopped", "error", err.Error())
		return 1
	}
	return 0
}
