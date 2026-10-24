using System.Security.Cryptography;
using System.Text.Json;
using Aethera.Api.Features.Backups;
using Aethera.Domain;
using Aethera.Domain.Transport;
using Aethera.Infrastructure.Backups;
using Aethera.Infrastructure.Crypto;
using Aethera.Infrastructure.Scheduling;

namespace Aethera.Api.Tests.Backups;

public sealed class Aeb1Tests
{
    private static readonly byte[] Key = Enumerable.Repeat((byte)1, 32).ToArray();
    private static readonly byte[] Salt = Enumerable.Repeat((byte)2, 32).ToArray();

    private static byte[] Plain(int length) => Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();

    private static async Task<(byte[] Sealed, Aeb1Result Result)> EncryptAsync(byte[] plain, byte[]? salt = null, string keyId = "test")
    {
        using var output = new MemoryStream();
        var result = await Aeb1Encryptor.EncryptAsync(new MemoryStream(plain), output, Key, keyId, salt);
        return (output.ToArray(), result);
    }

    private static async Task<byte[]> DecryptAsync(byte[] sealedBytes, byte[]? key = null)
    {
        using var output = new MemoryStream();
        await Aeb1Decryptor.DecryptAsync(new MemoryStream(sealedBytes), output, _ => key ?? Key);
        return output.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65535)]
    [InlineData(65536)]
    [InlineData(65537)]
    [InlineData(131072)]
    [InlineData(150000)]
    public async Task Round_trips_at_every_chunk_boundary(int length)
    {
        var plain = Plain(length);
        var (sealedBytes, result) = await EncryptAsync(plain);

        Assert.Equal(length, result.RawBytes);
        Assert.Equal(sealedBytes.Length, result.StoredBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(sealedBytes)), result.StoredSha256);
        Assert.Equal(plain, await DecryptAsync(sealedBytes));
    }

    [Fact]
    public async Task The_object_has_the_layout_of_the_adr()
    {
        var (sealedBytes, _) = await EncryptAsync(Plain(10), Salt, "mk1-abcd1234");
        Assert.Equal("AEB1"u8.ToArray(), sealedBytes[..4]);
        Assert.Equal(12, sealedBytes[4]);
        Assert.Equal("mk1-abcd1234", System.Text.Encoding.ASCII.GetString(sealedBytes, 5, 12));
        Assert.Equal(Salt, sealedBytes[17..49]);
        Assert.Equal(49 + 10 + 16, sealedBytes.Length); // header, one short final chunk, tag
    }

    [Fact]
    public async Task A_random_salt_makes_every_object_different()
    {
        var plain = Plain(1000);
        Assert.NotEqual((await EncryptAsync(plain)).Sealed, (await EncryptAsync(plain)).Sealed);
    }

    [Fact]
    public async Task A_changed_byte_anywhere_fails_to_decrypt()
    {
        var (sealedBytes, _) = await EncryptAsync(Plain(100000), Salt);
        foreach (var position in new[] { 2, 6, 30, 60, 70000, sealedBytes.Length - 3 })
        {
            var damaged = (byte[])sealedBytes.Clone();
            damaged[position] ^= 0x40;
            await Assert.ThrowsAsync<Aeb1Exception>(() => DecryptAsync(damaged));
        }
    }

    [Fact]
    public async Task A_truncated_object_fails_instead_of_restoring_a_prefix()
    {
        var (sealedBytes, _) = await EncryptAsync(Plain(200000), Salt);
        // Dropping the whole last chunk leaves a valid-looking prefix; it must still be rejected because the last chunk is not sealed as final.
        var lastChunk = (200000 - 3 * 65536) + 16; // 3 full chunks and a short last one
        await Assert.ThrowsAsync<Aeb1Exception>(() => DecryptAsync(sealedBytes[..^lastChunk]));
        await Assert.ThrowsAsync<Aeb1Exception>(() => DecryptAsync(sealedBytes[..^1]));
        await Assert.ThrowsAsync<Aeb1Exception>(() => DecryptAsync(sealedBytes[..60]));
        await Assert.ThrowsAsync<Aeb1Exception>(() => DecryptAsync(sealedBytes[..20]));
    }

    [Fact]
    public async Task Chunks_cannot_be_reordered_or_extended()
    {
        var (sealedBytes, _) = await EncryptAsync(Plain(3 * 65536), Salt);
        var swapped = (byte[])sealedBytes.Clone();
        var size = 65536 + 16;
        Array.Copy(sealedBytes, 49, swapped, 49 + size, size);
        Array.Copy(sealedBytes, 49 + size, swapped, 49, size);
        await Assert.ThrowsAsync<Aeb1Exception>(() => DecryptAsync(swapped));
        await Assert.ThrowsAsync<Aeb1Exception>(() => DecryptAsync([.. sealedBytes, .. new byte[40]]));
    }

    [Fact]
    public async Task The_wrong_key_and_other_formats_are_rejected_with_a_clear_message()
    {
        var (sealedBytes, _) = await EncryptAsync(Plain(100), Salt);
        await Assert.ThrowsAsync<Aeb1Exception>(() => DecryptAsync(sealedBytes, new byte[32]));
        var notBackup = await Assert.ThrowsAsync<Aeb1Exception>(() => DecryptAsync("PGDMP......"u8.ToArray()));
        Assert.Contains("not an Aethera backup", notBackup.Message);
        using var output = new MemoryStream();
        var unknown = await Assert.ThrowsAsync<Aeb1Exception>(() => Aeb1Decryptor.DecryptAsync(new MemoryStream(sealedBytes), output, _ => null));
        Assert.Contains("test", unknown.Message);
    }

    [Fact]
    public async Task Reading_the_header_reports_the_key_id_and_leaves_the_stream_at_the_first_chunk()
    {
        var (sealedBytes, _) = await EncryptAsync(Plain(5), Salt, "mk7-0a1b2c3d");
        var stream = new MemoryStream(sealedBytes);
        var header = await Aeb1Decryptor.ReadHeaderAsync(stream);
        Assert.Equal("mk7-0a1b2c3d", header.KeyId);
        Assert.Equal(Salt, header.Salt);
        Assert.Equal(header.Bytes.Length, stream.Position);
    }

    // ---- the reference vector of the Go agent -----------------------------------------------------------------------------------

    private static JsonElement LoadVector() => JsonDocument.Parse(File.ReadAllText(VectorFileFactAttribute.Path!)).RootElement;

    /// <summary>
    /// src/agent/internal/backup/testdata/aeb1-vector.json: key 32 x 0x01, key id "test", salt 32 x 0x02, plaintext byte i = i % 251. This implementation
    /// must produce exactly the object of the main vector and read it back.
    /// </summary>
    [VectorFileFact]
    public async Task Produces_the_object_of_the_main_reference_vector_and_reads_it_back()
    {
        var vector = LoadVector();
        var key = Convert.FromBase64String(vector.GetProperty("key_base64").GetString()!);
        var salt = Convert.FromHexString(vector.GetProperty("salt_hex").GetString()!);
        var keyId = vector.GetProperty("key_id").GetString()!;
        var expected = Convert.FromBase64String(vector.GetProperty("object_base64").GetString()!);
        var length = vector.GetProperty("plaintext_length").GetInt32();
        Assert.Equal(Key, key);
        Assert.Equal(Salt, salt);
        Assert.Equal(vector.GetProperty("header_hex").GetString(), Convert.ToHexStringLower(Aeb1.BuildHeader(keyId, salt)));
        Assert.Equal(vector.GetProperty("derived_key_hex").GetString(), Convert.ToHexStringLower(Aeb1.DeriveChunkKey(key, salt)));

        var plain = Plain(length);
        Assert.Equal(vector.GetProperty("plaintext_sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(plain)));
        var (sealedBytes, result) = await EncryptAsync(plain, salt, keyId);
        Assert.Equal(expected.Length, vector.GetProperty("object_length").GetInt32());
        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(sealedBytes));
        Assert.Equal(vector.GetProperty("object_sha256").GetString(), result.StoredSha256);

        Assert.Equal(plain, await DecryptAsync(expected));
    }

    /// <summary>Empty, one byte, exactly one and two full chunks, a chunk and a byte: lengths and SHA-256 of plaintext and object must match the agent's.</summary>
    [VectorFileFact]
    public async Task Produces_the_additional_reference_vectors()
    {
        var vector = LoadVector();
        var salt = Convert.FromHexString(vector.GetProperty("salt_hex").GetString()!);
        var keyId = vector.GetProperty("key_id").GetString()!;
        var cases = vector.GetProperty("additional_vectors").EnumerateArray().ToList();
        Assert.True(cases.Count >= 5, "the vector file lists the boundary cases");

        foreach (var c in cases)
        {
            var name = c.GetProperty("name").GetString();
            var plain = Plain(c.GetProperty("plaintext_length").GetInt32());
            Assert.True(c.GetProperty("plaintext_sha256").GetString() == Convert.ToHexStringLower(SHA256.HashData(plain)), $"{name}: plaintext");
            var (sealedBytes, result) = await EncryptAsync(plain, salt, keyId);
            Assert.True(c.GetProperty("object_length").GetInt32() == sealedBytes.Length, $"{name}: object length");
            Assert.True(c.GetProperty("object_sha256").GetString() == result.StoredSha256, $"{name}: object sha256");
            Assert.Equal(plain, await DecryptAsync(sealedBytes));
        }
    }

    [Fact]
    public async Task A_full_last_chunk_carries_the_final_flag_and_no_empty_chunk_follows()
    {
        var (one, _) = await EncryptAsync(Plain(65536), Salt);
        var (two, _) = await EncryptAsync(Plain(131072), Salt);
        Assert.Equal(41 + 65536 + 16, one.Length);
        Assert.Equal(41 + 2 * (65536 + 16), two.Length);
        Assert.Equal(41 + 16, (await EncryptAsync([], Salt)).Sealed.Length); // an empty plaintext is one empty final chunk
    }

    [Fact]
    public async Task A_reader_accepts_an_empty_final_chunk_after_a_full_one()
    {
        // header | full chunk (not final) | empty chunk (final): what a writer that always ends with an empty chunk produces
        var plain = Plain(65536);
        var header = Aeb1.BuildHeader("test", Salt);
        var chunkKey = Aeb1.DeriveChunkKey(Key, Salt);
        using var aes = new AesGcm(chunkKey, Aeb1.TagLength);
        var nonce = new byte[Aeb1.NonceLength];
        var sealedBytes = new List<byte>(header);

        var chunk = new byte[plain.Length + Aeb1.TagLength];
        Aeb1.WriteNonce(nonce, 0, final: false);
        aes.Encrypt(nonce, plain, chunk.AsSpan(0, plain.Length), chunk.AsSpan(plain.Length, Aeb1.TagLength), header);
        sealedBytes.AddRange(chunk);
        var tag = new byte[Aeb1.TagLength];
        Aeb1.WriteNonce(nonce, 1, final: true);
        aes.Encrypt(nonce, ReadOnlySpan<byte>.Empty, Span<byte>.Empty, tag.AsSpan(), header);
        sealedBytes.AddRange(tag);

        Assert.Equal(plain, await DecryptAsync([.. sealedBytes]));
    }
}

