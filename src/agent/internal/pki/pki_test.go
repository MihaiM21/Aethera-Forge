package pki_test

import (
	"crypto/tls"
	"crypto/x509"
	"errors"
	"fmt"
	"strings"
	"testing"
	"time"

	"github.com/mihaim21/aethera-forge/agent/internal/fakecp"
	"github.com/mihaim21/aethera-forge/agent/internal/pki"
)

func rawChain(c tls.Certificate) [][]byte { return c.Certificate }

func TestPinnedConfigAcceptsMatchingCAAndHost(t *testing.T) {
	ca, _ := fakecp.NewCA(nil)
	srv, _ := ca.ServerCert("127.0.0.1", "cp.example.com")
	cfg := pki.PinnedConfig("127.0.0.1:9443", ca.Fingerprint, nil)
	if cfg.MinVersion != tls.VersionTLS13 {
		t.Fatal("TLS 1.3 is mandatory")
	}
	if err := cfg.VerifyPeerCertificate(rawChain(srv), nil); err != nil {
		t.Fatal(err)
	}
	if err := pki.PinnedConfig("cp.example.com:443", ca.Fingerprint, nil).VerifyPeerCertificate(rawChain(srv), nil); err != nil {
		t.Fatal(err)
	}
}

// The real control plane sends only its leaf (a TLS stack drops a self-signed root) and embeds the CA in it.
func TestPinnedConfigAcceptsTheCAEmbeddedInTheLeaf(t *testing.T) {
	ca, _ := fakecp.NewCA(nil)
	other, _ := fakecp.NewCA(nil)
	srv, _ := ca.ServerCertEmbeddedCA("127.0.0.1")
	if len(srv.Certificate) != 1 {
		t.Fatal("test setup: only the leaf may be on the wire")
	}
	if err := pki.PinnedConfig("127.0.0.1:9443", ca.Fingerprint, nil).VerifyPeerCertificate(rawChain(srv), nil); err != nil {
		t.Fatal(err)
	}
	// An embedded CA that does not match the pin must not be trusted.
	if err := pki.PinnedConfig("127.0.0.1:9443", other.Fingerprint, nil).VerifyPeerCertificate(rawChain(srv), nil); err == nil || !strings.Contains(err.Error(), "pinned") {
		t.Fatalf("pin of another CA accepted: %v", err)
	}
	// A leaf that claims the pinned CA but was not signed by it must not verify.
	forged, _ := other.ServerCertEmbeddedCA("127.0.0.1")
	if err := pki.PinnedConfig("127.0.0.1:9443", other.Fingerprint, nil).VerifyPeerCertificate(rawChain(forged), nil); err != nil {
		t.Fatalf("test setup: %v", err)
	}
	forgedLeaf, _ := ca.ServerCertEmbeddedCA("127.0.0.1")
	forgedLeaf.Certificate[0] = rawChain(forged)[0]
	if err := pki.PinnedConfig("127.0.0.1:9443", ca.Fingerprint, nil).VerifyPeerCertificate(rawChain(forgedLeaf), nil); err == nil {
		t.Fatal("a leaf from another CA was accepted")
	}
}

func TestPinnedConfigRejects(t *testing.T) {
	ca, _ := fakecp.NewCA(nil)
	other, _ := fakecp.NewCA(nil)
	srv, _ := ca.ServerCert("127.0.0.1")
	foreign, _ := other.ServerCert("127.0.0.1")

	cases := map[string]struct {
		endpoint string
		pin      string
		chain    [][]byte
		now      func() time.Time
		msg      string
	}{
		"pin of another CA":  {"127.0.0.1:1", other.Fingerprint, rawChain(srv), nil, "pinned"},
		"cert from other CA": {"127.0.0.1:1", ca.Fingerprint, rawChain(foreign), nil, "pinned"},
		"wrong host":         {"10.1.1.1:1", ca.Fingerprint, rawChain(srv), nil, "rejected"},
		"expired":            {"127.0.0.1:1", ca.Fingerprint, rawChain(srv), func() time.Time { return time.Now().Add(400 * 24 * time.Hour) }, "rejected"},
		"leaf only (no CA)":  {"127.0.0.1:1", ca.Fingerprint, rawChain(srv)[:1], nil, "pinned"},
		"no certificate":     {"127.0.0.1:1", ca.Fingerprint, nil, nil, "no certificate"},
	}
	for name, c := range cases {
		err := pki.PinnedConfig(c.endpoint, c.pin, c.now).VerifyPeerCertificate(c.chain, nil)
		if err == nil || !strings.Contains(err.Error(), c.msg) {
			t.Errorf("%s: err = %v, want containing %q", name, err, c.msg)
		}
	}
}

func TestPinnedConfigNeverUsesSystemRoots(t *testing.T) {
	cfg := pki.PinnedConfig("127.0.0.1:1", strings.Repeat("a", 64), nil)
	if cfg.RootCAs != nil {
		t.Fatal("pinned config must not carry a root pool")
	}
}

func TestNormalizeFingerprint(t *testing.T) {
	good := strings.Repeat("ab", 32)
	for _, in := range []string{good, strings.ToUpper(good), "sha256:" + good, " " + good + " "} {
		if got, err := pki.NormalizeFingerprint(in); err != nil || got != good {
			t.Errorf("%q -> %q, %v", in, got, err)
		}
	}
	for _, in := range []string{"", "abcd", strings.Repeat("g", 64), good + "00"} {
		if _, err := pki.NormalizeFingerprint(in); err == nil {
			t.Errorf("%q accepted", in)
		}
	}
}

func TestKeyAndCSR(t *testing.T) {
	key, keyPEM, err := pki.GenerateKey()
	if err != nil || key.Curve.Params().Name != "P-256" {
		t.Fatalf("key: %v", err)
	}
	if !strings.Contains(string(keyPEM), "EC PRIVATE KEY") {
		t.Fatal("unexpected key PEM")
	}
	csrPEM, err := pki.CreateCSR(key, "host1")
	if err != nil {
		t.Fatal(err)
	}
	ca, _ := fakecp.NewCA(nil)
	certPEM, _, err := ca.SignCSR(string(csrPEM), "srv-7", time.Hour)
	if err != nil {
		t.Fatal(err)
	}
	certs, err := pki.ParseCertificates([]byte(certPEM))
	if err != nil {
		t.Fatal(err)
	}
	id, ok := pki.ServerIDFromCert(certs[0])
	if !ok || id != "srv-7" {
		t.Fatalf("server id = %q ok=%v", id, ok)
	}
	if _, err := pki.ParseCertificates([]byte("garbage")); err == nil {
		t.Fatal("garbage parsed")
	}
}

func TestIsTLSError(t *testing.T) {
	yes := []error{
		x509.UnknownAuthorityError{},
		fmt.Errorf("rpc error: code = Unavailable desc = connection error: desc = \"transport: authentication handshake failed: tls: failed to verify certificate: x509: certificate has expired\""),
		fmt.Errorf("remote error: tls: bad certificate"),
	}
	for _, e := range yes {
		if !pki.IsTLSError(e) {
			t.Errorf("not recognised: %v", e)
		}
	}
	for _, e := range []error{nil, errors.New("connection refused"), errors.New("EOF"),
		errors.New("rpc error: code = Unavailable desc = connection error: desc = \"transport: authentication handshake failed: EOF\""),
		errors.New("tls: first record does not look like a TLS handshake"), errors.New("read tcp: connection reset by peer")} {
		if pki.IsTLSError(e) {
			t.Errorf("false positive: %v", e)
		}
	}
}
