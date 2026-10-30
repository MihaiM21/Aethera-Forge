package mesh

import (
	"errors"
	"fmt"
	"os"
	"strings"

	"golang.zx2c4.com/wireguard/wgctrl/wgtypes"

	"github.com/mihaim21/aethera-forge/agent/internal/state"
)

// LoadOrCreateKey returns the server's WireGuard private key, generating and storing it (mode 0600, atomically)
// on first use. The private key never leaves this file and the process: only the public key is reported.
//
// An unreadable or malformed file is an error and is never overwritten: replacing it would silently change the
// server's identity in the mesh.
func LoadOrCreateKey(path string) (wgtypes.Key, error) {
	key, err := readKey(path)
	if err == nil {
		return key, nil
	}
	if !errors.Is(err, os.ErrNotExist) {
		return wgtypes.Key{}, err
	}
	key, err = wgtypes.GeneratePrivateKey()
	if err != nil {
		return wgtypes.Key{}, fmt.Errorf("generate the mesh key: %w", err)
	}
	if err := state.WriteFileAtomic(path, []byte(key.String()+"\n"), 0o600); err != nil {
		return wgtypes.Key{}, fmt.Errorf("store the mesh key: %w", err)
	}
	return key, nil
}

// PublicKey reads the stored key without creating one; ok is false while the server has none.
func PublicKey(path string) (pub wgtypes.Key, ok bool, err error) {
	key, err := readKey(path)
	if errors.Is(err, os.ErrNotExist) {
		return wgtypes.Key{}, false, nil
	}
	if err != nil {
		return wgtypes.Key{}, false, err
	}
	return key.PublicKey(), true, nil
}

func readKey(path string) (wgtypes.Key, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return wgtypes.Key{}, err
		}
		return wgtypes.Key{}, fmt.Errorf("read the mesh key: %w", errors.Unwrap(err))
	}
	key, err := wgtypes.ParseKey(strings.TrimSpace(string(data)))
	if err != nil {
		// The message names the file, never its content.
		return wgtypes.Key{}, fmt.Errorf("the mesh key file %s is not a WireGuard key; move it away to generate a new identity", path)
	}
	return key, nil
}
