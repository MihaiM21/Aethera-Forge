namespace Aethera.Domain;

/// <summary>Base for every entity with a surrogate key. Ids are client-generated UUIDv7 (time-ordered).</summary>
public abstract class Entity
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
}

/// <summary>
/// Entity that can be modified after creation. <see cref="UpdatedAt"/> is stamped by the DbContext on
/// every update; <see cref="RowVersion"/> maps to Postgres <c>xmin</c> and is the optimistic-concurrency token.
/// </summary>
public abstract class MutableEntity : Entity
{
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Postgres <c>xmin</c> system column. Never set by application code.</summary>
    public uint RowVersion { get; private set; }
}

/// <summary>Soft-deletable entity. A global query filter hides rows where <see cref="DeletedAt"/> is set.</summary>
public interface ISoftDeletable
{
    DateTimeOffset? DeletedAt { get; }
}

public abstract class SoftDeletableEntity : MutableEntity, ISoftDeletable
{
    public DateTimeOffset? DeletedAt { get; private set; }

    public bool IsDeleted => DeletedAt is not null;

    public void MarkDeleted(DateTimeOffset now)
    {
        DeletedAt ??= now;
        UpdatedAt = now;
    }

    public void Restore(DateTimeOffset now)
    {
        DeletedAt = null;
        UpdatedAt = now;
    }
}
