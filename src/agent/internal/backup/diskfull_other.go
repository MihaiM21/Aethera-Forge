//go:build !windows

package backup

func isWindowsDiskFull(error) bool { return false }