/// <summary>Skips with a clear message only when the Go agent's reference vector is not in the tree (a branch that does not have it yet).</summary>
public sealed class VectorFileFactAttribute : FactAttribute
{
    public static readonly string? Path = Locate();

    public VectorFileFactAttribute()
    {
        if (Path is null)
            Skip = "src/agent/internal/backup/testdata/aeb1-vector.json does not exist in this checkout (the Go agent writes it); the test runs once the agent branch is merged.";
    }

    private static string? Locate()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "src", "agent", "internal", "backup", "testdata", "aeb1-vector.json");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}

public sealed class BackupKeyTests
{
    private static MasterKeyring Ring(params (int Version, byte Fill)[] keys) =>
        new(keys.ToDictionary(k => k.Version, k => Enumerable.Repeat(k.Fill, 32).ToArray()), keys.Max(k => k.Version));

    [Fact]
    public void The_stream_key_is_hkdf_sha256_of_the_master_key_with_the_info_of_the_adr()
    {
        var master = Enumerable.Repeat((byte)9, 32).ToArray();
        var expected = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, master, expected, [], "aethera/backup/v1"u8);

        var key = BackupKeys.Derive(master, 3);
        Assert.Equal(expected, key.Key);
        Assert.Equal($"mk3-{Convert.ToHexStringLower(SHA256.HashData(expected))[..8]}", key.KeyId);
        Assert.Matches(@"^mk3-[0-9a-f]{8}$", key.KeyId);
        Assert.NotEqual(master, key.Key);
    }

    [Fact]
    public void New_backups_use_the_current_master_key_and_old_ones_stay_readable_after_a_rotation()
    {
        var old = BackupKeys.Current(Ring((1, 1)));
        var rotated = Ring((1, 1), (2, 2));
        var current = BackupKeys.Current(rotated);
        Assert.StartsWith("mk2-", current.KeyId);
        Assert.NotEqual(old.KeyId, current.KeyId);

        Assert.True(BackupKeys.TryForKeyId(rotated, old.KeyId, out var found));
        Assert.Equal(old.Key, found!.Key);
    }

    [Theory]
    [InlineData("mk1-00000000")] // right version, wrong hash: another master key
    [InlineData("mk9-abcdef01")] // a version that is not in the keyring
    [InlineData("nonsense")]
    [InlineData("mk-1234")]
    public void A_key_id_that_does_not_match_the_keyring_is_not_resolved(string keyId) =>
        Assert.False(BackupKeys.TryForKeyId(Ring((1, 1)), keyId, out _));

    [Fact]
    public void The_encryption_spec_carries_the_key_as_a_secret_that_never_prints()
    {
        var spec = BackupKeys.Current(Ring((1, 1))).ToSpec();
        Assert.Equal(32, Convert.FromBase64String(spec.Key.Value).Length);
        Assert.DoesNotContain(spec.Key.Value, spec.ToString());
    }
}

