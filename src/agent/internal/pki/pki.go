// Package pki holds the agent's key, CSR and TLS helpers.
//
// Trust rules (ADR 0002): TLS 1.3 only; the control plane is authenticated
// against the Aethera CA the agent received at enrollment (never the OS trust
// store unless trust_system_roots is set); at first contact the CA is pinned by
// SHA-256 fingerprint.
package pki

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/asn1"
	"encoding/hex"
	"encoding/pem"
	"errors"
	"fmt"
	"net"
	"os"
	"strings"
	"time"
)

// EmbeddedCAOID names the non-critical extension of the control plane's listener
// certificate that carries the DER of the CA certificate. A TLS server strips a
// self-signed root from the chain it sends, but at first contact the agent has
// only the CA fingerprint, so the CA has to travel inside the leaf. The pin
// keeps this safe: the embedded certificate is used only if its SHA-256 equals
// the pin, and the leaf must still verify against it.
var EmbeddedCAOID = asn1.ObjectIdentifier{1, 3, 6, 1, 3, 54173, 1}

// SPIFFEPrefix is the SAN URI prefix of agent certificates.
const SPIFFEPrefix = "spiffe://aethera/server/"

// GenerateKey creates a fresh EC P-256 key and returns it with its PEM encoding.
func GenerateKey() (*ecdsa.PrivateKey, []byte, error) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return nil, nil, fmt.Errorf("generate key: %w", err)
	}
	der, err := x509.MarshalECPrivateKey(key)
	if err != nil {
		return nil, nil, fmt.Errorf("marshal key: %w", err)
	}
	return key, pem.EncodeToMemory(&pem.Block{Type: "EC PRIVATE KEY", Bytes: der}), nil
}

// CreateCSR builds a PEM PKCS#10 CSR for key. The control plane ignores the
// subject and SANs and substitutes its own, so only a hint is placed in it.
func CreateCSR(key *ecdsa.PrivateKey, hostname string) ([]byte, error) {
	tmpl := &x509.CertificateRequest{
		Subject:            pkix.Name{CommonName: "aethera-agent"},
		SignatureAlgorithm: x509.ECDSAWithSHA256,
	}
	if hostname != "" {
		tmpl.DNSNames = []string{hostname}
	}
	der, err := x509.CreateCertificateRequest(rand.Reader, tmpl, key)
	if err != nil {
		return nil, fmt.Errorf("create csr: %w", err)
	}
	return pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE REQUEST", Bytes: der}), nil
}

// Fingerprint returns the lower-case hex SHA-256 of a DER certificate.
func Fingerprint(der []byte) string {
	sum := sha256.Sum256(der)
	return hex.EncodeToString(sum[:])
}

// NormalizeFingerprint lower-cases and strips separators from a user supplied
// fingerprint (accepts "AB:CD:..." and "sha256:..." forms).
func NormalizeFingerprint(s string) (string, error) {
	s = strings.ToLower(strings.TrimSpace(s))
	s = strings.TrimPrefix(s, "sha256:")
	s = strings.NewReplacer(":", "", " ", "").Replace(s)
	if len(s) != 64 {
		return "", errors.New("ca fingerprint must be 64 hex characters (SHA-256)")
	}
	if _, err := hex.DecodeString(s); err != nil {
		return "", errors.New("ca fingerprint is not valid hex")
	}
	return s, nil
}

// ParseCertificates decodes every CERTIFICATE block in pemData.
func ParseCertificates(pemData []byte) ([]*x509.Certificate, error) {
	var certs []*x509.Certificate
	rest := pemData
	for {
		var block *pem.Block
		block, rest = pem.Decode(rest)
		if block == nil {
			break
		}
		if block.Type != "CERTIFICATE" {
			continue
		}
		c, err := x509.ParseCertificate(block.Bytes)
		if err != nil {
			return nil, fmt.Errorf("parse certificate: %w", err)
		}
		certs = append(certs, c)
	}
	if len(certs) == 0 {
		return nil, errors.New("no certificate found in PEM data")
	}
	return certs, nil
}

// ServerIDFromCert extracts the server id from the SAN URI.
func ServerIDFromCert(c *x509.Certificate) (string, bool) {
	for _, u := range c.URIs {
		if s := u.String(); strings.HasPrefix(s, SPIFFEPrefix) {
			return strings.TrimPrefix(s, SPIFFEPrefix), true
		}
	}
	return "", false
}

// hostOf strips the port from host:port.
func hostOf(endpoint string) string {
	if h, _, err := net.SplitHostPort(endpoint); err == nil {
		return h
	}
	return endpoint
}

