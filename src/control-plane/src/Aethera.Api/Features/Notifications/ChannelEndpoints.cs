using Aethera.Api.Features.Resources;
using Aethera.Api.Features.Resources.Secrets;
using Aethera.Api.Http;
using Aethera.Api.Http.Errors;
using Aethera.Api.Http.Pagination;
using Aethera.Api.Security;
using Aethera.Domain;
using Aethera.Infrastructure.Notifications;
using Aethera.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aethera.Api.Features.Notifications;

public sealed record CreateChannelRequest
{
    public string? Name { get; init; }

    /// <summary><c>email</c> or <c>webhook</c>.</summary>
    public string? Kind { get; init; }

    public bool? Enabled { get; init; }

    /// <summary>Email: also send to every administrator and owner (default true).</summary>
    public bool? SendToMembers { get; init; }

    /// <summary>Email: extra addresses.</summary>
    public List<string>? EmailRecipients { get; init; }

    /// <summary>Webhook: the https URL that receives a signed JSON POST.</summary>
    public string? WebhookUrl { get; init; }

    /// <summary>Event types to receive (<c>GET /notification-events</c>); default: everything except the success events.</summary>
    public List<string>? Events { get; init; }

    /// <summary>Only events of workloads in this project.</summary>
    public Guid? ProjectId { get; init; }

    /// <summary>Only events of workloads in this environment.</summary>
    public Guid? EnvironmentId { get; init; }
}

public sealed record UpdateChannelRequest
{
    [NotClearable] public string? Name { get; init; }
    [NotClearable] public bool? Enabled { get; init; }
    [NotClearable] public bool? SendToMembers { get; init; }
    [NotClearable] public List<string>? EmailRecipients { get; init; }
    [NotClearable] public string? WebhookUrl { get; init; }
    [NotClearable] public List<string>? Events { get; init; }
    public Guid? ProjectId { get; init; }
    public Guid? EnvironmentId { get; init; }
}

/// <summary>
/// A notification channel. <c>webhookSecret</c> is the HMAC signing secret and is present <b>only</b> in the response that created it (or rotated it):
/// it cannot be read again, <c>hasWebhookSecret</c> only says one exists.
/// </summary>
public sealed record ChannelResponse(
    Guid Id, string Name, NotificationChannelKind Kind, bool Enabled, bool SendToMembers, IReadOnlyList<string> EmailRecipients, string? WebhookUrl,
    bool HasWebhookSecret, IReadOnlyList<string> Events, Guid? ProjectId, Guid? EnvironmentId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? WebhookSecret = null);

public sealed record ChannelTestResponse(bool Ok, string? Error);

public sealed record DeliveryResponse(Guid Id, Guid ChannelId, Guid? AlertId, string EventType, NotificationDeliveryStatus Status, string? Error, DateTimeOffset CreatedAt);

internal static class ChannelRules
{
    public const int MaxRecipients = 50;

    public static void Events<T>(IRuleBuilderInitial<T, List<string>?> rule) =>
        rule.Must(e => e is null || e.Count > 0).WithErrorCode("required").WithMessage("Choose at least one event.")
            .Must(e => e is null || e.All(NotificationEvents.All.Contains)).WithErrorCode("invalid_enum")
            .WithMessage("Unknown event type. See GET /api/v1/notification-events.");

    public static void Recipients<T>(IRuleBuilderInitial<T, List<string>?> rule) =>
        rule.Must(r => r is null || r.Count <= MaxRecipients).WithErrorCode("too_long").WithMessage($"At most {MaxRecipients} addresses.")
            .Must(r => r is null || r.All(a => NotificationRules.IsEmail(a?.Trim()))).WithErrorCode("email").WithMessage("Every recipient must be an email address.");

    public static IRuleBuilderOptions<T, string?> WebhookUrl<T>(IRuleBuilder<T, string?> rule, IOptions<NotificationOptions> options) =>
        rule.MaximumLength(2000).Must(url => WebhookTargetPolicy.Check(url, options.Value.AllowPrivateWebhookTargets) is null).WithErrorCode("pattern")
            .WithMessage((_, url) => WebhookTargetPolicy.Check(url, options.Value.AllowPrivateWebhookTargets) ?? "Invalid URL.");
}

public sealed class CreateChannelValidator : AbstractValidator<CreateChannelRequest>
{
    public CreateChannelValidator(IOptions<NotificationOptions> options)
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Kind).NotEmpty().MustBeEnum<CreateChannelRequest, NotificationChannelKind>();
        ChannelRules.Events(RuleFor(x => x.Events));
        ChannelRules.Recipients(RuleFor(x => x.EmailRecipients));
        RuleFor(x => x.WebhookUrl).NotEmpty().When(x => EnumText.Parse<NotificationChannelKind>(x.Kind) == NotificationChannelKind.Webhook);
        ChannelRules.WebhookUrl(RuleFor(x => x.WebhookUrl), options).When(x => !string.IsNullOrEmpty(x.WebhookUrl));
    }
}

