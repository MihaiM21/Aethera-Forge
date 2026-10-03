using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Aethera.Infrastructure.Agents.Pki;

/// <summary>
/// The identity an agent proves with its client certificate (ADR 0002): the server id in the SAN URI
/// <c>spiffe://aethera/server/&lt;server_id&gt;</c>. It is the only identity used for authorization; a <c>server_id</c> claimed in a message body
/// must equal it.
/// </summary>
public sealed record AgentIdentity(Guid ServerId, string Serial, string FingerprintSha256, DateTimeOffset NotBefore, DateTimeOffset NotAfter)
{
    public const string SpiffePrefix = "spiffe://aethera/server/";

    public static string SubjectUri(Guid serverId) => SpiffePrefix + serverId.ToString("D");

    /// <summary>
    /// Reads the identity from a certificate: exactly one SAN URI of the SPIFFE form, and a <c>CN</c> equal to the same server id.
    /// Does not validate trust, dates or revocation: that is the CA's and the interceptor's job.
    /// </summary>
    public static bool TryFromCertificate(X509Certificate2 certificate, out AgentIdentity identity)
    {
        identity = null!;
        Guid? fromUri = null;
        foreach (var extension in certificate.Extensions)
        {
            if (extension.Oid?.Value != "2.5.29.17") continue; // subjectAltName
            if (!TryReadUris(extension.RawData, out var uris)) return false;
            foreach (var uri in uris)
            {
                if (!uri.StartsWith(SpiffePrefix, StringComparison.Ordinal)) return false; // foreign URI SANs are not accepted
                if (fromUri is not null) return false;
                if (!Guid.TryParseExact(uri.AsSpan(SpiffePrefix.Length), "D", out var id)) return false;
                fromUri = id;
            }
        }

        if (fromUri is not { } serverId) return false;
        var cn = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        if (!Guid.TryParseExact(cn, "D", out var fromCn) || fromCn != serverId) return false;

        identity = new AgentIdentity(
            serverId, certificate.SerialNumber, Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant(),
            certificate.NotBefore.ToUniversalTime(), certificate.NotAfter.ToUniversalTime());
        return true;
    }

    // SubjectAltName ::= SEQUENCE OF GeneralName; uniformResourceIdentifier is [6] IMPLICIT IA5String.
    private static bool TryReadUris(ReadOnlyMemory<byte> raw, out List<string> uris)
    {
        uris = [];
        try
        {
            var names = new AsnReader(raw, AsnEncodingRules.DER).ReadSequence();
            var uriTag = new Asn1Tag(TagClass.ContextSpecific, 6);
            while (names.HasData)
            {
                if (names.PeekTag() == uriTag) uris.Add(names.ReadCharacterString(UniversalTagNumber.IA5String, uriTag));
                else names.ReadEncodedValue();
            }

            return true;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }
}
