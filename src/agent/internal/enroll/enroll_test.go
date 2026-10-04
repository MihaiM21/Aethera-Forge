package enroll_test

import (
	"context"
	"crypto/x509"
	"encoding/pem"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/mihaim21/aethera-forge/agent/internal/enroll"
	"github.com/mihaim21/aethera-forge/agent/internal/fakecp"
	"github.com/mihaim21/aethera-forge/agent/internal/pki"
	"github.com/mihaim21/aethera-forge/agent/internal/state"
)

const token = "aeth_join_TESTTOKEN_do_not_log"

func params(cp *fakecp.Server, dir state.Dir) enroll.Params {
	return enroll.Params{Endpoint: cp.Addr, Token: token, CASHA256: cp.CA.Fingerprint, Dir: dir, Version: "9.9.9", Timeout: 5 * time.Second}
}

func TestEnrollStoresIdentityKeyAndCertificates(t *testing.T) {
	cp, err := fakecp.Start(token, "srv-42")
	if err != nil {
		t.Fatal(err)
	}
	defer cp.Stop()
	dir := state.Dir(t.TempDir() + "/aethera")

	id, err := enroll.Enroll(context.Background(), params(cp, dir))
	if err != nil {
		t.Fatal(err)
	}
	if id.ServerID != "srv-42" || id.Endpoint != cp.Addr || id.CAFingerprint != cp.CA.Fingerprint {
		t.Fatalf("identity = %+v", id)
	}
	if id.RenewBefore != 10*24*time.Hour || time.Until(id.NotAfter) < 29*24*time.Hour {
		t.Fatalf("renewal data = %+v", id)
	}
	loaded, err := dir.LoadIdentity()
	if err != nil || loaded.ServerID != "srv-42" {
		t.Fatalf("persisted identity: %+v %v", loaded, err)
	}

	st, err := os.Stat(dir.KeyPath())
	if err != nil {
		t.Fatal(err)
	}
	if st.Mode().Perm() != 0o600 {
		t.Fatalf("private key mode = %v, want 0600", st.Mode().Perm())
	}
	keyPEM, _ := os.ReadFile(dir.KeyPath())
	if !strings.Contains(string(keyPEM), "EC PRIVATE KEY") {
		t.Fatal("key is not an EC key")
	}
	leaf, err := pki.ReadLeaf(dir.CertPath())
	if err != nil || leaf.ServerID != "srv-42" {
		t.Fatalf("leaf = %+v %v", leaf, err)
	}
	ca, _ := os.ReadFile(dir.CAPath())
	blk, _ := pem.Decode(ca)
	caCert, err := x509.ParseCertificate(blk.Bytes)
	if err != nil || pki.Fingerprint(caCert.Raw) != cp.CA.Fingerprint {
		t.Fatal("stored CA bundle does not match the pinned CA")
	}
	// The mTLS client config built from the stored material works.
	if _, err := pki.MTLSConfig(id.Endpoint, dir.CertPath(), dir.KeyPath(), dir.CAPath(), false); err != nil {
		t.Fatal(err)
	}
}

func TestEnrollAcceptsFormattedFingerprints(t *testing.T) {
	cp, _ := fakecp.Start(token, "srv-1")
	defer cp.Stop()
	p := params(cp, state.Dir(t.TempDir()))
	var colon []string
	fp := strings.ToUpper(cp.CA.Fingerprint)
	for i := 0; i < len(fp); i += 2 {
		colon = append(colon, fp[i:i+2])
	}
	p.CASHA256 = "sha256:" + strings.Join(colon, ":")
	if _, err := enroll.Enroll(context.Background(), p); err != nil {
		t.Fatal(err)
	}
}

func TestEnrollRefusesWrongPinWithoutSendingTheToken(t *testing.T) {
	cp, _ := fakecp.Start(token, "srv-1")
	defer cp.Stop()
	p := params(cp, state.Dir(t.TempDir()))
	p.CASHA256 = strings.Repeat("ab", 32)
	_, err := enroll.Enroll(context.Background(), p)
	if err == nil {
		t.Fatal("enrolled against a CA that does not match the pin")
	}
	if strings.Contains(err.Error(), token) {
		t.Fatal("token leaked into error")
	}
	// The pin failure happens during the handshake, so the control plane never
	// saw the token and it is still usable with the right pin.
	if _, err := enroll.Enroll(context.Background(), params(cp, state.Dir(t.TempDir()))); err != nil {
		t.Fatalf("token was burnt by a failed pin check: %v", err)
	}
}

