using System.Text.Json.Nodes;
using Aethera.Domain;
using Aethera.Infrastructure.Auditing;
using Aethera.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Api.Tests;

public sealed class RedactionTests
{
    [Fact]
    public void RemovesSensitiveKeys_CaseInsensitively_AtAnyDepth()
    {
        var redacted = Redaction.Redact(new
        {
            name = "web",
            Password = "p",
            dbSECRET = "s",
            apiToken = "t",
            sshKey = "k",
            newValue = "v",
            port = 80,
            nested = new { deeper = new { ClientSecret = "x", keep = "y" } },
            list = new object[] { new { Token = "t1", ok = true }, "plain", 3 },
        })!.AsObject();

        Assert.Equal("web", redacted["name"]!.GetValue<string>());
        Assert.Equal(80, redacted["port"]!.GetValue<int>());
        foreach (var key in new[] { "password", "dbSECRET", "apiToken", "sshKey", "newValue" })
            Assert.Equal(Redaction.Placeholder, redacted[key == "password" ? "password" : key]!.GetValue<string>());
        Assert.Equal(Redaction.Placeholder, redacted["nested"]!["deeper"]!["clientSecret"]!.GetValue<string>());
        Assert.Equal("y", redacted["nested"]!["deeper"]!["keep"]!.GetValue<string>());
        Assert.Equal(Redaction.Placeholder, redacted["list"]![0]!["token"]!.GetValue<string>());
        Assert.True(redacted["list"]![0]!["ok"]!.GetValue<bool>());
        Assert.Equal("plain", redacted["list"]![1]!.GetValue<string>());
    }

    [Fact]
    public void DoesNotMutateTheInput_AndRedactsJsonNodes()
    {
        var input = JsonNode.Parse("""{"password":"p","a":{"secret":"s"}}""")!;
        var redacted = Redaction.Redact(input)!;
        Assert.Equal("p", input["password"]!.GetValue<string>());
        Assert.Equal(Redaction.Placeholder, redacted["a"]!["secret"]!.GetValue<string>());
    }

    [Fact]
    public void ToJsonObject_AlwaysProducesAnObject()
    {
        Assert.Equal("{}", Redaction.ToJsonObject(null));
        Assert.Equal("""{"data":"text"}""", Redaction.ToJsonObject("text"));
        Assert.Equal("""{"data":[1,2]}""", Redaction.ToJsonObject(new[] { 1, 2 }));
        Assert.Equal("""{"status":"inProgress"}""", Redaction.ToJsonObject(new { status = ThingStatus.InProgress }));
    }

    [Theory]
    [InlineData("password", true)]
    [InlineData("PasswordHash", true)]
    [InlineData("secretId", true)]
    [InlineData("apiTokenId", true)]
    [InlineData("idempotencyKey", true)]
    [InlineData("value", true)]
    [InlineData("name", false)]
    [InlineData("resourceType", false)]
    public void IsSensitiveName(string name, bool expected) => Assert.Equal(expected, Redaction.IsSensitiveName(name));
}

[Collection(TestApiCollection.Name)]
public sealed class EfAuditLogTests(TestApiFixture fixture)
{
    private static readonly Guid ResourceId = Guid.Parse("0190f3c2-7b1e-7c3a-9f4d-2a6b8e1d4c55");

    [RequiresDatabaseFact]
    public async Task SessionRequest_WritesAnEventWithActorRequestIdAndRedactedMetadata()
    {
        var identity = await fixture.Base.SeedIdentityAsync(OrganizationRole.Admin);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/_test/audit");
        request.Headers.Add("X-Request-Id", "audit-req-1");
        var response = await fixture.Factory.CreateClientAs(OrganizationRole.Admin, identity: identity).SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);

        var row = await FindAsync("audit-req-1");
        Assert.Equal("thing.created", row.Action);
        Assert.Equal("thing", row.ResourceType);
        Assert.Equal(ResourceId, row.ResourceId);
        Assert.Equal(AuditActorType.User, row.ActorType);
        Assert.Equal(identity.UserId, row.ActorUserId);
        Assert.Null(row.ActorApiTokenId);
        Assert.Equal(identity.OrganizationId, row.OrganizationId);
        Assert.True(DateTimeOffset.UtcNow - row.OccurredAt < TimeSpan.FromMinutes(1));

        Assert.DoesNotContain("hunter2", row.MetadataJson);
        Assert.DoesNotContain("k-123", row.MetadataJson);
        Assert.DoesNotContain("t-1", row.MetadataJson);
        var metadata = JsonNode.Parse(row.MetadataJson)!;
        Assert.Equal("web", metadata["name"]!.GetValue<string>());
        Assert.Equal(Redaction.Placeholder, metadata["password"]!.GetValue<string>());
        Assert.Equal(Redaction.Placeholder, metadata["nested"]!["apiKey"]!.GetValue<string>());
        Assert.Equal(1, metadata["nested"]!["ok"]!.GetValue<int>());
        Assert.Equal(80, metadata["list"]![0]!["port"]!.GetValue<int>());
    }

    [RequiresDatabaseFact]
    public async Task TokenRequest_IsAttributedToTheToken()
    {
        var identity = await fixture.Base.SeedIdentityAsync(OrganizationRole.Developer);
        var tokenId = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/_test/audit");
        request.Headers.Add("X-Request-Id", "audit-req-2");
        var client = fixture.Factory.CreateClientAs(OrganizationRole.Developer, ["write"], identity, tokenId);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, (await client.SendAsync(request)).StatusCode);

        var row = await FindAsync("audit-req-2");
        Assert.Equal(AuditActorType.ApiToken, row.ActorType);
        Assert.Equal(tokenId, row.ActorApiTokenId);
        Assert.Equal(identity.UserId, row.ActorUserId);
    }

    private async Task<AuditEvent> FindAsync(string requestId)
    {
        await using var scope = fixture.Base.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AetheraDbContext>();
        return await db.AuditEvents.AsNoTracking().SingleAsync(e => e.RequestId == requestId);
    }
}
