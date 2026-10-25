//go:build !linux

package docker

// killProcessTree is Linux only (it reads /proc); elsewhere an exec that
// outlives its deadline keeps running until it ends by itself.
func killProcessTree(pid int, containerID string) {}
