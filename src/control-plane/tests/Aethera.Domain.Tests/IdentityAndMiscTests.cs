namespace Aethera.Domain.Tests;

public sealed class IdentityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void User_NormalizesEmail()
    {
        var user = new User("  Mihai@Example.COM ", "Mihai");

        Assert.Equal("Mihai@Example.COM", user.Email);
        Assert.Equal("mihai@example.com", user.NormalizedEmail);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    public void User_RejectsInvalidEmail(string email) =>
        Assert.Throws<DomainRuleException>(() => new User(email, "x"));

    [Fact]
    public void User_LocksOutAfterTooManyFailures_AndLoginClearsIt()
    {
        var user = new User("a@b.c", "A");

        user.RecordFailedLogin(T0, maxAttempts: 2, TimeSpan.FromMinutes(15));
        Assert.False(user.IsLockedOut(T0));
        user.RecordFailedLogin(T0, maxAttempts: 2, TimeSpan.FromMinutes(15));
        Assert.True(user.IsLockedOut(T0.AddMinutes(5)));
        Assert.False(user.IsLockedOut(T0.AddMinutes(16)));

        user.RecordLogin(T0.AddMinutes(20));
        Assert.Equal(0, user.FailedLoginCount);
        Assert.Null(user.LockoutEndAt);
        Assert.Equal(T0.AddMinutes(20), user.LastLoginAt);
    }

    [Fact]
    public void ApiToken_ActiveUntilRevokedOrExpired()
    {
        var token = new ApiToken
        {
            Name = "ci",
            Prefix = "aeth_ab12cd34",
            SecretHash = new byte[ApiToken.SecretHashLength],
            Scopes = ["deployments:write"],
            ExpiresAt = T0.AddDays(1),
        };

        Assert.True(token.IsActive(T0));
        Assert.False(token.IsActive(T0.AddDays(2)));

        token.Revoke(T0.AddHours(1));
        Assert.False(token.IsActive(T0.AddHours(2)));
        Assert.Equal(T0.AddHours(1), token.RevokedAt);

        token.Revoke(T0.AddHours(5)); // idempotent
        Assert.Equal(T0.AddHours(1), token.RevokedAt);
    }

    [Fact]
    public void ApiToken_WithoutExpiryNeverExpires()
    {
        var token = new ApiToken { Name = "n", Prefix = "aeth_x", SecretHash = new byte[32] };
        Assert.True(token.IsActive(T0.AddYears(50)));
    }

    [Fact]
    public void ApiToken_ScopeMatchingSupportsWildcard()
    {
        var narrow = new ApiToken { Name = "n", Prefix = "p", SecretHash = new byte[32], Scopes = ["servers:read"] };
        var wide = new ApiToken { Name = "n", Prefix = "p2", SecretHash = new byte[32], Scopes = ["*"] };

        Assert.True(narrow.HasScope("servers:read"));
        Assert.False(narrow.HasScope("servers:write"));
        Assert.True(wide.HasScope("anything"));
    }

    [Fact]
    public void UserSession_IsActiveOnlyBeforeExpiryAndBeforeRevocation()
    {
        var session = new UserSession { SecretHash = new byte[32], ExpiresAt = T0.AddHours(1) };
        Assert.True(session.IsActive(T0));
        Assert.False(session.IsActive(T0.AddHours(2)));
        session.Revoke(T0);
        Assert.False(session.IsActive(T0));
    }

    private static byte[] Hash() => new byte[JoinToken.HashLength];

    [Fact]
    public void JoinToken_DefaultsToOneHourTtlAndIsSingleUse()
    {
        var token = JoinToken.Issue(Guid.CreateVersion7(), Hash(), T0);

        Assert.Equal(T0.AddHours(1), token.ExpiresAt);
        Assert.True(token.IsUsable(T0));
        token.Consume(T0.AddMinutes(5));
        Assert.False(token.IsUsable(T0.AddMinutes(6)));
        Assert.Throws<DomainRuleException>(() => token.Consume(T0.AddMinutes(6)));
    }

    [Fact]
    public void JoinToken_ExpiresAndHonoursMaximumTtl()
    {
        var token = JoinToken.Issue(Guid.CreateVersion7(), Hash(), T0, TimeSpan.FromMinutes(10));
        Assert.Throws<DomainRuleException>(() => token.Consume(T0.AddMinutes(11)));

        Assert.NotNull(JoinToken.Issue(Guid.CreateVersion7(), Hash(), T0, TimeSpan.FromHours(24)));
        Assert.Throws<DomainRuleException>(() => JoinToken.Issue(Guid.CreateVersion7(), Hash(), T0, TimeSpan.FromHours(25)));
        Assert.Throws<DomainRuleException>(() => JoinToken.Issue(Guid.CreateVersion7(), Hash(), T0, TimeSpan.Zero));
    }

    [Fact]
    public void JoinToken_RequiresSha256Hash() =>
        Assert.Throws<DomainRuleException>(() => JoinToken.Issue(Guid.CreateVersion7(), new byte[5], T0));

    [Fact]
    public void JoinToken_CannotBeUsedAfterRevocation()
    {
        var token = JoinToken.Issue(Guid.CreateVersion7(), Hash(), T0);
        token.Revoke(T0);
        Assert.False(token.IsUsable(T0));
    }

    [Fact]
    public void AgentCertificate_RevocationInvalidatesIt()
    {
        var cert = new AgentCertificate
        {
            Serial = "0a1b", FingerprintSha256 = "ff", SubjectUri = "spiffe://aethera/server/x",
            NotBefore = T0, NotAfter = T0.AddDays(30),
        };

        Assert.True(cert.IsValid(T0.AddDays(1)));
        Assert.False(cert.IsValid(T0.AddDays(31)));

        cert.Revoke("server deleted", T0.AddDays(2));
        cert.Revoke("again", T0.AddDays(3)); // idempotent
        Assert.False(cert.IsValid(T0.AddDays(3)));
        Assert.Equal("server deleted", cert.RevokedReason);
    }
}

