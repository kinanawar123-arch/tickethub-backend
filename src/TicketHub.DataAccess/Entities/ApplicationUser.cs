using Microsoft.AspNetCore.Identity;
using TicketHub.Contracts.Enums;

namespace TicketHub.DataAccess.Entities;

/// <summary>
/// A login. Extends ASP.NET Core Identity's user with the few fields our domain needs.
/// </summary>
/// <remarks>
/// WHY USE IDENTITY INSTEAD OF ROLLING OUR OWN USER TABLE?
/// Everything below is already built, tested and maintained by Microsoft, and every item on
/// the list is something people get wrong when they write it themselves:
/// <list type="bullet">
/// <item><b>Password hashing</b> — PBKDF2, per-user salt, high iteration count, and a
///       versioned format so the algorithm can be upgraded without logging everyone out.</item>
/// <item><b>NormalizedEmail / NormalizedUserName</b> — so <c>Wael@x.com</c> and
///       <c>wael@x.com</c> are the same account regardless of the database's collation. This
///       is a real bug the moment you move from SQL Server's case-insensitive default to
///       PostgreSQL.</item>
/// <item><b>SecurityStamp</b> — changes on password reset or forced logout. We copy it into
///       the JWT, so every token issued before the change stops validating. This is how you
///       revoke a token in a stateless system.</item>
/// <item><b>Lockout</b> — <c>AccessFailedCount</c> and <c>LockoutEnd</c> give brute-force
///       defence for free.</item>
/// <item><b>Two-factor, email confirmation, external logins</b> — the columns already exist,
///       so switching them on later is configuration, not a migration and a rewrite.</item>
/// </list>
/// <para/>
/// The generic argument <c>&lt;int&gt;</c> makes the key an int instead of Identity's default
/// string GUID. Ints are narrower in every index and every foreign key, they join faster, and
/// they match the rest of our model. The cost is that ids are guessable — which does not
/// matter, because authorization is what stops you reading row 42, not the difficulty of
/// guessing the number 42.
/// </remarks>
public class ApplicationUser : IdentityUser<int>
{
    /// <summary>The name we show in the UI. Identity's UserName holds the email.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>What this person is to the business — see <see cref="Contracts.Enums.UserType"/>.</summary>
    public UserType UserType { get; set; } = UserType.Citizen;

    /// <summary>
    /// False = cannot log in. We deactivate rather than delete, because their tickets,
    /// comments and audit rows still point here and should keep resolving to a name.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    /// <summary>Updated on every successful login. Handy for "who is still using this system".</summary>
    public DateTime? LastLoginAt { get; set; }

    // ---------------------------------------------------------------------
    // Navigations
    // ---------------------------------------------------------------------

    /// <summary>
    /// Optional 1:1. A staff user has an <see cref="Entities.Agent"/> row; a citizen does not.
    /// </summary>
    public Agent? Agent { get; set; }

    /// <summary>Their live refresh tokens. Cascade-deleted with the user.</summary>
    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();

    /// <summary>Tickets this user reported.</summary>
    public ICollection<Ticket> ReportedTickets { get; set; } = new List<Ticket>();

    public ICollection<TicketComment> Comments { get; set; } = new List<TicketComment>();
    public ICollection<Notification> Notifications { get; set; } = new List<Notification>();
    public ICollection<ConversationParticipant> Conversations { get; set; } = new List<ConversationParticipant>();

    // NOTE: ApplicationUser does NOT inherit AuditableEntity. Identity already owns this
    // table's shape, and a soft-delete filter on the users table would fight with Identity's
    // own lookups (it queries by normalized email without going through our filter).
    // IsActive is the equivalent here, and it is checked explicitly at login.
}

/// <summary>
/// A role. Identity's <c>IdentityRole&lt;int&gt;</c> plus a human description.
/// </summary>
/// <remarks>
/// Roles answer "what may you do", coarsely. The four we seed are Admin, Supervisor, Agent
/// and Citizen. When "may you do X" starts depending on the row rather than the person —
/// may you read <em>this</em> ticket — a role stops being enough and you want a policy or a
/// resource check. <c>TicketService</c> shows both.
/// </remarks>
public class ApplicationRole : IdentityRole<int>
{
    public string? Description { get; set; }

    public ApplicationRole() { }

    public ApplicationRole(string name) : base(name) { }
}
