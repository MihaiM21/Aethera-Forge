package policy

import (
	"context"
	"errors"
	"net/netip"
	"path/filepath"
	"strings"
	"testing"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/config"
)

func execPolicy(t *testing.T) *Policy {
	t.Helper()
	cfg := config.Default()
	cfg.StateDir = t.TempDir()
	cfg.Finalize()
	p := New(cfg)
	p.SetSelfContainer("deadbeef0000")
	p.SetLookup(func(_ context.Context, host string) ([]netip.Addr, error) {
		switch host {
		case "minio":
			return []netip.Addr{netip.MustParseAddr("172.18.0.5")}, nil
		case "public.example":
			return []netip.Addr{netip.MustParseAddr("93.184.216.34")}, nil
		}
		return nil, errors.New("no such host")
	})
	return p
}

func managed(name, id string) *agentv1.ContainerInfo {
	return &agentv1.ContainerInfo{Id: id, Name: name, Labels: map[string]string{LabelManaged: "true", LabelWorkload: "w1"}}
}

func wantCode(t *testing.T, err error, code agentv1.ErrorCode) {
	t.Helper()
	v, ok := AsViolation(err)
	if !ok || v.Code != code {
		t.Fatalf("err = %v, want code %v", err, code)
	}
}

func TestCheckExecTarget(t *testing.T) {
	p := execPolicy(t)
	if err := p.CheckExecTarget(managed("web-1", "abc123")); err != nil {
		t.Fatalf("managed workload refused: %v", err)
	}
	cases := map[string]*agentv1.ContainerInfo{
		"no labels":        {Id: "1", Name: "x"},
		"not managed":      {Id: "1", Name: "x", Labels: map[string]string{LabelWorkload: "w"}},
		"managed false":    {Id: "1", Name: "x", Labels: map[string]string{LabelManaged: "false", LabelWorkload: "w"}},
		"no workload":      {Id: "1", Name: "x", Labels: map[string]string{LabelManaged: "true"}},
		"proxy":            managed("aethera-traefik", "2"),
		"proxy with slash": managed("/aethera-traefik", "2"),
		"agent itself":     managed("agent", "deadbeef0000cafe"),
	}
	for name, c := range cases {
		wantCode(t, p.CheckExecTarget(c), agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION)
		_ = name
	}
}

func TestCheckExecArgs(t *testing.T) {
	ok := []*agentv1.EnvVar{{Name: "PGPASSWORD"}}
	if err := CheckExecArgs("db", []string{"pg_dump", "-Fc"}, ok); err != nil {
		t.Fatal(err)
	}
	bad := []struct {
		container string
		argv      []string
		env       []*agentv1.EnvVar
	}{
		{"", []string{"x"}, nil},
		{"../db", []string{"x"}, nil},
		{"db", nil, nil},
		{"db", []string{""}, nil},
		{"db", []string{"a\x00b"}, nil},
		{"db", []string{"x"}, []*agentv1.EnvVar{{Name: "A=B"}}},
		{"db", []string{"x"}, []*agentv1.EnvVar{{Name: ""}}},
	}
	for i, c := range bad {
		wantCode(t, CheckExecArgs(c.container, c.argv, c.env), agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT)
		_ = i
	}
}

func TestCheckBackupVolume(t *testing.T) {
	for _, n := range []string{"data", "app_db-1", "Pgdata"} {
		if err := CheckBackupVolume(n); err != nil {
			t.Fatalf("%s: %v", n, err)
		}
	}
	for _, n := range []string{"aethera_pgdata", "aethera_data", "aethera-traefik-acme", "Aethera_X", "aethera-anything"} {
		wantCode(t, CheckBackupVolume(n), agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION)
	}
	for _, n := range []string{"", "/etc", "/var/lib/data", "a/b", "../x", "-x"} {
		wantCode(t, CheckBackupVolume(n), agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT)
	}
}