public sealed class ModelHelperTests
{
    [Theory]
    [InlineData("Turn One", "turn-one")]
    [InlineData("  API  (dev) ", "api-dev")]
    [InlineData("Ünïcode--Name", "n-code-name")]
    [InlineData("---", "")]
    public void Slug_FromName(string input, string expected) => Assert.Equal(expected, Slug.FromName(input));

    [Theory]
    [InlineData("api", true)]
    [InlineData("turn-one-2", true)]
    [InlineData("-bad", false)]
    [InlineData("bad-", false)]
    [InlineData("Bad", false)]
    [InlineData("a--b", false)]
    [InlineData("", false)]
    public void Slug_IsValid(string input, bool expected) => Assert.Equal(expected, Slug.IsValid(input));

    [Fact]
    public void Slug_FromName_RespectsMaxLength() =>
        Assert.True(Slug.FromName(new string('a', 200)).Length <= Slug.MaxLength);

    [Theory]
    [InlineData("DATABASE_URL", true)]
    [InlineData("_x1", true)]
    [InlineData("1ABC", false)]
    [InlineData("A-B", false)]
    [InlineData("", false)]
    public void EnvironmentVariable_KeyValidation(string key, bool expected) =>
        Assert.Equal(expected, EnvironmentVariable.IsValidKey(key));

    [Fact]
    public void Domain_NormalizesHostname()
    {
        var domain = new WorkloadDomain();
        domain.SetHostname("  API.Example.COM. ");
        Assert.Equal("api.example.com", domain.Hostname);
    }

    [Fact]
    public void Secret_ScopeIsDerivedFromForeignKeys()
    {
        Assert.Equal(SecretScope.Organization, new Secret { Name = "s" }.Scope);
        var s = new Secret { Name = "s", ProjectId = Guid.CreateVersion7() };
        Assert.Equal(SecretScope.Project, s.Scope);
        s.WorkloadId = Guid.CreateVersion7();
        Assert.Equal(SecretScope.Workload, s.Scope);
    }

