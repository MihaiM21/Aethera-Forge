package state

import (
	"errors"
	"os"
	"path/filepath"
	"testing"
	"time"
)

func TestIdentityRoundTripAndMissing(t *testing.T) {
	d := Dir(filepath.Join(t.TempDir(), "aethera"))
	if _, err := d.LoadIdentity(); !errors.Is(err, ErrNotEnrolled) {
		t.Fatalf("err = %v", err)
	}
	want := &Identity{ServerID: "s1", Endpoint: "cp:9443", CAFingerprint: "ab", NotAfter: time.Now().UTC().Truncate(time.Second), RenewBefore: time.Hour}
	if err := d.SaveIdentity(want); err != nil {
		t.Fatal(err)
	}
	got, err := d.LoadIdentity()
	if err != nil || got.ServerID != "s1" || got.Endpoint != "cp:9443" || !got.NotAfter.Equal(want.NotAfter) || got.RenewBefore != time.Hour {
		t.Fatalf("got %+v err %v", got, err)
	}
}

func TestIncompleteIdentityIsRejected(t *testing.T) {
	d := Dir(t.TempDir())
	if err := os.WriteFile(d.IdentityPath(), []byte(`{"server_id":""}`), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, err := d.LoadIdentity(); err == nil {
		t.Fatal("incomplete identity accepted")
	}
}

func TestWriteFileAtomicSetsModeAndReplaces(t *testing.T) {
	p := filepath.Join(t.TempDir(), "sub", "f")
	if err := WriteFileAtomic(p, []byte("one"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := WriteFileAtomic(p, []byte("two"), 0o600); err != nil {
		t.Fatal(err)
	}
	b, _ := os.ReadFile(p)
	st, _ := os.Stat(p)
	if string(b) != "two" || st.Mode().Perm() != 0o600 {
		t.Fatalf("content %q mode %v", b, st.Mode().Perm())
	}
	entries, _ := os.ReadDir(filepath.Dir(p))
	if len(entries) != 1 {
		t.Fatalf("temp files left behind: %v", entries)
	}
}

func TestEnsureCreatesPrivateTree(t *testing.T) {
	d := Dir(filepath.Join(t.TempDir(), "aethera"))
	if err := d.Ensure(); err != nil {
		t.Fatal(err)
	}
	for _, p := range []string{string(d), d.StatePath(), d.ProjectsPath()} {
		st, err := os.Stat(p)
		if err != nil || !st.IsDir() || st.Mode().Perm()&0o077 != 0 {
			t.Errorf("%s: %v %v", p, st, err)
		}
	}
}
