using Aethera.Infrastructure.Backups;
using Aethera.Infrastructure.Crypto;

namespace Aethera.Api.Features.Backups;

/// <summary>
/// <c>dotnet Aethera.Api.dll backup-decrypt &lt;in&gt; &lt;out&gt;</c>: decrypts an AEB1 backup with the key the object's header names, derived from the
/// configured master keyring (<c>AETHERA_MASTER_KEY</c>, or <c>/data/master.key</c> as the container entrypoint generates it). Runs before the
/// web host starts. <c>-</c> stands for stdin or stdout (the host script pipes through <c>docker compose exec</c>). This is how Aethera's own
/// database is restored from a destination (docs/backups.md).
/// </summary>
public static class BackupDecryptCommand
{
    public const string Name = "backup-decrypt";

    public static async Task<int> RunAsync(string[] args, TextWriter error)
    {
        if (args.Length != 2)
        {
            await error.WriteLineAsync($"usage: {Name} <encrypted backup file or -> <output file or ->");
            return 2;
        }

        MasterKeyring keyring;
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            if (string.IsNullOrWhiteSpace(configuration[MasterKeyring.MasterKeyConfigKey]) && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MasterKeyring.EnvironmentVariableName))
                && File.Exists("/data/master.key"))
                Environment.SetEnvironmentVariable(MasterKeyring.EnvironmentVariableName, (await File.ReadAllTextAsync("/data/master.key")).Trim());
            keyring = MasterKeyring.Load(configuration, KeyringMode.Strict);
        }
        catch (InvalidOperationException ex)
        {
            await error.WriteLineAsync($"{Name}: {ex.Message}");
            return 1;
        }

        string? partial = null;
        try
        {
            await using var input = args[0] == "-" ? Console.OpenStandardInput() : File.OpenRead(args[0]);
            await using var output = args[1] == "-" ? Console.OpenStandardOutput() : File.Create(partial = args[1] + ".partial");
            var written = await Aeb1Decryptor.DecryptAsync(input, output, keyId => BackupKeys.TryForKeyId(keyring, keyId, out var key) ? key!.Key : null);
            await output.DisposeAsync();
            if (partial is not null) File.Move(partial, args[1], overwrite: true);
            await error.WriteLineAsync($"{Name}: decrypted {written} bytes{(args[1] == "-" ? "" : $" into {args[1]}")}.");
            return 0;
        }
        catch (Exception ex) when (ex is Aeb1Exception or IOException or UnauthorizedAccessException)
        {
            if (partial is not null) try { File.Delete(partial); } catch (IOException) { }
            await error.WriteLineAsync($"{Name}: {ex.Message}");
            return 1;
        }
    }
}
