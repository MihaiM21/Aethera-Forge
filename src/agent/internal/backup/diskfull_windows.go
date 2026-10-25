//go:build windows

package backup

import (
	"errors"
	"syscall"
)

// ERROR_HANDLE_DISK_FULL and ERROR_DISK_FULL.
func isWindowsDiskFull(err error) bool {
	return errors.Is(err, syscall.Errno(39)) || errors.Is(err, syscall.Errno(112))
}