public sealed class RetentionTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static RetentionCandidate Run(string at) => new(Guid.CreateVersion7(), DateTimeOffset.Parse(at + "Z"));

    private static IReadOnlySet<Guid> Kept(IReadOnlyCollection<RetentionCandidate> runs, int d, int w, int m, TimeZoneInfo? zone = null)
    {
        var prune = BackupRetention.ToPrune(runs, d, w, m, zone ?? Utc);
        return runs.Select(r => r.Id).Except(prune).ToHashSet();
    }

    [Fact]
    public void Keeps_the_newest_run_of_each_of_the_last_n_days()
    {
        var day1 = Run("2026-10-20T03:00:00");
        var day1Later = Run("2026-10-20T15:00:00");
        var day2 = Run("2026-10-21T03:00:00");
        var day3 = Run("2026-10-22T03:00:00");
        var day4 = Run("2026-10-23T03:00:00");
        var kept = Kept([day1, day1Later, day2, day3, day4], 3, 0, 0);

        Assert.Equal(new[] { day2.Id, day3.Id, day4.Id }.Order(), kept.Order());
    }

    [Fact]
    public void Days_without_a_backup_do_not_count_so_a_pause_does_not_expire_old_backups()
    {
        var old = Run("2026-09-01T03:00:00");
        var recent = Run("2026-10-23T03:00:00");
        Assert.Equal(new[] { old.Id, recent.Id }.Order(), Kept([old, recent], 7, 0, 0).Order());
    }

    [Fact]
    public void Weekly_keeps_the_newest_run_of_each_iso_week()
    {
        // 2026-10-19 is a Monday: Oct 19-25 is one ISO week, Oct 12-18 the previous one.
        var wedPrev = Run("2026-10-14T03:00:00");
        var sunPrev = Run("2026-10-18T03:00:00");
        var monThis = Run("2026-10-19T03:00:00");
        var friThis = Run("2026-10-23T03:00:00");
        var kept = Kept([wedPrev, sunPrev, monThis, friThis], 0, 2, 0);

        Assert.Equal(new[] { sunPrev.Id, friThis.Id }.Order(), kept.Order());
    }

    [Fact]
    public void Monthly_keeps_the_newest_run_of_each_calendar_month_across_a_year_boundary()
    {
        var dec = Run("2025-12-30T03:00:00");
        var janEarly = Run("2026-01-02T03:00:00");
        var janLate = Run("2026-01-30T03:00:00");
        var feb = Run("2026-02-10T03:00:00");
        var kept = Kept([dec, janEarly, janLate, feb], 0, 0, 3);

        Assert.Equal(new[] { dec.Id, janLate.Id, feb.Id }.Order(), kept.Order());
        Assert.Equal(new[] { janLate.Id, feb.Id }.Order(), Kept([dec, janEarly, janLate, feb], 0, 0, 2).Order());
    }

    [Fact]
    public void The_year_boundary_belongs_to_the_iso_week_of_the_thursday()
    {
        var dec31 = Run("2026-12-31T03:00:00"); // Thursday, ISO week 53 of 2026
        var jan1 = Run("2027-01-01T03:00:00"); // Friday, the same ISO week 53
        Assert.Equal(new[] { jan1.Id }, Kept([dec31, jan1], 0, 1, 0).ToArray());
        Assert.Equal(new[] { jan1.Id }, Kept([dec31, jan1], 0, 2, 0).ToArray()); // one week only exists: its newest run wins, the other is pruned
    }

    [Fact]
    public void The_policys_time_zone_decides_which_day_a_run_belongs_to()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("utc+3", TimeSpan.FromHours(3), "UTC+3", "UTC+3"); // Bucharest in October
        var a = Run("2026-10-20T22:30:00"); // 01:30 on the 21st in Bucharest
        var b = Run("2026-10-21T10:00:00"); // the 21st as well
        Assert.Equal(new[] { b.Id }, Kept([a, b], 1, 0, 0, zone).ToArray());
        Assert.Equal(new[] { a.Id, b.Id }.Order(), Kept([a, b], 2, 0, 0, Utc).Order()); // two UTC days
    }

    [Fact]
    public void The_union_of_daily_weekly_and_monthly_is_kept_and_the_rest_pruned()
    {
        var runs = Enumerable.Range(0, 60).Select(i => Run(new DateTime(2026, 8, 25).AddDays(i).AddHours(3).ToString("s"))).ToList();
        var kept = Kept(runs, 7, 4, 2);
        Assert.Contains(runs[^1].Id, kept);
        Assert.InRange(kept.Count, 7, 7 + 4 + 2);
        Assert.True(kept.Count < runs.Count);
        // the last seven days are all there
        Assert.All(runs.TakeLast(7), r => Assert.Contains(r.Id, kept));
    }

    [Fact]
    public void The_newest_successful_run_is_always_kept_even_with_zero_everywhere()
    {
        var older = Run("2026-10-01T03:00:00");
        var newest = Run("2026-10-02T03:00:00");
        Assert.Equal(new[] { newest.Id }, Kept([older, newest], 0, 0, 0).ToArray());
    }

    [Fact]
    public void Nothing_to_prune_for_an_empty_history()
    {
        Assert.Empty(BackupRetention.ToPrune([], 7, 4, 6, Utc));
        var only = Run("2026-10-01T03:00:00");
        Assert.Empty(BackupRetention.ToPrune([only], 1, 0, 0, Utc));
    }
}

