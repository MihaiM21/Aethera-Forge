// Package version exposes build-time version information.
package version

// Version is overridden at build time with
// -ldflags "-X github.com/mihaim21/aethera-forge/agent/internal/version.Version=...".
var Version = "0.0.0-dev"

// String returns the human readable version line.
func String() string {
	return "aethera-agent " + Version
}
