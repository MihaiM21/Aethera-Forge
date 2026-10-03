using System.Text;
using Aethera.Domain;
using Aethera.Infrastructure.Jobs;

namespace Aethera.Api.Tests.Jobs;

public sealed class LogPipelineTests
{
    [Fact]
    public void Redactor_MasksRegisteredValues_LongestFirst()
    {
        var redactor = new SecretRedactor();
        redactor.Register("pass");
        redactor.Register("pass-word-123");
        redactor.Register(null);
        redactor.Register("");
        redactor.Register("ab"); // too short to mask safely

        Assert.Equal("login ******** ok, ******** too, ab stays", redactor.Redact("login pass-word-123 ok, pass too, ab stays"));
        Assert.Equal("nothing here", redactor.Redact("nothing here"));
    }

    [Fact]
    public void Redactor_MasksEachLineOfAMultiLineSecret()
    {
        var redactor = new SecretRedactor();
        redactor.Register("-----BEGIN KEY-----\nMIIEvQIBADANBg\n-----END KEY-----");

        Assert.Equal("a ******** b", redactor.Redact("a MIIEvQIBADANBg b"));
        Assert.Equal("********", redactor.Redact("-----END KEY-----"));
        Assert.Equal("********", redactor.Redact("-----BEGIN KEY-----\nMIIEvQIBADANBg\n-----END KEY-----"));
    }

    [RequiresDatabaseFact]
    public async Task Sink_StoresChunksInOrder_WithContiguousSequences_AndRedactsSecrets()
    {
        await using var db = await JobDatabase.CreateAsync();
        await using var host = await JobTestHost.StartAsync(db.ConnectionString, settings: s => s["Aethera:Jobs:WorkerCount"] = "0");
        var redactor = new SecretRedactor();
        redactor.Register("hunter2-password");

        await using (var sink = await host.Host.Services.GetRequiredService<ILogSinkFactory>().CreateAsync("job:stream-a", redactor))
        {
            for (var i = 0; i < 3000; i++)
                await sink.WriteAsync(i % 7 == 0 ? LogStream.Stderr : LogStream.Stdout, $"line {i:D5} password=hunter2-password");
            await sink.WriteSystemAsync("done");
            await sink.FlushAsync();
        }

        var chunks = await db.GetChunksAsync("job:stream-a");
        Assert.True(chunks.Count > 1);
        Assert.Equal(Enumerable.Range(1, chunks.Count).Select(i => (long)i), chunks.Select(c => c.Sequence));
        Assert.All(chunks, c => Assert.True(Encoding.UTF8.GetByteCount(c.Data) <= 64 * 1024));
        Assert.DoesNotContain(chunks, c => c.Data.Contains("hunter2-password"));

        // All lines survive, in order, with their stream.
        var lines = string.Concat(chunks.Select(c => c.Data)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3001, lines.Length);
        Assert.Equal("line 00000 password=********", lines[0]);
        Assert.Equal("line 02999 password=********", lines[2999]);
        Assert.Equal("[aethera] done", lines[3000]);
        Assert.Contains(chunks, c => c.Stream == LogStream.Stderr);
        Assert.All(chunks, c => Assert.Equal(LogSource.Job, c.Source));
        Assert.True(chunks.Zip(chunks.Skip(1)).All(p => p.First.Timestamp <= p.Second.Timestamp));
    }

    [RequiresDatabaseFact]
    public async Task Sink_FlushesOnTheInterval_WithoutAnExplicitFlush()
    {
        await using var db = await JobDatabase.CreateAsync();
        await using var host = await JobTestHost.StartAsync(db.ConnectionString, settings: s => s["Aethera:Jobs:WorkerCount"] = "0");

        var sink = await host.Host.Services.GetRequiredService<ILogSinkFactory>().CreateAsync("job:stream-b");
        await sink.WriteAsync(LogStream.Stdout, "tail me");

        await Eventually.UntilAsync(async () => (await db.GetChunksAsync("job:stream-b")).Count == 1, TimeSpan.FromSeconds(5));
        Assert.Equal("tail me\n", (await db.GetChunksAsync("job:stream-b")).Single().Data);
    }