public sealed class EngineTests
{
    private static BackupEngineContext Context(string template, Dictionary<string, string> vars, string? database = null, params BackupVolumeInfo[] volumes) =>
        new("db-1234", vars, database, volumes);

    private static IEnumerable<string> Argv(object spec) => spec switch
    {
        ExecDumpSource d => d.Argv,
        ExecRestoreSink r => r.Argv,
        _ => [],
    };

    private static IReadOnlyList<EnvVarSpec> Env(object spec) => (spec switch { ExecDumpSource d => d.Env, ExecRestoreSink r => r.Env, _ => null }) ?? [];

    [Fact]
    public void Postgres_dumps_with_pg_dump_custom_format_and_passes_the_password_in_the_environment()
    {
        var engine = BackupEngines.ForTemplate("postgres")!;
        var context = Context("postgres", new() { ["POSTGRES_USER"] = "turn", ["POSTGRES_PASSWORD"] = "pw-SECRET-1", ["POSTGRES_DB"] = "app" });

        var dump = (ExecDumpSource)engine.Dump(context);
        Assert.Equal("db-1234", dump.Container);
        Assert.Equal(["pg_dump", "-Fc", "-U", "turn", "-d", "app"], dump.Argv);
        var password = Assert.Single(dump.Env!);
        Assert.Equal("PGPASSWORD", password.Name);
        Assert.Null(password.Plain);
        Assert.Equal("pw-SECRET-1", password.Secret!.Value);

        var restore = (ExecRestoreSink)engine.Restore(context);
        Assert.Equal(["pg_restore", "--clean", "--if-exists", "--no-owner", "-U", "turn", "-d", "app"], restore.Argv);
        Assert.Equal("PGPASSWORD", Assert.Single(restore.Env!).Name);
        Assert.Equal("postgres", engine.Name);
        Assert.Equal("pgdump", engine.Extension);
        Assert.False(engine.StopsForRestore);
    }