public sealed class UpdateChannelValidator : AbstractValidator<UpdateChannelRequest>
{
    public UpdateChannelValidator(IOptions<NotificationOptions> options)
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200).When(x => x.Name is not null);
        ChannelRules.Events(RuleFor(x => x.Events));
        ChannelRules.Recipients(RuleFor(x => x.EmailRecipients));
        ChannelRules.WebhookUrl(RuleFor(x => x.WebhookUrl), options).When(x => x.WebhookUrl is not null);
    }
}

internal static class ChannelEndpoints
{
    private static readonly SortDefinition<NotificationChannel> Sorts = new SortDefinition<NotificationChannel>("name")
        .Add("name", c => c.Name).Add("createdAt", c => c.CreatedAt);

    private static readonly SortDefinition<NotificationDelivery> DeliverySorts = new SortDefinition<NotificationDelivery>("-createdAt")
        .Add("createdAt", d => d.CreatedAt);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/notification-events", Events).WithName("listNotificationEvents").WithTags("Notifications").RequireRead();

        var group = api.MapGroup("/notification-channels").WithTags("Notifications");
        group.MapGet("/", List).WithName("listNotificationChannels").RequireAdmin(Scopes.Read);
        group.MapPost("/", Create).WithName("createNotificationChannel").Validate<CreateChannelRequest>().RequireAdmin();
        group.MapGet("/{id:guid}", Get).WithName("getNotificationChannel").RequireAdmin(Scopes.Read);
        group.MapPatch("/{id:guid}", Update).WithName("updateNotificationChannel")
            .Accepts<UpdateChannelRequest>("application/merge-patch+json", "application/json")
            .ValidatePatch<UpdateChannelRequest>().RequireAdmin();
        group.MapDelete("/{id:guid}", Delete).WithName("deleteNotificationChannel").RequireAdmin();
        group.MapPost("/{id:guid}/test", Test).WithName("testNotificationChannel").RequireAdmin();
        // A new signing secret is a credential: a token also needs secrets:write.
        group.MapPost("/{id:guid}/rotate-secret", RotateSecret).WithName("rotateNotificationChannelSecret").RequireAdmin(Scopes.SecretsWrite);

