// Command aethera-agent is the Aethera server agent.
package main

import (
	"flag"
	"fmt"
	"io"
	"os"

	"github.com/mihaim21/aethera-forge/agent/internal/version"
)

func main() {
	os.Exit(run(os.Args[1:], os.Stdout, os.Stderr))
}

func run(args []string, stdout, stderr io.Writer) int {
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

	fmt.Fprintf(stdout, "aethera-agent %s: no runtime features implemented yet\n", version.Version)
	return 0
}