func TestResolveLocalPath(t *testing.T) {
	p := execPolicy(t)
	got, err := p.ResolveLocalPath("org/web/20261023.pgdump.aeb")
	if err != nil {
		t.Fatal(err)
	}
	want := filepath.Join(p.BackupsDir(), "org", "web", "20261023.pgdump.aeb")
	if got != want {
		t.Fatalf("got %s want %s", got, want)
	}
	for _, bad := range []string{"", "/etc/passwd", "../x", "a/../../x", "a/..", "..", ".", "a\\b", "C:\\x", "C:/x", "a\nb"} {
		if _, err := p.ResolveLocalPath(bad); err == nil {
			t.Fatalf("%q accepted", bad)
		}
	}
	// A cleaned path that would equal the directory itself is refused too.
	if _, err := p.ResolveLocalPath("a/.."); err == nil {
		t.Fatal("a/.. accepted")
	}
}

func TestCheckEndpoint(t *testing.T) {
	p := execPolicy(t)
	ctx := context.Background()
	ok := []string{
		"https://s3.eu-central-1.amazonaws.com", "https://acct.r2.cloudflarestorage.com", "https://93.184.216.34:9000",
		"http://127.0.0.1:9101", "http://localhost:9000", "http://[::1]:9000", "http://10.0.0.5:9000", "http://192.168.1.20", "http://minio:9000",
		"https://minio.example.com/",
	}
	for _, e := range ok {
		if err := CheckEndpoint(ctx, e, p.lookup); err != nil {
			t.Fatalf("%s refused: %v", e, err)
		}
	}
	bad := map[string]agentv1.ErrorCode{
		"http://s3.amazonaws.com": agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT, // does not resolve in the test
		"http://public.example":   agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION,
		"http://93.184.216.34":    agentv1.ErrorCode_ERROR_CODE_POLICY_VIOLATION,
		"ftp://127.0.0.1":         agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT,
		"127.0.0.1:9000":          agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT,
		"":                        agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT,
		"https://":                agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT,
		"https://user:pw@host":    agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT,
		"https://host/bucket":     agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT,
		"https://host?x=1":        agentv1.ErrorCode_ERROR_CODE_INVALID_ARGUMENT,
	}
	for e, code := range bad {
		wantCode(t, CheckEndpoint(ctx, e, p.lookup), code)
	}
}

func s3Loc() *agentv1.S3Location {
	return &agentv1.S3Location{
		Endpoint: "https://s3.example.com", Bucket: "bk", Key: "aethera/org/w/x.pgdump.aeb", AccessKeyId: "AKIA",
		SecretAccessKey: &agentv1.SecretValue{Value: "s3cr3t-value"},
	}
}

func TestCheckS3(t *testing.T) {
	p := execPolicy(t)
	if err := p.CheckS3(context.Background(), s3Loc()); err != nil {
		t.Fatal(err)
	}
	mut := map[string]func(*agentv1.S3Location){
		"bucket":   func(s *agentv1.S3Location) { s.Bucket = "a/b" },
		"nobucket": func(s *agentv1.S3Location) { s.Bucket = "" },
		"key":      func(s *agentv1.S3Location) { s.Key = "" },
		"slash":    func(s *agentv1.S3Location) { s.Key = "/abs" },
		"creds":    func(s *agentv1.S3Location) { s.SecretAccessKey = nil },
		"akid":     func(s *agentv1.S3Location) { s.AccessKeyId = "" },
		"endpoint": func(s *agentv1.S3Location) { s.Endpoint = "http://public.example" },
	}
	for name, f := range mut {
		l := s3Loc()
		f(l)
		if err := p.CheckS3(context.Background(), l); err == nil {
			t.Fatalf("%s accepted", name)
		}
	}
}