        api.MapGet("/notification-deliveries", Deliveries).WithName("listNotificationDeliveries").WithTags("Notifications").RequireAdmin(Scopes.Read);
    }

    private static Ok<NotificationEventsResponse> Events() => TypedResults.Ok(new NotificationEventsResponse(NotificationEventCatalog.All));

    private static async Task<Ok<Page<ChannelResponse>>> List(
        HttpContext http, AetheraDbContext db, ICurrentActor actor, KeysetCursor cursors, [AsParameters] PageRequest page, string? sort, CancellationToken ct)
    {
        http.RejectUnknownQuery();
        var query = db.NotificationChannels.AsNoTracking().Where(c => c.OrganizationId == actor.Org());
        var (items, next) = await Sorts.PageAsync(query, sort, page, cursors, "", ct);
        return TypedResults.Ok(new Page<ChannelResponse>(items.Select(c => ToResponse(c)).ToList(), next));
    }

    private static async Task<Created<ChannelResponse>> Create(
        CreateChannelRequest request, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, SecretVault vault, CancellationToken ct)
    {
        var org = actor.Org();
        var kind = EnumText.Parse<NotificationChannelKind>(request.Kind)!.Value;
        var name = request.Name!.Trim();
        await EnsureNameFreeAsync(db, org, name, null, ct);
        await EnsureScopeAsync(db, org, request.ProjectId, request.EnvironmentId, ct);

        var channel = new NotificationChannel
        {
            OrganizationId = org, Name = name, Kind = kind, Enabled = request.Enabled ?? true, SendToMembers = request.SendToMembers ?? true,
            EmailRecipients = Distinct(request.EmailRecipients), Events = request.Events?.Distinct().ToList() ?? [.. NotificationEvents.Defaults],
            ProjectId = request.ProjectId, EnvironmentId = request.EnvironmentId,
        };

        string? secretValue = null;
        if (kind == NotificationChannelKind.Webhook)
        {
            ManagedSecrets.RequireTokenScope(actor);
            channel.WebhookUrl = request.WebhookUrl!.Trim();
            secretValue = NewSecretValue();
            var secret = vault.Create(org, SecretName(channel.Id), $"Signing secret of the notification channel '{name}'.", secretValue, purpose: SecretPurpose.NotificationCredential);
            channel.WebhookSecretId = secret.Id;
        }

        db.NotificationChannels.Add(channel);
        await audit.RecordAsync("notification_channel.created", "notification_channel", channel.Id,
            new { name, kind = kind.ToString().ToLowerInvariant(), events = channel.Events.Count }, ct);
        http.SetETag(channel.RowVersion);
        return TypedResults.Created(ResourceHttp.Path("notification-channels", channel.Id), ToResponse(channel, secretValue));
    }

    private static async Task<Ok<ChannelResponse>> Get(Guid id, HttpContext http, AetheraDbContext db, ICurrentActor actor, CancellationToken ct)
    {
        var channel = await FindAsync(db, actor.Org(), id, tracking: false, ct);
        http.SetETag(channel.RowVersion);
        return TypedResults.Ok(ToResponse(channel));
    }

    private static async Task<Ok<ChannelResponse>> Update(
        Guid id, PatchRequest<UpdateChannelRequest> patch, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit,
        IOptions<NotificationOptions> options, CancellationToken ct)
    {
        var org = actor.Org();
        var channel = await FindAsync(db, org, id, tracking: true, ct);
        http.CheckIfMatch(channel.RowVersion);
        var body = patch.Body;
        var changed = new List<string>();

        if (patch.Has("name") && body.Name is { } name && name.Trim() != channel.Name)
        {
            await EnsureNameFreeAsync(db, org, name.Trim(), id, ct);
            channel.Name = name.Trim();
            changed.Add("name");
        }

        if (patch.Has("enabled") && body.Enabled is { } enabled) { channel.Enabled = enabled; changed.Add("enabled"); }
        if (patch.Has("sendToMembers") && body.SendToMembers is { } members) { channel.SendToMembers = members; changed.Add("sendToMembers"); }
        if (patch.Has("emailRecipients") && body.EmailRecipients is { } recipients) { channel.EmailRecipients = Distinct(recipients); changed.Add("emailRecipients"); }
        if (patch.Has("events") && body.Events is { } events) { channel.Events = events.Distinct().ToList(); changed.Add("events"); }

        if (patch.Has("webhookUrl") && body.WebhookUrl is { } url)
        {
            if (channel.Kind != NotificationChannelKind.Webhook)
                throw new ApiProblemException(ApiProblems.Validation([FieldError.AtPointer("/webhookUrl", "invalid", "Only webhook channels have a URL.")]));
            channel.WebhookUrl = url.Trim();
            changed.Add("webhookUrl");
        }

        if (patch.Has("projectId") || patch.Has("environmentId"))
        {
            var projectId = patch.Has("projectId") ? body.ProjectId : channel.ProjectId;
            var environmentId = patch.Has("environmentId") ? body.EnvironmentId : channel.EnvironmentId;
            await EnsureScopeAsync(db, org, projectId, environmentId, ct);
            channel.ProjectId = projectId;
            channel.EnvironmentId = environmentId;
            changed.Add("filter");
        }

        await audit.RecordAsync("notification_channel.updated", "notification_channel", id, new { changed }, ct);
        http.SetETag(channel.RowVersion);
        return TypedResults.Ok(ToResponse(channel));
    }

    private static async Task<NoContent> Delete(Guid id, HttpContext http, AetheraDbContext db, ICurrentActor actor, IAuditLog audit, IClock clock, CancellationToken ct)
    {
        var channel = await FindAsync(db, actor.Org(), id, tracking: true, ct);
        http.CheckIfMatch(channel.RowVersion);
        var now = clock.UtcNow;
        channel.MarkDeleted(now);
        if (channel.WebhookSecretId is { } secretId && await db.Secrets.FirstOrDefaultAsync(s => s.Id == secretId, ct) is { } secret) secret.MarkDeleted(now);
        await audit.RecordAsync("notification_channel.deleted", "notification_channel", id, new { name = channel.Name }, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<ChannelTestResponse>> Test(
        Guid id, AetheraDbContext db, ICurrentActor actor, NotificationDispatcher dispatcher, IClock clock, IAuditLog audit, CancellationToken ct)
    {
        var org = actor.Org();
        var channel = await FindAsync(db, org, id, tracking: false, ct);
        var now = clock.UtcNow;
        var payload = new NotificationPayload
        {
            OrganizationId = org, ChannelId = id, Type = "test", Kind = NotificationKinds.Test, Severity = AlertSeverity.Info, Title = "Test notification",
            Message = $"This is a test of the notification channel '{channel.Name}'. Nothing is wrong.", OccurredAt = now, Path = "/settings",
        };
        var result = await dispatcher.DeliverAsync(channel, payload, Guid.NewGuid(), ct);
        db.NotificationDeliveries.Add(new NotificationDelivery
        {
            OrganizationId = org, ChannelId = id, EventType = "test", Status = result.Ok ? NotificationDeliveryStatus.Sent : NotificationDeliveryStatus.Failed,
            Error = result.Ok ? null : Cut(result.Error ?? "The test failed.", 2000), CreatedAt = now,
        });
        await audit.RecordAsync("notification_channel.tested", "notification_channel", id, new { ok = result.Ok }, ct);
        return TypedResults.Ok(new ChannelTestResponse(result.Ok, result.Ok ? null : result.Error));
    }

    private static async Task<Ok<ChannelResponse>> RotateSecret(
        Guid id, AetheraDbContext db, ICurrentActor actor, SecretVault vault, IAuditLog audit, CancellationToken ct)
    {
        var org = actor.Org();
        var channel = await FindAsync(db, org, id, tracking: true, ct);
        if (channel.Kind != NotificationChannelKind.Webhook || channel.WebhookSecretId is not { } secretId)
            throw new ApiProblemException(ApiProblems.Conflict("notification_channel.no_secret", "Only webhook channels have a signing secret."));
        var value = NewSecretValue();
        vault.AddVersion(await db.Secrets.FirstAsync(s => s.Id == secretId, ct), value);
        await audit.RecordAsync("notification_channel.secret_rotated", "notification_channel", id, new { name = channel.Name }, ct);
        return TypedResults.Ok(ToResponse(channel, value));
    }

    private static async Task<Ok<Page<DeliveryResponse>>> Deliveries(
        HttpContext http, AetheraDbContext db, ICurrentActor actor, KeysetCursor cursors, [AsParameters] PageRequest page, Guid? channelId, string? sort, CancellationToken ct)
    {
        http.RejectUnknownQuery("channelId");
        var org = actor.Org();
        var query = db.NotificationDeliveries.AsNoTracking().Where(d => d.OrganizationId == org);
        if (channelId is { } channel) query = query.Where(d => d.ChannelId == channel);
        var (items, next) = await DeliverySorts.PageAsync(query, sort, page, cursors, $"channelId={channelId}", ct);
        return TypedResults.Ok(new Page<DeliveryResponse>(items.Select(d => new DeliveryResponse(d.Id, d.ChannelId, d.AlertId, d.EventType, d.Status, d.Error, d.CreatedAt)).ToList(), next));
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------------------------

    private static async Task<NotificationChannel> FindAsync(AetheraDbContext db, Guid org, Guid id, bool tracking, CancellationToken ct)
    {
        var query = db.NotificationChannels.Where(c => c.OrganizationId == org);
        if (!tracking) query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new ApiProblemException(ApiProblems.NotFound("notification_channel", id));
    }

    private static async Task EnsureNameFreeAsync(AetheraDbContext db, Guid org, string name, Guid? exceptId, CancellationToken ct)
    {
        if (await db.NotificationChannels.AnyAsync(c => c.OrganizationId == org && c.Name == name && c.Id != exceptId, ct))
            throw new ApiProblemException(ApiProblems.AlreadyExists("notification_channel", $"A notification channel named '{name}' already exists."));
    }

    /// <summary>The project and environment of a filter must exist in the organization, and the environment must belong to the project.</summary>
    private static async Task EnsureScopeAsync(AetheraDbContext db, Guid org, Guid? projectId, Guid? environmentId, CancellationToken ct)
    {
        var check = new ReferenceCheck();
        if (projectId is { } p) check.Check(await db.ProjectsOf(org).FirstOrDefaultAsync(x => x.Id == p, ct), "/projectId", "project");
        if (environmentId is { } e)
        {
            var env = check.Check(await db.EnvironmentsOf(org).FirstOrDefaultAsync(x => x.Id == e, ct), "/environmentId", "environment");
            if (env is not null && projectId is { } pid && env.ProjectId != pid)
                throw new ApiProblemException(ApiProblems.Validation([FieldError.AtPointer("/environmentId", "scope_mismatch", "The environment belongs to another project.")]));
        }

        check.ThrowIfAny();
    }

    private static List<string> Distinct(IEnumerable<string>? addresses) =>
        (addresses ?? []).Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string NewSecretValue() => "whsec_" + SecretVault.GeneratePassword(40);

    private static string SecretName(Guid channelId) => $"notifications/webhook/{channelId.ToString("N")[^12..]}";

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];

    internal static ChannelResponse ToResponse(NotificationChannel c, string? secret = null) => new(
        c.Id, c.Name, c.Kind, c.Enabled, c.SendToMembers, c.EmailRecipients, c.WebhookUrl, c.WebhookSecretId is not null, c.Events, c.ProjectId, c.EnvironmentId,
        c.CreatedAt, c.UpdatedAt, secret);
}
