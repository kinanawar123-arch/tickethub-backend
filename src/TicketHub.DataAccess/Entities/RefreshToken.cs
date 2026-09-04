namespace TicketHub.DataAccess.Entities;

/// <summary>
/// A long-lived, single-use token that can be traded for a new access token.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS AT ALL.
/// A JWT cannot be revoked — the server never stores it, it only checks the signature. So you
/// want it short-lived (15 minutes here). But nobody will accept being logged out every 15
/// minutes, so we need something long-lived to get a fresh one with. That something has to be
/// revocable, which means it has to be <em>stored</em>. Hence this table.
/// <para/>
/// The trade in one line: the short token is cheap to verify and hard to revoke; the long one
/// is expensive to verify (a database hit) and easy to revoke. Use each where it fits.
/// <para/>
/// ROTATION. Every refresh consumes the old token and issues a new one, and the old row keeps
/// a pointer to its replacement. If a consumed token is ever presented again, the only
/// explanations are a bug or a thief — so <c>AuthService</c> revokes the whole chain and
/// forces a real login. This is called refresh token rotation with reuse detection, and it
/// turns "someone stole a token" from silent into loud.
/// </remarks>
public class RefreshToken
{
    public int Id { get; set; }

    /// <summary>
    /// Cryptographically random, not a JWT and not guessable.
    /// </summary>
    /// <remarks>
    /// Generated with <c>RandomNumberGenerator</c>, never <c>Random</c>. <c>Random</c> is a
    /// predictable pseudo-random sequence; given a couple of outputs you can compute the rest.
    /// That is fine for shuffling a deck and catastrophic for a credential.
    /// </remarks>
    public string Token { get; set; } = string.Empty;

    public int UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    /// <summary>Set the moment it is used or revoked. Non-null means dead.</summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>The token issued in its place. Lets us walk a chain during reuse detection.</summary>
    public string? ReplacedByToken { get; set; }

    /// <summary>"rotated", "logout", "reuse detected", "password changed" — for the security log.</summary>
    public string? RevokedReason { get; set; }

    /// <summary>Where it came from, for the "recent sign-ins" screen and for forensics.</summary>
    public string? CreatedByIp { get; set; }

    public string? UserAgent { get; set; }

    // Computed helpers. Not mapped — they are C# conveniences, and EF cannot translate them
    // into SQL, so query code spells the conditions out instead.
    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
    public bool IsRevoked => RevokedAt.HasValue;
    public bool IsActive => !IsRevoked && !IsExpired;
}