// PinnedConfig returns a server-auth-only TLS config for the enrollment call.
// The presented chain must contain a certificate whose SHA-256 equals pin, the
// leaf must verify against exactly that certificate, and the leaf must be valid
// for the endpoint host. System roots are never consulted.
func PinnedConfig(endpoint, pin string, now func() time.Time) *tls.Config {
	host := hostOf(endpoint)
	if now == nil {
		now = time.Now
	}
	return &tls.Config{
		MinVersion: tls.VersionTLS13,
		ServerName: host,
		// Standard verification is replaced by the pin check below.
		InsecureSkipVerify: true, //nolint:gosec // verified in VerifyPeerCertificate
		VerifyPeerCertificate: func(raw [][]byte, _ [][]*x509.Certificate) error {
			return verifyPinned(raw, pin, host, now())
		},
	}
}

func verifyPinned(raw [][]byte, pin, host string, now time.Time) error {
	if len(raw) == 0 {
		return errors.New("server presented no certificate")
	}
	certs := make([]*x509.Certificate, 0, len(raw))
	for _, r := range raw {
		c, err := x509.ParseCertificate(r)
		if err != nil {
			return fmt.Errorf("parse server certificate: %w", err)
		}
		certs = append(certs, c)
	}
	var anchor *x509.Certificate
	for _, c := range certs {
		if Fingerprint(c.Raw) == pin {
			anchor = c
			break
		}
	}
	if anchor == nil {
		if c := embeddedCA(certs[0]); c != nil && Fingerprint(c.Raw) == pin {
			anchor = c
		}
	}
	if anchor == nil {
		return errors.New("control plane CA does not match the pinned --ca-sha256 fingerprint")
	}
	roots := x509.NewCertPool()
	roots.AddCert(anchor)
	inter := x509.NewCertPool()
	for _, c := range certs[1:] {
		inter.AddCert(c)
	}
	_, err := certs[0].Verify(x509.VerifyOptions{
		Roots:         roots,
		Intermediates: inter,
		DNSName:       host,
		CurrentTime:   now,
		KeyUsages:     []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
	})
	if err != nil {
		return fmt.Errorf("control plane certificate rejected: %w", err)
	}
	return nil
}

// embeddedCA returns the CA certificate carried in the leaf's EmbeddedCAOID extension.
func embeddedCA(leaf *x509.Certificate) *x509.Certificate {
	for _, ext := range leaf.Extensions {
		if ext.Id.Equal(EmbeddedCAOID) {
			if c, err := x509.ParseCertificate(ext.Value); err == nil {
				return c
			}
		}
	}
	return nil
}

// MTLSConfig builds the runtime client config: client cert from certPEM/keyPEM
// files, trust restricted to the CA bundle (plus system roots when asked).
func MTLSConfig(endpoint, certPath, keyPath, caPath string, trustSystemRoots bool) (*tls.Config, error) {
	cert, err := tls.LoadX509KeyPair(certPath, keyPath)
	if err != nil {
		return nil, fmt.Errorf("load client certificate: %w", err)
	}
	caPEM, err := os.ReadFile(caPath)
	if err != nil {
		return nil, fmt.Errorf("read CA bundle: %w", err)
	}
	var pool *x509.CertPool
	if trustSystemRoots {
		if pool, err = x509.SystemCertPool(); err != nil || pool == nil {
			pool = x509.NewCertPool()
		}
	} else {
		pool = x509.NewCertPool()
	}
	if !pool.AppendCertsFromPEM(caPEM) {
		return nil, errors.New("CA bundle contains no certificates")
	}
	return &tls.Config{
		MinVersion:   tls.VersionTLS13,
		Certificates: []tls.Certificate{cert},
		RootCAs:      pool,
		ServerName:   hostOf(endpoint),
	}, nil
}

// IsTLSError reports whether err looks like a certificate or handshake
// failure (retried slowly with a clear log line rather than in a hot loop).
func IsTLSError(err error) bool {
	if err == nil {
		return false
	}
	var ua x509.UnknownAuthorityError
	var ci x509.CertificateInvalidError
	var hn x509.HostnameError
	if errors.As(err, &ua) || errors.As(err, &ci) || errors.As(err, &hn) {
		return true
	}
	msg := strings.ToLower(err.Error())
	// Only real certificate problems qualify. A handshake that merely dies (EOF,
	// connection reset: a control plane that is starting or stopping) must keep
	// the fast reconnect backoff, not the 5 minute certificate retry.
	for _, s := range []string{"x509:", "failed to verify certificate", "bad certificate", "certificate expired", "expired certificate", "unknown certificate", "certificate required", "certificate revoked"} {
		if strings.Contains(msg, s) {
			return true
		}
	}
	return false
}

// LeafInfo is the part of a client certificate the agent cares about.
type LeafInfo struct {
	Serial   string
	NotAfter time.Time
	ServerID string
}

// ReadLeaf parses the first certificate of certPath.
func ReadLeaf(certPath string) (*LeafInfo, error) {
	data, err := os.ReadFile(certPath)
	if err != nil {
		return nil, err
	}
	certs, err := ParseCertificates(data)
	if err != nil {
		return nil, err
	}
	id, _ := ServerIDFromCert(certs[0])
	return &LeafInfo{Serial: certs[0].SerialNumber.Text(16), NotAfter: certs[0].NotAfter, ServerID: id}, nil
}
