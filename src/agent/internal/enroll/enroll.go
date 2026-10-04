// Package enroll implements `aethera-agent enroll` and certificate renewal.
//
// Enrollment: generate an EC P-256 key on this host, send the CSR plus a
// one-time join token to EnrollmentService.Enroll over TLS that is authenticated
// only by the pinned CA fingerprint (no client certificate yet), then store the
// signed certificate, the CA bundle, the key (0600) and the identity.
package enroll

import (
	"context"
	"crypto/ecdsa"
	"crypto/x509"
	"errors"
	"fmt"
	"os"
	"time"

	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/credentials"
	"google.golang.org/grpc/status"

	agentv1 "github.com/mihaim21/aethera-forge/agent/gen/aethera/agent/v1"
	"github.com/mihaim21/aethera-forge/agent/internal/metrics"
	"github.com/mihaim21/aethera-forge/agent/internal/pki"
	"github.com/mihaim21/aethera-forge/agent/internal/state"
)

// ProtocolVersion is the wire protocol revision this agent speaks.
const ProtocolVersion = 1

// DefaultRenewBefore applies when the control plane does not say otherwise.
const DefaultRenewBefore = 10 * 24 * time.Hour

// Params are the inputs of Enroll.
type Params struct {
	Endpoint string
	Token    string // SECRET: never log
	CASHA256 string
	Name     string
	Dir      state.Dir
	Version  string
	Timeout  time.Duration
	// DialOptions are extra gRPC options (tests).
	DialOptions []grpc.DialOption
}

// Enroll performs the enrollment and persists the result.
func Enroll(ctx context.Context, p Params) (*state.Identity, error) {
	pin, err := pki.NormalizeFingerprint(p.CASHA256)
	if err != nil {
		return nil, err
	}
	if p.Endpoint == "" {
		return nil, errors.New("--endpoint is required")
	}
	if p.Token == "" {
		return nil, errors.New("a join token is required")
	}
	if err := p.Dir.Ensure(); err != nil {
		return nil, fmt.Errorf("prepare state dir: %w", err)
	}
	key, keyPEM, err := pki.GenerateKey()
	if err != nil {
		return nil, err
	}
	facts := metrics.HostFacts(ctx)
	csr, err := pki.CreateCSR(key, facts.GetHostname())
	if err != nil {
		return nil, err
	}

	timeout := p.Timeout
	if timeout <= 0 {
		timeout = 30 * time.Second
	}
	cctx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	opts := append([]grpc.DialOption{grpc.WithTransportCredentials(credentials.NewTLS(pki.PinnedConfig(p.Endpoint, pin, nil)))}, p.DialOptions...)
	conn, err := grpc.NewClient(p.Endpoint, opts...)
	if err != nil {
		return nil, fmt.Errorf("dial %s: %w", p.Endpoint, err)
	}
	defer conn.Close()

	resp, err := agentv1.NewEnrollmentServiceClient(conn).Enroll(cctx, &agentv1.EnrollRequest{
		JoinToken:       &agentv1.SecretValue{Value: p.Token},
		AgentVersion:    p.Version,
		ProtocolVersion: ProtocolVersion,
		CsrPem:          string(csr),
		Host:            facts,
		RequestedName:   p.Name,
	})
	if err != nil {
		return nil, describe(err, p.Endpoint)
	}
	leaf, caPEM, err := validate(resp.GetClientCertificatePem(), resp.GetCaChainPem(), pin, key, resp.GetServerId())
	if err != nil {
		return nil, fmt.Errorf("enrollment response rejected: %w", err)
	}
	if fp := resp.GetCaFingerprintSha256(); fp != "" {
		if n, nerr := pki.NormalizeFingerprint(fp); nerr != nil || n != pin {
			return nil, errors.New("enrollment response rejected: CA fingerprint differs from --ca-sha256")
		}
	}

	endpoint := resp.GetControlPlaneEndpoint()
	if endpoint == "" {
		endpoint = p.Endpoint
	}
	renew := resp.GetRenewBefore().AsDuration()
	if renew <= 0 {
		renew = DefaultRenewBefore
	}
	id := &state.Identity{
		ServerID: resp.GetServerId(), Endpoint: endpoint, CAFingerprint: pin,
		NotAfter: leaf.NotAfter, RenewBefore: renew,
	}
	if err := persist(p.Dir, keyPEM, []byte(resp.GetClientCertificatePem()), caPEM, id); err != nil {
		return nil, err
	}
	return id, nil
}

func describe(err error, endpoint string) error {
	if st, ok := status.FromError(err); ok {
		switch st.Code() {
		case codes.PermissionDenied:
			return errors.New("join token rejected: it is unknown, expired or already used - create a new one in the UI")
		case codes.ResourceExhausted:
			return errors.New("too many enrollment attempts: wait a minute and retry")
		case codes.Unavailable:
			return fmt.Errorf("cannot reach %s (or the CA fingerprint does not match): %s", endpoint, st.Message())
		}
		return fmt.Errorf("enrollment failed: %s: %s", st.Code(), st.Message())
	}
	return fmt.Errorf("enrollment failed: %w", err)
}

