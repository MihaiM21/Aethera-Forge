package version

import "testing"

func TestString(t *testing.T) {
	if got, want := String(), "aethera-agent "+Version; got != want {
		t.Fatalf("String() = %q, want %q", got, want)
	}
}
