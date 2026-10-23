package compose

import (
	"reflect"
	"strings"
	"testing"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
)

func plain(name, v string) *agentv1.EnvVar {
	return &agentv1.EnvVar{Name: name, Value: &agentv1.EnvVar_Plain{Plain: v}}
}

func TestDotenvValueQuoting(t *testing.T) {
	cases := map[string]string{
		"simple":        "'simple'",
		"":              "''",
		"a$b":           "'a$b'",
		"${X}":          "'${X}'",
		"x #not":        "'x #not'",
		`say "hi"`:      `'say "hi"'`,
		`back\slash`:    `'back\slash'`,
		"two\nlines":    "'two\nlines'",
		"it's":          `"it's"`,
		`it's $5 "q" \`: `"it's \$5 \"q\" \\"`,
		"it's\nnew":     `"it's\nnew"`,
	}
	for in, want := range cases {
		if got := dotenvValue(in); got != want {
			t.Errorf("dotenvValue(%q) = %q, want %q", in, got, want)
		}
	}
}

func TestDotenvFile(t *testing.T) {
	got, err := dotenvFile([]*agentv1.EnvVar{plain("A", "1"), plain("B", "x $y"), {Name: "C", Value: &agentv1.EnvVar_Secret{Secret: &agentv1.SecretValue{Value: "s #3"}}}})
	if err != nil || got != "A='1'\nB='x $y'\nC='s #3'\n" {
		t.Fatalf("dotenvFile = %q, %v", got, err)
	}
	for _, v := range []*agentv1.EnvVar{plain("A B", "x"), plain("1A", "x"), plain("", "x"), plain("A", "x\x00y")} {
		if _, err := dotenvFile([]*agentv1.EnvVar{v}); err == nil {
			t.Errorf("%q accepted", v.GetName())
		}
	}
}

func TestBuildArgsGoThroughTheEnvironment(t *testing.T) {
	pairs, names, err := buildArgs([]*agentv1.EnvVar{plain("TOKEN", "s3cret value"), plain("TOKEN", "s3cret value"), plain("MODE", "prod")}, []*agentv1.EnvVar{plain("MODE", "prod"), plain("OTHER", "1")})
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(names, []string{"TOKEN", "MODE"}) || !reflect.DeepEqual(pairs, []string{"TOKEN=s3cret value", "MODE=prod"}) {
		t.Fatalf("pairs = %v names = %v", pairs, names)
	}
	// One environment serves interpolation and build arguments: the same name cannot mean two values.
	if _, _, err := buildArgs([]*agentv1.EnvVar{plain("MODE", "dev")}, []*agentv1.EnvVar{plain("MODE", "prod")}); err == nil || !strings.Contains(err.Error(), "MODE") {
		t.Errorf("conflicting values accepted: %v", err)
	}
	if _, _, err := buildArgs([]*agentv1.EnvVar{plain("a-b", "x")}, nil); err == nil {
		t.Error("bad name accepted")
	}
}
