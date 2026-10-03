package main

import (
	"bytes"
	"strings"
	"testing"
)

func call(args ...string) (int, string, string) {
	var out, errOut bytes.Buffer
	code := run(args, strings.NewReader(""), &out, &errOut)
	return code, out.String(), errOut.String()
}

func TestRunVersionFlag(t *testing.T) {
	code, out, errOut := call("--version")
	if code != 0 {
		t.Fatalf("exit code = %d, want 0 (stderr: %s)", code, errOut)
	}
	if !strings.HasPrefix(out, "aethera-agent ") {
		t.Fatalf("unexpected output %q", out)
	}
}

func TestRunVersionSubcommand(t *testing.T) {
	code, out, _ := call("version")
	if code != 0 || !strings.HasPrefix(out, "aethera-agent ") {
		t.Fatalf("code=%d out=%q", code, out)
	}
}

func TestRunUnknownFlag(t *testing.T) {
	if code, _, _ := call("--nope"); code != 2 {
		t.Fatalf("exit code = %d, want 2", code)
	}
}

func TestRunWithoutArgsPrintsUsage(t *testing.T) {
	code, _, errOut := call()
	if code != 2 || !strings.Contains(errOut, "usage:") {
		t.Fatalf("code=%d stderr=%q", code, errOut)
	}
}

func TestEnrollRequiresArguments(t *testing.T) {
	t.Setenv("AETHERA_JOIN_TOKEN", "")
	code, _, errOut := call("enroll", "--endpoint", "cp.example:9443")
	if code != 2 || !strings.Contains(errOut, "--ca-sha256") {
		t.Fatalf("code=%d stderr=%q", code, errOut)
	}
}

func TestEnrollNeverEchoesToken(t *testing.T) {
	const secret = "aeth_join_super_secret_token_value"
	code, out, errOut := call("enroll", "--endpoint", "127.0.0.1:1", "--ca-sha256", "zz", "--token", secret, "--state-dir", t.TempDir())
	if code == 0 {
		t.Fatalf("enroll with a bogus pin must fail")
	}
	if strings.Contains(out+errOut, secret) {
		t.Fatalf("token leaked into output: %q", out+errOut)
	}
}

func TestRunNotEnrolled(t *testing.T) {
	code, _, errOut := call("run", "--config", "/nonexistent/agent.yaml", "--state-dir", t.TempDir())
	if code != 1 || !strings.Contains(errOut, "not enrolled") {
		t.Fatalf("code=%d stderr=%q", code, errOut)
	}
}
