namespace Aethera.Domain;

public enum OrganizationRole
{
    Viewer = 0,
    Developer = 1,
    Admin = 2,
    Owner = 3,
}

public class OrganizationMember : MutableEntity
{
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public OrganizationRole Role { get; set; } = OrganizationRole.Viewer;
}
