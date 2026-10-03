using Aethera.Domain;

namespace Aethera.Api.Tests.Resources;

/// <summary>WP1.6 part 3: validation hardening that needs the running API (the unit-level pieces are in <see cref="RegexAnchorRegressionTests"/>).</summary>
[Collection(ResourcesCollection.Name)]
public sealed class ValidationHardeningTests(ResourcesFixture fixture)
{
    // ---- domain path prefixes -------------------------------------------------------------------------------------------------------

    [RequiresDatabaseTheory]
    [InlineData("/a b")]
    [InlineData("/a?x=1")]
    [InlineData("/a#f")]
    [InlineData("/a`)||Host(`evil.example.com")]
    [InlineData("/a\"b")]
    [InlineData("/a'b")]
    [InlineData("/a;b")]
    [InlineData("/a{b}")]
    [InlineData("/a//b")]
    [InlineData("//")]
    [InlineData("/a/../b")]
    [InlineData("/a/./b")]
    [InlineData("/a\\b")]
    [InlineData("/ä")]
    [InlineData("/api\n")]
    [InlineData("api")]
    public async Task DomainPathPrefix_IsRestrictedToSafeCharacters(string prefix)
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();
        var host = Tenant.Unique("h") + ".example.com"; // hostnames are unique across organizations
        Assert.Contains(("/pathPrefix", "pattern"), await (await tenant.Developer.PostAsync($"/api/v1/applications/{appId}/domains", new { hostname = host, pathPrefix = prefix })).ValidationErrorsAsync());
        var ok = await tenant.Developer.CreateAsync($"/api/v1/applications/{appId}/domains", new { hostname = host, pathPrefix = "/ok" });
        Assert.Contains(("/pathPrefix", "pattern"), await (await tenant.Developer.PatchAsync($"/api/v1/domains/{ok.Id()}", new { pathPrefix = prefix })).ValidationErrorsAsync());
    }

    [RequiresDatabaseFact]
    public async Task DomainPathPrefix_AllowsSafeCharacters_AndAtMost256()
    {
        var tenant = await fixture.NewTenantAsync();
        var (_, _, _, appId) = await tenant.CreateStackAsync();
        var url = $"/api/v1/applications/{appId}/domains";
        Assert.Equal("/v1.0/_a~b-c", (await tenant.Developer.CreateAsync(url, new { hostname = Tenant.Unique("a") + ".example.com", pathPrefix = "/v1.0/_a~b-c/" }))["pathPrefix"]!.GetValue<string>());
        Assert.Equal("/", (await tenant.Developer.CreateAsync(url, new { hostname = Tenant.Unique("b") + ".example.com" }))["pathPrefix"]!.GetValue<string>());
        var longest = "/" + new string('a', 255);
        Assert.Equal(longest, (await tenant.Developer.CreateAsync(url, new { hostname = Tenant.Unique("c") + ".example.com", pathPrefix = longest }))["pathPrefix"]!.GetValue<string>());
        Assert.Contains(("/pathPrefix", "pattern"), await (await tenant.Developer.PostAsync(url, new { hostname = Tenant.Unique("d") + ".example.com", pathPrefix = longest + "a" })).ValidationErrorsAsync());
    }
}