    [Fact]
    public void Secret_AddVersionIncrementsAndTracksRotation()
    {
        var t0 = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var secret = new Secret { Name = "DB_PASSWORD" };

        var v1 = secret.AddVersion([1], [2], [3], [4], masterKeyVersion: 1, t0);
        Assert.Equal(1, v1.Version);
        Assert.Null(secret.RotatedAt);

        var v2 = secret.AddVersion([5], [6], [7], [8], masterKeyVersion: 1, t0.AddDays(1));
        Assert.Equal(2, v2.Version);
        Assert.Equal(2, secret.CurrentVersion);
        Assert.Equal(t0.AddDays(1), secret.RotatedAt);
        Assert.Equal(secret.Id, v2.SecretId);
        Assert.Equal(2, secret.Versions.Count);
    }

    [Fact]
    public void SoftDelete_IsIdempotentAndRestorable()
    {
        var t0 = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var project = new Project { Name = "P", Slug = "p" };

        project.MarkDeleted(t0);
        project.MarkDeleted(t0.AddDays(1));
        Assert.True(project.IsDeleted);
        Assert.Equal(t0, project.DeletedAt);

        project.Restore(t0.AddDays(2));
        Assert.False(project.IsDeleted);
    }

    [Fact]
    public void NewWorkloadDefaults_AreSensible()
    {
        var service = new Service { Name = "pg", Slug = "pg", TemplateKey = "postgres", Image = "postgres:17" };

        Assert.Equal(RestartPolicy.UnlessStopped, service.Runtime.RestartPolicy);
        Assert.Equal(DeploymentStrategies.Recreate, service.Runtime.DeploymentStrategy);
        Assert.Equal(HealthCheckType.None, service.Runtime.HealthCheck.Type);
        Assert.Equal(DesiredState.Running, service.DesiredState);
    }

    [Fact]
    public void Server_HeartbeatUpdatesAgentReachabilityAndDockerAxesIndependently()
    {
        var t0 = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var server = new Server { Name = "s", Host = "10.0.0.1", Roles = [ServerRole.Master, ServerRole.Build] };

        server.RecordHeartbeat(t0, DockerStatus.Running);

        Assert.Equal(AgentStatus.Connected, server.AgentStatus);
        Assert.Equal(ReachabilityStatus.Reachable, server.ReachabilityStatus);
        Assert.Equal(DockerStatus.Running, server.DockerStatus);
        Assert.Equal(t0, server.LastHeartbeatAt);
        Assert.True(server.HasRole(ServerRole.Build));
        Assert.False(server.HasRole(ServerRole.Storage));
    }

    [Fact]
    public void Server_StatusSettersReportChangesAndOnlyMoveTimestampsOnChange()
    {
        var t0 = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var server = new Server { Name = "s", Host = "h" };

        Assert.True(server.SetAgentStatus(AgentStatus.Unavailable, t0));
        Assert.False(server.SetAgentStatus(AgentStatus.Unavailable, t0.AddMinutes(1)));
        Assert.Equal(t0, server.AgentStatusChangedAt);

        // A reachability probe always records the check time but the "changed" stamp only moves on change.
        Assert.True(server.SetReachability(ReachabilityStatus.Reachable, t0));
        Assert.False(server.SetReachability(ReachabilityStatus.Reachable, t0.AddMinutes(1)));
        Assert.Equal(t0, server.ReachabilityChangedAt);
        Assert.Equal(t0.AddMinutes(1), server.ReachabilityCheckedAt);

        Assert.True(server.SetDockerStatus(DockerStatus.PermissionDenied, t0));
        Assert.Equal(AgentStatus.Unavailable, server.AgentStatus); // axes do not affect each other
    }
}
