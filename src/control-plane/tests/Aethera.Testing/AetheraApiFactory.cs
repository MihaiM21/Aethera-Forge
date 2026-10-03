using Aethera.Api.Features;
using Aethera.Api.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Aethera.Testing;

/// <summary>
/// The API under test. Use as an xUnit class fixture: <c>IClassFixture&lt;AetheraApiFactory&gt;</c>.
/// <list type="bullet">
/// <item>Environment <c>Testing</c>. No database, Redis or network is needed unless a test uses one.</item>
/// <item>When <c>AETHERA_TEST_DB</c> is set, a uniquely named database is created and migrated on first use, the app is pointed at it, and
/// it is dropped when the factory is disposed. <see cref="HasDatabase"/> tells which; use <c>[RequiresDatabaseFact]</c> for such tests.</item>
/// <item>Authentication is replaced by <see cref="TestAuthHandler"/> (see <see cref="ClientExtensions.CreateClientAs"/>), unless
/// <see cref="UseTestAuthentication"/> is overridden to false (<see cref="RealAuthApiFactory"/>).</item>
/// </list>
/// Add throw-away endpoints with <see cref="FactoryExtensions.WithEndpoints"/>.
/// </summary>
public class AetheraApiFactory : WebApplicationFactory<Program>
{
    private readonly Lazy<TestDatabase?> _database = new(() => TestDatabase.CreateAsync().GetAwaiter().GetResult());

    /// <summary>True when <c>AETHERA_TEST_DB</c> is set and the per-factory database exists.</summary>
    public bool HasDatabase => _database.Value is not null;

    /// <summary>Connection string of the per-factory database. Throws when <see cref="HasDatabase"/> is false.</summary>
    public string ConnectionString => _database.Value?.ConnectionString
        ?? throw new InvalidOperationException($"{RequiresDatabaseFactAttribute.EnvironmentVariable} is not set.");

    /// <summary>Replace the real authentication with <see cref="TestAuthHandler"/>. Override to false to exercise the real handlers.</summary>
    protected virtual bool UseTestAuthentication => true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        if (_database.Value is { } database) builder.UseSetting("ConnectionStrings:Aethera", database.ConnectionString);

        if (UseTestAuthentication)
        {
            builder.ConfigureTestServices(services =>
                services.AddAuthentication(options =>
                    {
                        options.DefaultScheme = AetheraAuthSchemes.Test;
                        options.DefaultAuthenticateScheme = AetheraAuthSchemes.Test;
                        options.DefaultChallengeScheme = AetheraAuthSchemes.Test;
                        options.DefaultForbidScheme = AetheraAuthSchemes.Test;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(AetheraAuthSchemes.Test, _ => { }));
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && _database.IsValueCreated && _database.Value is { } database)
            database.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

/// <summary>Factory with the real authentication handlers (for WP1.1's own tests).</summary>
public class RealAuthApiFactory : AetheraApiFactory
{
    protected override bool UseTestAuthentication => false;
}

public static class FactoryExtensions
{
    /// <summary>
    /// A factory (same configuration and database) whose <c>/api/v1</c> group additionally contains the endpoints mapped by
    /// <paramref name="map"/>. They inherit the group's conventions: authenticated by default, 1 MiB body limit. Remember
    /// <c>.WithName(...)</c> on each. <paramref name="configureServices"/> registers what the endpoints need (validators, fakes).
    /// </summary>
    public static WebApplicationFactory<Program> WithEndpoints(
        this WebApplicationFactory<Program> factory,
        Action<IEndpointRouteBuilder> map,
        Action<IServiceCollection>? configureServices = null) =>
        factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IApiEndpointContributor>(new DelegateContributor(map));
                configureServices?.Invoke(services);
            }));

    private sealed class DelegateContributor(Action<IEndpointRouteBuilder> map) : IApiEndpointContributor
    {
        public void MapEndpoints(IEndpointRouteBuilder api) => map(api);
    }
}