    [RequiresDatabaseFact]
    public async Task Sink_ContinuesTheSequenceOfAnExistingStream_AndReplaysAreIdempotent()
    {
        await using var db = await JobDatabase.CreateAsync();
        await using var host = await JobTestHost.StartAsync(db.ConnectionString, settings: s => s["Aethera:Jobs:WorkerCount"] = "0");
        var factory = host.Host.Services.GetRequiredService<ILogSinkFactory>();

        await using (var first = await factory.CreateAsync("job:stream-c"))
        {
            await first.WriteAsync(LogStream.Stdout, "one");
            await first.FlushAsync();
        }

        // A later attempt (maybe on another host) continues numbering ...
        await using (var second = await factory.CreateAsync("job:stream-c"))
        {
            await second.WriteAsync(LogStream.Stdout, "two");
            await second.FlushAsync();
        }

        // ... and two writers racing for the same sequence cannot duplicate or fail: ON CONFLICT DO NOTHING.
        var racerA = await factory.CreateAsync("job:stream-c");
        var racerB = await factory.CreateAsync("job:stream-c");
        await racerA.WriteAsync(LogStream.Stdout, "from A");
        await racerB.WriteAsync(LogStream.Stdout, "from B");
        await racerA.FlushAsync();
        await racerB.FlushAsync();

        var chunks = await db.GetChunksAsync("job:stream-c");
        Assert.Equal([1L, 2L, 3L], chunks.Select(c => c.Sequence));
        Assert.Equal(["one\n", "two\n"], chunks.Take(2).Select(c => c.Data));
        Assert.Contains(chunks[2].Data, new[] { "from A\n", "from B\n" });
    }

    [RequiresDatabaseFact]
    public async Task Sink_StopsAtTheStreamCap_WithOneTruncationMarker()
    {
        await using var db = await JobDatabase.CreateAsync();
        await using var host = await JobTestHost.StartAsync(db.ConnectionString, settings: s =>
        {
            s["Aethera:Jobs:WorkerCount"] = "0";
            s["Aethera:Jobs:LogMaxBytesPerStream"] = "200";
        });

        var sink = await host.Host.Services.GetRequiredService<ILogSinkFactory>().CreateAsync("job:stream-d");
        for (var i = 0; i < 100; i++) await sink.WriteAsync(LogStream.Stdout, $"0123456789 line {i}");
        await sink.FlushAsync();

        var text = string.Concat((await db.GetChunksAsync("job:stream-d")).Select(c => c.Data));
        Assert.Equal(1, text.Split("... log truncated ...").Length - 1);
        Assert.True(Encoding.UTF8.GetByteCount(text) < 400);
        Assert.StartsWith("0123456789 line 0\n", text);
    }

    [RequiresDatabaseFact]
    public async Task Sink_SplitsAHugeLine_IntoChunksBelow64KiB()
    {
        await using var db = await JobDatabase.CreateAsync();
        await using var host = await JobTestHost.StartAsync(db.ConnectionString, settings: s => s["Aethera:Jobs:WorkerCount"] = "0");

        var sink = await host.Host.Services.GetRequiredService<ILogSinkFactory>().CreateAsync("job:stream-e");
        var huge = new string('é', 100_000); // 2 bytes per char
        await sink.WriteAsync(LogStream.Stdout, huge);
        await sink.FlushAsync();

        var chunks = await db.GetChunksAsync("job:stream-e");
        Assert.True(chunks.Count >= 4);
        Assert.All(chunks, c => Assert.True(Encoding.UTF8.GetByteCount(c.Data) <= 64 * 1024));
        Assert.Equal(huge + "\n", string.Concat(chunks.Select(c => c.Data)));
    }
}
