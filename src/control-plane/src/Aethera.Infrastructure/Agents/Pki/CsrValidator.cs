using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Aethera.Infrastructure.Agents.Pki;

/// <summary>A CSR that failed validation. The message is safe to return to the caller (it never echoes the request).</summary>
public sealed class CsrValidationException(string message) : Exception(message);

/// <summary>A validated certificate signing request: only its public key is used.</summary>
public sealed record ValidatedCsr(PublicKey PublicKey, string KeyDescription);

/// <summary>
/// CSR handling of ADR 0002: verify the signature (proof of possession), check the key type (EC P-256/P-384, or RSA of at least 3072
/// bits) and use <b>nothing but the public key</b>. The CSR's subject, SANs and requested extensions are ignored: the CA substitutes its
/// own, so an agent cannot choose its identity.
/// </summary>
public static class CsrValidator
{
    public const int MaxCsrChars = 8192;
    public const int MinRsaBits = 3072;

    private const string P256Oid = "1.2.840.10045.3.1.7";
    private const string P384Oid = "1.3.132.0.34";
    private const string EcPublicKeyOid = "1.2.840.10045.2.1";
    private const string RsaOid = "1.2.840.113549.1.1.1";

    public static ValidatedCsr Validate(string? csrPem)
    {
        if (string.IsNullOrWhiteSpace(csrPem) || csrPem.Length > MaxCsrChars)
            throw new CsrValidationException("The certificate signing request is missing or too large.");

        CertificateRequest request;
        try
        {
            // Load() verifies the request's signature with its own public key, which proves the sender holds the private key.
            request = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
        {
            throw new CsrValidationException("The certificate signing request is not valid.");
        }

        var oid = request.PublicKey.Oid.Value;
        try
        {
            switch (oid)
            {
                case EcPublicKeyOid:
                {
                    using var ec = request.PublicKey.GetECDsaPublicKey() ?? throw new CsrValidationException("The certificate signing request is not valid.");
                    var curve = ec.ExportParameters(false).Curve.Oid;
                    var curveOid = curve.Value;
                    if (curveOid is not (P256Oid or P384Oid))
                    {
                        // Some platforms report a friendly name instead of the OID for named curves.
                        if (curve.FriendlyName is not ("nistP256" or "nistP384" or "ECDSA_P256" or "ECDSA_P384"))
                            throw new CsrValidationException("Only EC P-256, EC P-384 and RSA (3072 bits or more) keys are accepted.");
                    }

                    return new ValidatedCsr(request.PublicKey, "EC");
                }

                case RsaOid:
                {
                    using var rsa = request.PublicKey.GetRSAPublicKey() ?? throw new CsrValidationException("The certificate signing request is not valid.");
                    if (rsa.KeySize < MinRsaBits)
                        throw new CsrValidationException("Only EC P-256, EC P-384 and RSA (3072 bits or more) keys are accepted.");
                    return new ValidatedCsr(request.PublicKey, "RSA");
                }

                default:
                    throw new CsrValidationException("Only EC P-256, EC P-384 and RSA (3072 bits or more) keys are accepted.");
            }
        }
        catch (CryptographicException)
        {
            throw new CsrValidationException("The certificate signing request is not valid.");
        }
    }
}
