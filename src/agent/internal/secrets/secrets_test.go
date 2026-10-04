package secrets

import (
	"strings"
	"testing"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
)

func TestCollectFindsSecretsByType(t *testing.T) {
	cmd := &agentv1.Command{
		CommandId: "not-a-secret-token-name", // field names are irrelevant: only SecretValue counts
		Request: &agentv1.Command_ContainerCreate{ContainerCreate: &agentv1.ContainerCreate{
			Spec: &agentv1.ContainerSpec{
				Image: "x",
				Env: []*agentv1.EnvVar{
					{Name: "PLAIN", Value: &agentv1.EnvVar_Plain{Plain: "visible"}},
					{Name: "DB_PASSWORD", Value: &agentv1.EnvVar_Secret{Secret: &agentv1.SecretValue{Value: "s3cr3t-db-pass"}}},
				},
			},
			PullAuth: &agentv1.RegistryAuth{
				Server: "ghcr.io", Username: "bot",
				Password:      &agentv1.SecretValue{Value: "registry-pass-1"},
				IdentityToken: &agentv1.SecretValue{Value: "id-token-xyz"},
			},
		}},
	}
	got := Collect(cmd)
	want := map[string]bool{"s3cr3t-db-pass": true, "registry-pass-1": true, "id-token-xyz": true}
	if len(got) != len(want) {
		t.Fatalf("collected %v", got)
	}
	for _, g := range got {
		if !want[g] {
			t.Errorf("unexpected value %q", g)
		}
	}
}

func TestCollectWalksListsAndMaps(t *testing.T) {
	cmd := &agentv1.Command{Request: &agentv1.Command_ComposeUp{ComposeUp: &agentv1.ComposeUp{Project: &agentv1.ComposeProject{
		ProjectName: "p",
		Env:         []*agentv1.EnvVar{{Name: "K", Value: &agentv1.EnvVar_Secret{Secret: &agentv1.SecretValue{Value: "compose-secret"}}}},
		RegistryAuths: []*agentv1.RegistryAuth{
			{Server: "a", Password: &agentv1.SecretValue{Value: "pw-for-a"}},
			{Server: "b", Password: &agentv1.SecretValue{Value: "pw-for-b"}},
		},
	}}}}
	if got := Collect(cmd); len(got) != 3 {
		t.Fatalf("collected %v", got)
	}
}

func TestMaskerReplacesAndIgnoresTooShort(t *testing.T) {
	m := NewMasker("hunter2-long", "ab")
	if got := m.String("login hunter2-long ab ok"); got != "login *** ab ok" {
		t.Fatalf("got %q", got)
	}
	if NewMasker("", "xyz").Empty() == false {
		t.Fatal("empty/short secrets must not create needles")
	}
}

func TestMaskerLongestFirstAndMultiline(t *testing.T) {
	m := NewMasker("secretvalue", "secretvalue-extended")
	if got := m.String("a secretvalue-extended b"); got != "a *** b" {
		t.Fatalf("overlap masking got %q", got)
	}
	key := "-----BEGIN KEY-----\nAAAABBBBCCCC\n-----END KEY-----"
	km := NewMasker(key)
	line := "debug: AAAABBBBCCCC leaked"
	if got := km.String(line); strings.Contains(got, "AAAABBBBCCCC") {
		t.Fatalf("a single line of a multi-line secret leaked: %q", got)
	}
}

func TestMergeAndForMessage(t *testing.T) {
	a := NewMasker("first-secret")
	b := NewMasker("second-secret")
	m := Merge(a, b)
	if got := m.String("first-secret second-secret"); got != "*** ***" {
		t.Fatalf("got %q", got)
	}
	if Merge(nil, nil).Empty() == false {
		t.Fatal("merge of nothing must be empty")
	}
}
