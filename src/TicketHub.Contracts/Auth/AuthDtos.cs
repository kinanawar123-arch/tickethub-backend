using System.ComponentModel.DataAnnotations;
using TicketHub.Contracts.Enums;

namespace TicketHub.Contracts.Auth;

/// <summary>Body of <c>POST /api/auth/register</c>.</summary>
/// <remarks>
/// Registration always creates a <see cref="UserType.Citizen"/>. There is no "Role" property
/// here on purpose — if there were, anyone could register themselves as an Admin. Staff
/// accounts are created by an Admin through <c>POST /api/users</c>, which is behind
/// <c>[Authorize(Roles = "Admin")]</c>.
/// </remarks>
public class RegisterDto
{
    [Required]
    [EmailAddress]
    [StringLength(160)]
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Plain text over HTTPS, exactly once, and never stored. ASP.NET Core Identity hashes it
    /// with PBKDF2 (many iterations, per-user salt) before it goes anywhere near the database.
    /// The rules — length, digits, symbols — are configured centrally in Program.cs so they
    /// live in one place instead of being duplicated in an attribute here.
    /// </summary>
    [Required]
    [StringLength(100, MinimumLength = 8, ErrorMessage = "Password must be at least 8 characters.")]
    public string Password { get; set; } = string.Empty;

    [Required]
    [Compare(nameof(Password), ErrorMessage = "The two passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;

    [Required]
    [StringLength(120, MinimumLength = 2)]
    public string DisplayName { get; set; } = string.Empty;

    [Phone]
    [StringLength(30)]
    public string? PhoneNumber { get; set; }
}

/// <summary>Body of <c>POST /api/auth/login</c>.</summary>
public class LoginDto
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;
}

/// <summary>What a successful login or refresh hands back.</summary>
public class AuthResponseDto
{
    /// <summary>
    /// The JWT. Short-lived (see <c>JwtOptions.AccessTokenMinutes</c>) because it cannot be
    /// revoked — the server does not store it, it only verifies the signature. A stolen
    /// access token is valid until it expires, so the window is kept small.
    /// </summary>
    public string AccessToken { get; init; } = string.Empty;

    /// <summary>
    /// Long-lived, single-use, and stored in the database — so it CAN be revoked.
    /// This is the trade: the short token is fast to check, the long one is controllable.
    /// </summary>
    public string RefreshToken { get; init; } = string.Empty;

    public DateTime AccessTokenExpiresAt { get; init; }
    public DateTime RefreshTokenExpiresAt { get; init; }

    public UserDto User { get; init; } = new();
}

/// <summary>Body of <c>POST /api/auth/refresh</c>.</summary>
public class RefreshTokenRequestDto
{
    [Required]
    public string RefreshToken { get; set; } = string.Empty;
}

public class ChangePasswordDto
{
    [Required]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required]
    [StringLength(100, MinimumLength = 8)]
    public string NewPassword { get; set; } = string.Empty;

    [Required]
    [Compare(nameof(NewPassword))]
    public string ConfirmNewPassword { get; set; } = string.Empty;
}

/// <summary>A user as the API describes them. Note what is missing.</summary>
/// <remarks>
/// There is no <c>PasswordHash</c>, no <c>SecurityStamp</c>, no <c>AccessFailedCount</c>.
/// Those are all real properties on the entity. They are absent here because a DTO is the
/// only thing that ever reaches a caller, and the safest secret is one that was never in
/// the object you serialised.
/// </remarks>
public class UserDto
{
    public int Id { get; init; }
    public string Email { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? PhoneNumber { get; init; }
    public UserType UserType { get; init; }
    public bool IsActive { get; init; }

    /// <summary>The Identity roles this user holds: Admin, Supervisor, Agent, Citizen.</summary>
    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();

    /// <summary>Set when this user is also an agent. Null for citizens.</summary>
    public int? AgentId { get; init; }
    public int? DepartmentId { get; init; }
    public string? DepartmentName { get; init; }

    public DateTime CreatedAt { get; init; }
    public DateTime? LastLoginAt { get; init; }
}

/// <summary>Admin-only: create a staff login.</summary>
public class CreateUserDto
{
    [Required, EmailAddress, StringLength(160)]
    public string Email { get; set; } = string.Empty;

    [Required, StringLength(100, MinimumLength = 8)]
    public string Password { get; set; } = string.Empty;

    [Required, StringLength(120, MinimumLength = 2)]
    public string DisplayName { get; set; } = string.Empty;

    [Phone, StringLength(30)]
    public string? PhoneNumber { get; set; }

    public UserType UserType { get; set; } = UserType.Employee;

    /// <summary>Must be existing role names. The service rejects unknown ones rather than inventing them.</summary>
    public List<string> Roles { get; set; } = new();

    /// <summary>Required when the user is an Employee — it becomes their agent's department.</summary>
    public int? DepartmentId { get; set; }
}

public class UpdateUserDto
{
    [Required, StringLength(120, MinimumLength = 2)]
    public string DisplayName { get; set; } = string.Empty;

    [Phone, StringLength(30)]
    public string? PhoneNumber { get; set; }

    public bool IsActive { get; set; } = true;

    public List<string> Roles { get; set; } = new();
}

// =========================================================================
// Account recovery — the flows lesson 3 mentioned but never built
// =========================================================================

/// <summary>Body of <c>POST /api/auth/forgot-password</c>.</summary>
/// <remarks>
/// One field, and the endpoint answers 204 whether or not the account exists.
/// See <c>AuthService.ForgotPasswordAsync</c> for why that matters.
/// </remarks>
public class ForgotPasswordDto
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;
}

/// <summary>Body of <c>POST /api/auth/reset-password</c>.</summary>
public class ResetPasswordDto
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// The token from the email link. Generated by Identity, single-use, time-limited,
    /// and tied to the user's security stamp — so it dies the moment the password changes,
    /// even if it has not expired yet.
    /// </summary>
    [Required]
    public string Token { get; set; } = string.Empty;

    [Required]
    [StringLength(100, MinimumLength = 8)]
    public string NewPassword { get; set; } = string.Empty;

    [Required]
    [Compare(nameof(NewPassword), ErrorMessage = "The two passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

/// <summary>Body of <c>POST /api/auth/confirm-email</c>.</summary>
public class ConfirmEmailDto
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Token { get; set; } = string.Empty;
}

/// <summary>Body of <c>POST /api/auth/resend-confirmation</c>.</summary>
public class ResendConfirmationDto
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;
}

/// <summary>Body of <c>PUT /api/auth/me</c> — the caller editing their own details.</summary>
/// <remarks>
/// Compare <see cref="UpdateUserDto"/>, which an Admin uses on someone else. That one has
/// <c>Roles</c> and <c>IsActive</c>; this one deliberately does not. A user editing their own
/// profile must not be able to grant themselves a role or reactivate a suspended account —
/// and the safest way to guarantee that is for the fields not to exist on the DTO at all.
/// </remarks>
public class UpdateProfileDto
{
    [Required]
    [StringLength(120, MinimumLength = 2)]
    public string DisplayName { get; set; } = string.Empty;

    [Phone]
    [StringLength(30)]
    public string? PhoneNumber { get; set; }
}

/// <summary>One active sign-in, for the "your devices" screen.</summary>
public class SessionDto
{
    public int Id { get; init; }
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime ExpiresAt { get; init; }

    /// <summary>True for the session that made this request, so the UI can label it "this device".</summary>
    public bool IsCurrent { get; init; }
}