    [Fact]
    public void Postgres_defaults_and_the_database_override()
    {
        var engine = BackupEngines.ForTemplate("postgres")!;
        Assert.Equal(["pg_dump", "-Fc", "-U", "postgres", "-d", "postgres"], ((ExecDumpSource)engine.Dump(Context("postgres", []))).Argv);
        Assert.Equal("other", ((ExecDumpSource)engine.Dump(Context("postgres", new() { ["POSTGRES_DB"] = "app" }, "other"))).Argv[^1]);
    }

    [Theory]
    [InlineData("mysql", "mysqldump", "mysql", "MYSQL_ROOT_PASSWORD")]
    [InlineData("mariadb", "mariadb-dump", "mariadb", "MARIADB_ROOT_PASSWORD")]
    public void MySql_and_mariadb_use_the_root_password_through_mysql_pwd(string template, string dumpProgram, string restoreProgram, string variable)
    {
        var engine = BackupEngines.ForTemplate(template)!;
        var context = Context(template, new() { [variable] = "rootpw-SECRET" });
        var dump = (ExecDumpSource)engine.Dump(context);
        Assert.Equal(dumpProgram, dump.Argv[0]);
        Assert.Contains("--single-transaction", dump.Argv);
        Assert.Contains("--all-databases", dump.Argv);
        Assert.Equal("MYSQL_PWD", Assert.Single(dump.Env!).Name);
        Assert.Equal(restoreProgram, ((ExecRestoreSink)engine.Restore(context)).Argv[0]);
        Assert.Equal("sql", engine.Extension);
        Assert.DoesNotContain(dump.Argv, a => a.Contains("rootpw"));
    }