func cmdOf(r any) *agentv1.Command {
	c := &agentv1.Command{CommandId: "c1"}
	switch v := r.(type) {
	case *agentv1.ContainerExec:
		c.Request = &agentv1.Command_ContainerExec{ContainerExec: v}
	case *agentv1.BackupRun:
		c.Request = &agentv1.Command_BackupRun{BackupRun: v}
	case *agentv1.BackupRestore:
		c.Request = &agentv1.Command_BackupRestore{BackupRestore: v}
	case *agentv1.BackupDelete:
		c.Request = &agentv1.Command_BackupDelete{BackupDelete: v}
	}
	return c
}

func TestCheckCommandExecAndBackup(t *testing.T) {
	p := execPolicy(t)
	ctx := context.Background()
	execSrc := &agentv1.BackupSource{Kind: &agentv1.BackupSource_Exec{Exec: &agentv1.ExecDump{Container: "db", Argv: []string{"pg_dump"}}}}
	volSrc := func(v string) *agentv1.BackupSource {
		return &agentv1.BackupSource{Kind: &agentv1.BackupSource_Volume{Volume: &agentv1.VolumeArchive{Volume: v}}}
	}
	s3 := &agentv1.BackupLocation{Kind: &agentv1.BackupLocation_S3{S3: s3Loc()}}
	local := func(path string) *agentv1.BackupLocation {
		return &agentv1.BackupLocation{Kind: &agentv1.BackupLocation_Local{Local: &agentv1.LocalLocation{Path: path}}}
	}

	good := []*agentv1.Command{
		cmdOf(&agentv1.ContainerExec{Container: "web", Argv: []string{"sh", "-c", "date"}}),
		cmdOf(&agentv1.BackupRun{Source: execSrc, Target: s3}),
		cmdOf(&agentv1.BackupRun{Source: volSrc("data"), Target: local("a/b.aeb")}),
		cmdOf(&agentv1.BackupRestore{Source: s3, Sink: &agentv1.BackupSink{Kind: &agentv1.BackupSink_Volume{Volume: &agentv1.VolumeRestore{Volume: "data"}}}}),
		cmdOf(&agentv1.BackupDelete{Locations: []*agentv1.BackupLocation{s3, local("a/b.aeb")}}),
	}
	for i, c := range good {
		if err := p.CheckCommand(ctx, c); err != nil {
			t.Fatalf("good[%d]: %v", i, err)
		}
	}
	bad := []*agentv1.Command{
		cmdOf(&agentv1.ContainerExec{Container: "web"}),
		cmdOf(&agentv1.BackupRun{Source: volSrc("aethera_data"), Target: s3}),
		cmdOf(&agentv1.BackupRun{Source: volSrc("/etc"), Target: s3}),
		cmdOf(&agentv1.BackupRun{Source: execSrc, Target: local("../escape")}),
		cmdOf(&agentv1.BackupRun{Source: execSrc, Target: local("/abs")}),
		cmdOf(&agentv1.BackupRun{Source: execSrc}),
		cmdOf(&agentv1.BackupRun{Target: s3}),
		cmdOf(&agentv1.BackupRestore{Source: s3, Sink: &agentv1.BackupSink{Kind: &agentv1.BackupSink_Volume{Volume: &agentv1.VolumeRestore{Volume: "aethera-traefik-acme"}}}}),
		cmdOf(&agentv1.BackupRestore{Source: s3}),
		cmdOf(&agentv1.BackupDelete{Locations: []*agentv1.BackupLocation{local("../x")}}),
	}
	for i, c := range bad {
		if err := p.CheckCommand(ctx, c); err == nil {
			t.Fatalf("bad[%d] accepted", i)
		}
	}
	// A refused endpoint names the rule, not the credentials.
	l := s3Loc()
	l.Endpoint = "http://public.example"
	err := p.CheckCommand(ctx, cmdOf(&agentv1.BackupDelete{Locations: []*agentv1.BackupLocation{{Kind: &agentv1.BackupLocation_S3{S3: l}}}}))
	if err == nil || strings.Contains(err.Error(), "s3cr3t-value") {
		t.Fatalf("err = %v", err)
	}
}
