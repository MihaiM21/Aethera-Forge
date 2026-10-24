using Amazon.S3;
using Amazon.S3.Model;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Backups;

namespace Aethera.Api.Tests.Backups;

/// <summary>
/// Runs the real AWS SDK against an S3-compatible server. Set <c>AETHERA_TEST_S3</c> to its endpoint (for MinIO:
/// <c>http://127.0.0.1:9102</c>); <c>AETHERA_TEST_S3_ACCESS_KEY</c> and <c>AETHERA_TEST_S3_SECRET_KEY</c> default to <c>minioadmin</c>.
/// </summary>
public sealed class RequiresS3FactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "AETHERA_TEST_S3";

    public RequiresS3FactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable)))
            Skip = $"{EnvironmentVariable} is not set; point it at an S3-compatible server (e.g. a MinIO on http://127.0.0.1:9102) to run the S3 tests.";
    }

    public static string Endpoint => Environment.GetEnvironmentVariable(EnvironmentVariable)!.TrimEnd('/');

    public static S3Target Target(string bucket, string? secret = null) => new(Endpoint, null, bucket,
        Environment.GetEnvironmentVariable("AETHERA_TEST_S3_ACCESS_KEY") ?? "minioadmin",
        new SecretValue(secret ?? Environment.GetEnvironmentVariable("AETHERA_TEST_S3_SECRET_KEY") ?? "minioadmin"), ForcePathStyle: true);
}

public sealed class BackupS3Tests
{
    private static async Task<string> NewBucketAsync()
    {
        var bucket = "aethera-test-" + Guid.NewGuid().ToString("N")[..12];
        using var client = S3BackupObjectStore.CreateClient(RequiresS3FactAttribute.Target(bucket));
        await client.PutBucketAsync(bucket);
        return bucket;
    }

    [RequiresS3Fact]
    public async Task The_connection_test_writes_reads_and_deletes_an_object()
    {
        var bucket = await NewBucketAsync();
        var target = RequiresS3FactAttribute.Target(bucket);

        var result = await new S3BackupObjectStore().TestAsync(target, "aethera", CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        using var client = S3BackupObjectStore.CreateClient(target);
        Assert.Empty((await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket })).S3Objects ?? []); // it cleans up after itself
    }

    [RequiresS3Fact]
    public async Task A_missing_bucket_and_a_wrong_secret_are_reported_without_leaking_credentials()
    {
        var store = new S3BackupObjectStore();
        var missing = await store.TestAsync(RequiresS3FactAttribute.Target("aethera-no-such-bucket-" + Guid.NewGuid().ToString("N")[..8]), null, CancellationToken.None);
        Assert.False(missing.Ok);
        Assert.Contains("bucket", missing.Error!, StringComparison.OrdinalIgnoreCase);

        var bucket = await NewBucketAsync();
        var wrong = await store.TestAsync(RequiresS3FactAttribute.Target(bucket, "definitely-the-wrong-secret"), null, CancellationToken.None);
        Assert.False(wrong.Ok);
        Assert.DoesNotContain("definitely-the-wrong-secret", wrong.Error!);
    }

    [RequiresS3Fact]
    public async Task Uploading_an_encrypted_file_and_deleting_it_works_and_the_object_decrypts()
    {
        var bucket = await NewBucketAsync();
        var target = RequiresS3FactAttribute.Target(bucket);
        var store = new S3BackupObjectStore();
        var key = BackupKeys.Derive(new byte[32], 1);
        var plain = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 241)).ToArray();
        var path = Path.GetTempFileName();
        try
        {
            await using (var file = File.Create(path)) await Aeb1Encryptor.EncryptAsync(new MemoryStream(plain), file, key.Key, key.KeyId);
            await store.UploadFileAsync(target, "aethera/org/aethera/controlplane/x.pgdump.aeb", path, CancellationToken.None);

            using var client = S3BackupObjectStore.CreateClient(target);
            using var response = await client.GetObjectAsync(bucket, "aethera/org/aethera/controlplane/x.pgdump.aeb");
            using var plainOut = new MemoryStream();
            await Aeb1Decryptor.DecryptAsync(response.ResponseStream, plainOut, _ => key.Key);
            Assert.Equal(plain, plainOut.ToArray());

            await store.DeleteAsync(target, ["aethera/org/aethera/controlplane/x.pgdump.aeb", "never/existed.aeb"], CancellationToken.None); // missing keys are fine
            Assert.Empty((await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket })).S3Objects ?? []);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