    [Fact]
    public void Mongo_never_puts_the_password_on_a_command_line()
    {
        var engine = BackupEngines.ForTemplate("mongodb")!;
        var context = Context("mongodb", new() { ["MONGO_INITDB_ROOT_USERNAME"] = "admin", ["MONGO_INITDB_ROOT_PASSWORD"] = "mongo-SECRET\"with'quotes" });
        var dump = (ExecDumpSource)engine.Dump(context);
        var restore = (ExecRestoreSink)engine.Restore(context);

        Assert.Equal(["sh", "-c"], dump.Argv.Take(2));
        Assert.Contains("mongodump --config \"$cfg\"", dump.Argv[2]);
        Assert.Contains("--authenticationDatabase admin --archive --gzip", dump.Argv[2]);
        Assert.Contains("umask 077", dump.Argv[2]);
        Assert.Contains("trap 'rm -f \"$cfg\"' EXIT", dump.Argv[2]);
        Assert.Contains("mongorestore --config \"$cfg\"", restore.Argv[2]);
        Assert.EndsWith("--archive --gzip --drop\n", restore.Argv[2]);
        Assert.Equal("mongo.gz", engine.Extension);

        // the script is constant text: neither the user nor the password is in it, they travel in the environment of the exec
        foreach (var spec in new object[] { dump, restore })
        {
            Assert.DoesNotContain(Argv(spec), a => a.Contains("mongo-SECRET"));
            var env = Env(spec);
            Assert.Equal("admin", env.Single(e => e.Name == "AETHERA_DB_USER").Plain);
            var secret = env.Single(e => e.Name == "AETHERA_DB_PASSWORD");
            Assert.Null(secret.Plain);
            Assert.Equal("mongo-SECRET\"with'quotes", secret.Secret!.Value);
        }
    }