func TestEnrollRejectsBadInputs(t *testing.T) {
	cp, _ := fakecp.Start(token, "srv-1")
	defer cp.Stop()
	dir := state.Dir(t.TempDir())
	cases := map[string]func(*enroll.Params){
		"short pin":   func(p *enroll.Params) { p.CASHA256 = "abcd" },
		"non-hex":     func(p *enroll.Params) { p.CASHA256 = strings.Repeat("zz", 32) },
		"no token":    func(p *enroll.Params) { p.Token = "" },
		"no endpoint": func(p *enroll.Params) { p.Endpoint = "" },
	}
	for name, mutate := range cases {
		p := params(cp, dir)
		mutate(&p)
		if _, err := enroll.Enroll(context.Background(), p); err == nil {
			t.Errorf("%s: accepted", name)
		}
	}
	if _, err := dir.LoadIdentity(); err == nil {
		t.Fatal("a failed enrollment left an identity behind")
	}
}

func TestJoinTokenIsSingleUseAndErrorsDoNotRevealWhy(t *testing.T) {
	cp, _ := fakecp.Start(token, "srv-1")
	defer cp.Stop()
	if _, err := enroll.Enroll(context.Background(), params(cp, state.Dir(t.TempDir()))); err != nil {
		t.Fatal(err)
	}
	_, replay := enroll.Enroll(context.Background(), params(cp, state.Dir(t.TempDir())))
	p := params(cp, state.Dir(t.TempDir()))
	p.Token = "aeth_join_unknown"
	_, unknown := enroll.Enroll(context.Background(), p)
	if replay == nil || unknown == nil {
		t.Fatal("replayed or unknown token accepted")
	}
	if replay.Error() != unknown.Error() {
		t.Fatalf("errors differ (oracle): %q vs %q", replay, unknown)
	}
	if strings.Contains(replay.Error(), token) || strings.Contains(unknown.Error(), "aeth_join_unknown") {
		t.Fatal("token echoed in error")
	}
}

func TestRenewUsesNewKeyAndUpdatesIdentity(t *testing.T) {
	cp, _ := fakecp.Start(token, "srv-1")
	defer cp.Stop()
	dir := state.Dir(t.TempDir())
	before, err := enroll.Enroll(context.Background(), params(cp, dir))
	if err != nil {
		t.Fatal(err)
	}
	oldKey, _ := os.ReadFile(dir.KeyPath())
	oldLeaf, _ := pki.ReadLeaf(dir.CertPath())
	cp.Validity = 60 * 24 * time.Hour

	after, err := enroll.Renew(context.Background(), dir, false)
	if err != nil {
		t.Fatal(err)
	}
	newKey, _ := os.ReadFile(dir.KeyPath())
	newLeaf, _ := pki.ReadLeaf(dir.CertPath())
	if string(oldKey) == string(newKey) {
		t.Fatal("renewal must generate a new key pair")
	}
	if oldLeaf.Serial == newLeaf.Serial || !after.NotAfter.After(before.NotAfter) {
		t.Fatalf("certificate not renewed: %v -> %v", before.NotAfter, after.NotAfter)
	}
	if st, _ := os.Stat(dir.KeyPath()); st.Mode().Perm() != 0o600 {
		t.Fatalf("renewed key mode %v", st.Mode().Perm())
	}
	if cp.RenewCalls != 1 {
		t.Fatalf("renew calls = %d", cp.RenewCalls)
	}
}

func TestRenewWithoutIdentityFails(t *testing.T) {
	if _, err := enroll.Renew(context.Background(), state.Dir(t.TempDir()), false); err == nil {
		t.Fatal("renew without enrollment succeeded")
	}
}

func TestNeedsRenewal(t *testing.T) {
	now := time.Now()
	id := &state.Identity{NotAfter: now.Add(20 * 24 * time.Hour), RenewBefore: 10 * 24 * time.Hour}
	if enroll.NeedsRenewal(id, now) {
		t.Fatal("renewing too early")
	}
	if !enroll.NeedsRenewal(id, now.Add(11*24*time.Hour)) {
		t.Fatal("not renewing inside the window")
	}
	id.RenewBefore = 0 // default applies
	if !enroll.NeedsRenewal(&state.Identity{NotAfter: now.Add(5 * 24 * time.Hour)}, now) {
		t.Fatal("default window of 10 days not applied")
	}
}
