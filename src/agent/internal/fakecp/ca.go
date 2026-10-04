// Package fakecp is an in-process fake control plane for tests: a throw-away
// CA, an mTLS gRPC server implementing EnrollmentService and AgentService, and
// helpers to script a Connect session. It is not used by the agent binary.
package fakecp

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/pem"
	"errors"
	"math/big"
	"net"
	"net/url"
	"time"

	"github.com/mihaim21/aethera-forge/agent/internal/pki"
)

// CA is a test certificate authority.
type CA struct {
	Cert        *x509.Certificate
	Key         *ecdsa.PrivateKey
	PEM         []byte
	Fingerprint string
	now         func() time.Time
}

// NewCA creates a CA valid around now.
func NewCA(now func() time.Time) (*CA, error) {
	if now == nil {
		now = time.Now
	}
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return nil, err
	}
	tmpl := &x509.Certificate{
		SerialNumber:          serial(),
		Subject:               pkix.Name{CommonName: "Aethera Test CA"},
		NotBefore:             now().Add(-time.Hour),
		NotAfter:              now().Add(10 * 365 * 24 * time.Hour),
		IsCA:                  true,
		BasicConstraintsValid: true,
		KeyUsage:              x509.KeyUsageCertSign | x509.KeyUsageCRLSign,
	}
	der, err := x509.CreateCertificate(rand.Reader, tmpl, tmpl, &key.PublicKey, key)
	if err != nil {
		return nil, err
	}
	cert, err := x509.ParseCertificate(der)
	if err != nil {
		return nil, err
	}
	return &CA{
		Cert: cert, Key: key, now: now,
		PEM:         pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}),
		Fingerprint: pki.Fingerprint(der),
	}, nil
}

func serial() *big.Int {
	n, _ := rand.Int(rand.Reader, new(big.Int).Lsh(big.NewInt(1), 120))
	return n
}

// ServerCert issues a TLS server certificate for the given hosts (DNS names or
// IPs) and returns it with the CA appended to the chain.
func (ca *CA) ServerCert(hosts ...string) (tls.Certificate, error) {
	return ca.serverCert(false, hosts...)
}

// ServerCertEmbeddedCA is ServerCert the way the real control plane presents it:
// only the leaf goes on the wire, and the CA travels inside it (pki.EmbeddedCAOID).
func (ca *CA) ServerCertEmbeddedCA(hosts ...string) (tls.Certificate, error) {
	return ca.serverCert(true, hosts...)
}

func (ca *CA) serverCert(embed bool, hosts ...string) (tls.Certificate, error) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return tls.Certificate{}, err
	}
	tmpl := &x509.Certificate{
		SerialNumber: serial(),
		Subject:      pkix.Name{CommonName: "aethera-control-plane"},
		NotBefore:    ca.now().Add(-time.Hour),
		NotAfter:     ca.now().Add(90 * 24 * time.Hour),
		KeyUsage:     x509.KeyUsageDigitalSignature,
		ExtKeyUsage:  []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
	}
	for _, h := range hosts {
		if ip := net.ParseIP(h); ip != nil {
			tmpl.IPAddresses = append(tmpl.IPAddresses, ip)
		} else {
			tmpl.DNSNames = append(tmpl.DNSNames, h)
		}
	}
	if embed {
		tmpl.ExtraExtensions = []pkix.Extension{{Id: pki.EmbeddedCAOID, Value: ca.Cert.Raw}}
	}
	der, err := x509.CreateCertificate(rand.Reader, tmpl, ca.Cert, &key.PublicKey, ca.Key)
	if err != nil {
		return tls.Certificate{}, err
	}
	if embed {
		return tls.Certificate{Certificate: [][]byte{der}, PrivateKey: key}, nil
	}
	return tls.Certificate{Certificate: [][]byte{der, ca.Cert.Raw}, PrivateKey: key}, nil
}

// SignCSR issues a client certificate for serverID from a PEM CSR, ignoring
// the CSR's subject and SANs like the real control plane does.
func (ca *CA) SignCSR(csrPEM, serverID string, validity time.Duration) (string, time.Time, error) {
	block, _ := pem.Decode([]byte(csrPEM))
	if block == nil {
		return "", time.Time{}, errors.New("no CSR")
	}
	csr, err := x509.ParseCertificateRequest(block.Bytes)
	if err != nil {
		return "", time.Time{}, err
	}
	if err := csr.CheckSignature(); err != nil {
		return "", time.Time{}, err
	}
	uri, _ := url.Parse(pki.SPIFFEPrefix + serverID)
	notAfter := ca.now().Add(validity)
	tmpl := &x509.Certificate{
		SerialNumber: serial(),
		Subject:      pkix.Name{CommonName: serverID},
		URIs:         []*url.URL{uri},
		NotBefore:    ca.now().Add(-time.Minute),
		NotAfter:     notAfter,
		KeyUsage:     x509.KeyUsageDigitalSignature,
		ExtKeyUsage:  []x509.ExtKeyUsage{x509.ExtKeyUsageClientAuth},
	}
	der, err := x509.CreateCertificate(rand.Reader, tmpl, ca.Cert, csr.PublicKey, ca.Key)
	if err != nil {
		return "", time.Time{}, err
	}
	return string(pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der})), notAfter, nil
}