// validate checks the issued certificate against our key and the CA bundle,
// and that the CA bundle contains the pinned root.
func validate(certPEM, caPEM, pin string, key *ecdsa.PrivateKey, serverID string) (*x509.Certificate, []byte, error) {
	certs, err := pki.ParseCertificates([]byte(certPEM))
	if err != nil {
		return nil, nil, fmt.Errorf("client certificate: %w", err)
	}
	leaf := certs[0]
	cas, err := pki.ParseCertificates([]byte(caPEM))
	if err != nil {
		return nil, nil, fmt.Errorf("CA bundle: %w", err)
	}
	pinned := false
	roots := x509.NewCertPool()
	for _, c := range cas {
		roots.AddCert(c)
		if pki.Fingerprint(c.Raw) == pin {
			pinned = true
		}
	}
	if !pinned {
		return nil, nil, errors.New("CA bundle does not contain the pinned CA")
	}
	if pub, ok := leaf.PublicKey.(*ecdsa.PublicKey); !ok || !pub.Equal(&key.PublicKey) {
		return nil, nil, errors.New("certificate does not match the generated key")
	}
	inter := x509.NewCertPool()
	for _, c := range certs[1:] {
		inter.AddCert(c)
	}
	if _, err := leaf.Verify(x509.VerifyOptions{
		Roots: roots, Intermediates: inter, KeyUsages: []x509.ExtKeyUsage{x509.ExtKeyUsageClientAuth},
	}); err != nil {
		return nil, nil, fmt.Errorf("certificate does not verify against the CA: %w", err)
	}
	id, ok := pki.ServerIDFromCert(leaf)
	if !ok || id != serverID || serverID == "" {
		return nil, nil, errors.New("certificate identity (SAN URI) does not match the assigned server id")
	}
	return leaf, []byte(caPEM), nil
}

func persist(dir state.Dir, keyPEM, certPEM, caPEM []byte, id *state.Identity) error {
	if err := state.WriteFileAtomic(dir.CAPath(), caPEM, 0o644); err != nil {
		return err
	}
	if err := state.WriteFileAtomic(dir.KeyPath(), keyPEM, 0o600); err != nil {
		return err
	}
	if err := state.WriteFileAtomic(dir.CertPath(), certPEM, 0o644); err != nil {
		return err
	}
	// Identity last: its presence marks a complete enrollment.
	return dir.SaveIdentity(id)
}

// NeedsRenewal reports whether the certificate is within its renewal window.
func NeedsRenewal(id *state.Identity, now time.Time) bool {
	rb := id.RenewBefore
	if rb <= 0 {
		rb = DefaultRenewBefore
	}
	return !now.Before(id.NotAfter.Add(-rb))
}

// Renew obtains a fresh certificate with a NEW key pair over the current mTLS
// identity, then replaces key, certificate and identity on disk.
func Renew(ctx context.Context, dir state.Dir, trustSystemRoots bool, dialOpts ...grpc.DialOption) (*state.Identity, error) {
	id, err := dir.LoadIdentity()
	if err != nil {
		return nil, err
	}
	tlsCfg, err := pki.MTLSConfig(id.Endpoint, dir.CertPath(), dir.KeyPath(), dir.CAPath(), trustSystemRoots)
	if err != nil {
		return nil, err
	}
	key, keyPEM, err := pki.GenerateKey()
	if err != nil {
		return nil, err
	}
	csr, err := pki.CreateCSR(key, "")
	if err != nil {
		return nil, err
	}
	conn, err := grpc.NewClient(id.Endpoint, append([]grpc.DialOption{grpc.WithTransportCredentials(credentials.NewTLS(tlsCfg))}, dialOpts...)...)
	if err != nil {
		return nil, err
	}
	defer conn.Close()
	cctx, cancel := context.WithTimeout(ctx, 30*time.Second)
	defer cancel()
	resp, err := agentv1.NewAgentServiceClient(conn).RenewCertificate(cctx, &agentv1.RenewCertificateRequest{CsrPem: string(csr)})
	if err != nil {
		return nil, fmt.Errorf("renew certificate: %w", err)
	}
	caPEM := resp.GetCaChainPem()
	if caPEM == "" {
		b, _ := os.ReadFile(dir.CAPath())
		caPEM = string(b)
	}
	leaf, _, err := validate(resp.GetClientCertificatePem(), caPEM, id.CAFingerprint, key, id.ServerID)
	if err != nil {
		return nil, fmt.Errorf("renewal response rejected: %w", err)
	}
	next := *id
	next.NotAfter = leaf.NotAfter
	if err := persist(dir, keyPEM, []byte(resp.GetClientCertificatePem()), []byte(caPEM), &next); err != nil {
		return nil, err
	}
	return &next, nil
}