    [Fact]
    public void Mongo_without_credentials_runs_the_plain_tools()
    {
        var engine = BackupEngines.ForTemplate("mongodb")!;
        Assert.Equal(["mongodump", "--archive", "--gzip"], ((ExecDumpSource)engine.Dump(Context("mongodb", []))).Argv);
    }

    [Fact]
    public void Redis_streams_a_tar_of_the_snapshot_and_restores_into_its_data_volume_with_the_workload_stopped()
    {
        var engine = BackupEngines.ForTemplate("redis")!;
        var data = new BackupVolumeInfo("cache-1234-data", "/data", false);
        var context = Context("redis", new() { ["REDIS_PASSWORD"] = "redis-SECRET" }, null, new BackupVolumeInfo("scratch", "/tmp", false), data);

        var dump = (ExecDumpSource)engine.Dump(context);
        Assert.Equal(["sh", "-c"], dump.Argv.Take(2));
        Assert.Contains("redis-cli --rdb", dump.Argv[2]);
        Assert.Contains("tar -C \"$d\" -czf - dump.rdb", dump.Argv[2]);
        var auth = Assert.Single(dump.Env!);
        Assert.Equal("REDISCLI_AUTH", auth.Name);
        Assert.Equal("redis-SECRET", auth.Secret!.Value);
        Assert.DoesNotContain(dump.Argv, a => a.Contains("redis-SECRET"));

        var restore = Assert.IsType<VolumeRestoreSink>(engine.Restore(context));
        Assert.Equal("cache-1234-data", restore.Volume);
        Assert.True(restore.ClearFirst); // an AOF directory left behind would win over the restored dump.rdb
        Assert.True(engine.StopsForRestore);
        Assert.Equal("redis", engine.Name);
    }

    [Fact]
    public void Redis_cannot_restore_without_a_named_data_volume()
    {
        var engine = BackupEngines.ForTemplate("redis")!;
        Assert.Throws<BackupEngineException>(() => engine.Restore(Context("redis", [])));
        Assert.Throws<BackupEngineException>(() => engine.Restore(Context("redis", [], null, new BackupVolumeInfo("/srv/redis", "/data", true))));
    }

    [Theory]
    [InlineData("postgres", true)]
    [InlineData("POSTGRES", true)]
    [InlineData("mysql", true)]
    [InlineData("mariadb", true)]
    [InlineData("mongodb", true)]
    [InlineData("redis", true)]
    [InlineData("grafana", false)]
    [InlineData("minio", false)]
    [InlineData(null, false)]
    public void Only_the_database_templates_have_a_database_policy(string? template, bool supported) =>
        Assert.Equal(supported, BackupEngines.Supports(template));

    [Fact]
    public void Every_supported_template_exists_in_the_catalogue()
    {
        foreach (var key in BackupEngines.SupportedTemplates)
            Assert.NotNull(Aethera.Api.Features.Resources.Services.ServiceTemplates.Find(key));
    }
}

public sealed class LocationAndRuleTests
{
    [Fact]
    public void Object_keys_follow_the_layout_of_the_adr()
    {
        var org = Guid.Parse("0190f3c2-7b1e-7c3a-9f4d-2a6b8e1d4c55");
        var workload = Guid.Parse("0190f3c2-aaaa-7c3a-9f4d-2a6b8e1d0001");
        var run = Guid.Parse("0190f3c2-bbbb-7c3a-9f4d-2a6b8e1d0002");
        var at = new DateTimeOffset(2026, 10, 23, 3, 0, 7, TimeSpan.FromHours(3));

        Assert.Equal("aethera/8e1d4c55/turn-api-0190f3c2/database/20261023T000007Z-8e1d0002.pgdump.aeb",
            BackupLocations.ObjectKey("aethera", org, "turn-api", workload, BackupKind.Database, at, run, "pgdump"));
        Assert.Equal("8e1d4c55/turn-api-0190f3c2/volume/20261023T000007Z-8e1d0002.tar.gz.aeb",
            BackupLocations.ObjectKey(null, org, "turn-api", workload, BackupKind.Volume, at, run, "tar.gz"));
        Assert.Equal("x/8e1d4c55/aethera/controlplane/20261023T000007Z-8e1d0002.pgdump.aeb",
            BackupLocations.ObjectKey("/x/", org, null, null, BackupKind.ControlPlane, at, run, "pgdump"));
    }

