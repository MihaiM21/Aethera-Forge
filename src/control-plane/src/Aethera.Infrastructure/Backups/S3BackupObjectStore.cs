using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Aethera.Domain.Transport;

namespace Aethera.Infrastructure.Backups;

/// <summary>An S3-compatible bucket and the credentials to reach it. The secret is a <see cref="SecretValue"/>, so a log line or exception never prints it.</summary>
public sealed record S3Target(string Endpoint, string? Region, string Bucket, string AccessKeyId, SecretValue SecretAccessKey, bool ForcePathStyle);

/// <summary>The result of <see cref="IBackupObjectStore.TestAsync"/>. <see cref="Error"/> is null on success and never contains credentials.</summary>
public sealed record StoreTestResult(bool Ok, string? Error);

/// <summary>
/// What the control plane does with a store itself (everything else is the agent's job): test a destination, upload Aethera's own database
/// dump, delete pruned objects (ADR 0010). A seam so API tests need no S3.
/// </summary>
public interface IBackupObjectStore
{
    /// <summary>Writes, reads back and deletes a small object.</summary>
    Task<StoreTestResult> TestAsync(S3Target target, string? pathPrefix, CancellationToken cancellationToken);

    Task UploadFileAsync(S3Target target, string key, string path, CancellationToken cancellationToken);

    /// <summary>Deletes the objects; a missing object is not an error.</summary>
    Task DeleteAsync(S3Target target, IReadOnlyCollection<string> keys, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IBackupObjectStore"/> over the AWS SDK. Works with AWS S3, Cloudflare R2, Backblaze B2 and MinIO: the endpoint is the service URL,
/// the region defaults to <c>auto</c> for R2 and <c>us-east-1</c> otherwise, and checksum headers are only sent where the API requires them
/// (R2 and several other stores reject the SDK's newer default checksums).
/// </summary>
public sealed class S3BackupObjectStore : IBackupObjectStore
{
    public static string DefaultRegion(string endpoint) =>
        endpoint.Contains("r2.cloudflarestorage.com", StringComparison.OrdinalIgnoreCase) ? "auto" : "us-east-1";

    public static AmazonS3Client CreateClient(S3Target target)
    {
        var region = string.IsNullOrWhiteSpace(target.Region) ? DefaultRegion(target.Endpoint) : target.Region.Trim();
        var config = new AmazonS3Config
        {
            ServiceURL = target.Endpoint,
            ForcePathStyle = target.ForcePathStyle,
            AuthenticationRegion = region,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            MaxErrorRetry = 2,
            Timeout = TimeSpan.FromMinutes(5),
        };
        return new AmazonS3Client(new BasicAWSCredentials(target.AccessKeyId, target.SecretAccessKey.Value), config);
    }

    public async Task<StoreTestResult> TestAsync(S3Target target, string? pathPrefix, CancellationToken cancellationToken)
    {
        var key = $"{(string.IsNullOrEmpty(pathPrefix) ? "" : pathPrefix.Trim('/') + "/")}.aethera-test/{Guid.NewGuid():N}.txt";
        var body = $"aethera connection test {DateTimeOffset.UtcNow:O}";
        try
        {
            using var client = CreateClient(target);
            await client.PutObjectAsync(new PutObjectRequest { BucketName = target.Bucket, Key = key, ContentBody = body, ContentType = "text/plain" }, cancellationToken);
            try
            {
                using var response = await client.GetObjectAsync(target.Bucket, key, cancellationToken);
                using var reader = new StreamReader(response.ResponseStream, Encoding.UTF8);
                if (await reader.ReadToEndAsync(cancellationToken) != body)
                    return new StoreTestResult(false, "The object that was read back differs from the one that was written.");
            }
            finally
            {
                await client.DeleteObjectAsync(target.Bucket, key, cancellationToken);
            }

            return new StoreTestResult(true, null);
        }
        catch (Exception ex) when (ex is AmazonServiceException or AmazonClientException or HttpRequestException or IOException or TaskCanceledException)
        {
            return new StoreTestResult(false, Describe(ex));
        }
    }

    public async Task UploadFileAsync(S3Target target, string key, string path, CancellationToken cancellationToken)
    {
        using var client = CreateClient(target);
        using var transfer = new TransferUtility(client);
        await transfer.UploadAsync(new TransferUtilityUploadRequest { BucketName = target.Bucket, Key = key, FilePath = path }, cancellationToken);
    }

    public async Task DeleteAsync(S3Target target, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        if (keys.Count == 0) return;
        using var client = CreateClient(target);
        foreach (var key in keys) await client.DeleteObjectAsync(target.Bucket, key, cancellationToken); // S3 answers 204 for a missing key
    }

    /// <summary>A short, credential-free explanation of what went wrong.</summary>
    internal static string Describe(Exception ex)
    {
        var text = ex switch
        {
            AmazonS3Exception s3 when s3.ErrorCode is "InvalidAccessKeyId" or "SignatureDoesNotMatch" => "The access key or secret was rejected by the store.",
            AmazonS3Exception s3 when s3.ErrorCode == "NoSuchBucket" => "The bucket does not exist.",
            AmazonS3Exception s3 when s3.ErrorCode == "AccessDenied" => "Access denied: the key may not read, write and delete objects in this bucket.",
            AmazonS3Exception s3 => $"The store answered {(int)s3.StatusCode} {s3.ErrorCode}: {s3.Message}",
            AmazonServiceException svc => $"The store answered {(int)svc.StatusCode}: {svc.Message}",
            HttpRequestException http => $"The store could not be reached: {http.Message}",
            TaskCanceledException => "The store did not answer in time.",
            _ => ex.Message,
        };
        return text.Length > 500 ? text[..500] : text;
    }
}