    [Theory]
    [InlineData("https://abc123.r2.cloudflarestorage.com", true)]
    [InlineData("https://s3.us-west-004.backblazeb2.com/", true)]
    [InlineData("https://s3.eu-central-1.amazonaws.com", true)]
    [InlineData("http://127.0.0.1:9000", true)]
    [InlineData("http://localhost:9000", true)]
    [InlineData("http://minio:9000", true)]
    [InlineData("http://192.168.1.20:9000", true)]
    [InlineData("http://10.0.0.5", true)]
    [InlineData("http://172.20.1.1:9000", true)]
    [InlineData("http://storage.internal:9000", true)]
    [InlineData("http://s3.example.com", false)]
    [InlineData("http://8.8.8.8", false)]
    [InlineData("http://172.32.0.1", false)]
    [InlineData("ftp://example.com", false)]
    [InlineData("https://user:pw@example.com", false)]
    [InlineData("https://example.com/bucket", false)]
    [InlineData("https://example.com?x=1", false)]
    [InlineData("example.com", false)]
    [InlineData("", false)]
    public void Endpoints_are_https_except_for_private_hosts(string endpoint, bool accepted) =>
        Assert.Equal(accepted, BackupDestinationRules.TryNormalizeEndpoint(endpoint, out _, out _));

    [Fact]
    public void The_endpoint_is_normalized_without_a_trailing_slash()
    {
        Assert.True(BackupDestinationRules.TryNormalizeEndpoint(" https://abc.r2.cloudflarestorage.com/ ", out var normalized, out _));
        Assert.Equal("https://abc.r2.cloudflarestorage.com", normalized);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("aethera", true)]
    [InlineData("backups/prod", true)]
    [InlineData("/aethera", false)]
    [InlineData("aethera/", false)]
    [InlineData("a/../b", false)]
    [InlineData("a//b", false)]
    [InlineData("a\\b", false)]
    public void Path_prefixes_are_folder_names(string? prefix, bool valid) => Assert.Equal(valid, BackupDestinationRules.IsValidPrefix(prefix));

    [Theory]
    [InlineData("0 3 * * *", "UTC", true)]
    [InlineData("0 */6 * * *", "UTC", true)]
    [InlineData("0 4 * * 0", "UTC", true)]
    [InlineData("*/15 * * * *", "UTC", true)]
    [InlineData("*/14 * * * *", "UTC", false)]
    [InlineData("* * * * *", "UTC", false)]
    [InlineData("0,5 * * * *", "UTC", false)]
    [InlineData("not a cron", "UTC", false)]
    [InlineData("0 3 * * *", "Mars/Olympus", false)]
    public void Schedules_are_valid_cron_in_a_known_zone_and_at_most_every_fifteen_minutes(string schedule, string zone, bool valid)
    {
        var errors = new List<Aethera.Api.Http.Errors.FieldError>();
        BackupPolicyRules.ValidateSchedule(schedule, zone, "", errors, DateTimeOffset.Parse("2026-10-23T12:00:00Z"));
        Assert.Equal(valid, errors.Count == 0);
    }

    [Fact]
    public void An_absent_schedule_means_daily_at_three()
    {
        var errors = new List<Aethera.Api.Http.Errors.FieldError>();
        BackupPolicyRules.ValidateSchedule(null, null, "", errors, DateTimeOffset.UtcNow);
        Assert.Empty(errors);
    }

    [IanaTimeZoneFact]
    public void The_next_run_comes_from_the_schedule_in_its_time_zone()
    {
        var schedule = CronSchedule.Parse("0 3 * * *", "Europe/Bucharest");
        var first = schedule.Next(DateTimeOffset.Parse("2026-10-23T12:00:00Z"))!.Value;
        Assert.Equal(DateTimeOffset.Parse("2026-10-24T00:00:00Z"), first); // 03:00 at UTC+3
    }
}
