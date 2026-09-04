using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketHub.BusinessLogic.Abstractions;
using TicketHub.BusinessLogic.Options;
using TicketHub.Contracts.Abstractions;
using TicketHub.Contracts.Auth;
using TicketHub.Contracts.Common;
using TicketHub.Contracts.Enums;
using TicketHub.DataAccess;
using TicketHub.DataAccess.Entities;
using TicketHub.DataAccess.Repositories;

namespace TicketHub.BusinessLogic.Services;

public interface IAuthService
{
    Task<ServiceResult<AuthResponseDto>> RegisterAsync(RegisterDto dto, string? ip, CancellationToken ct = default);
    Task<ServiceResult<AuthResponseDto>> LoginAsync(LoginDto dto, string? ip, string? userAgent, CancellationToken ct = default);
    Task<ServiceResult<AuthResponseDto>> RefreshAsync(string refreshToken, string? ip, CancellationToken ct = default);
    Task<ServiceResult> LogoutAsync(string refreshToken, CancellationToken ct = default);
    Task<ServiceResult> ChangePasswordAsync(ChangePasswordDto dto, CancellationToken ct = default);
    Task<ServiceResult<UserDto>> GetCurrentUserAsync(CancellationToken ct = default);

    // ----- account recovery -----
    Task<ServiceResult> ForgotPasswordAsync(ForgotPasswordDto dto, CancellationToken ct = default);
    Task<ServiceResult> ResetPasswordAsync(ResetPasswordDto dto, CancellationToken ct = default);
    Task<ServiceResult> ConfirmEmailAsync(ConfirmEmailDto dto, CancellationToken ct = default);
    Task<ServiceResult> ResendConfirmationAsync(ResendConfirmationDto dto, CancellationToken ct = default);

    // ----- self-service profile and sessions -----
    Task<ServiceResult<UserDto>> UpdateProfileAsync(UpdateProfileDto dto, CancellationToken ct = default);
    Task<ServiceResult> LogoutEverywhereAsync(CancellationToken ct = default);
    Task<ServiceResult<IReadOnlyList<SessionDto>>> GetSessionsAsync(string? currentRefreshToken, CancellationToken ct = default);
    Task<ServiceResult> RevokeSessionAsync(int sessionId, CancellationToken ct = default);
}

/// <summary>
/// Registration, login, refresh and logout — everything the token life cycle needs.
/// </summary>
public class AuthService : IAuthService
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly ITokenService _tokens;
    private readonly TicketHubDbContext _db;
    private readonly IUnitOfWork _uow;
    private readonly ICurrentUser _currentUser;
    private readonly JwtOptions _jwt;
    private readonly AppOptions _app;
    private readonly IEmailSender _email;
    private readonly ILogger<AuthService> _log;

    public AuthService(
        UserManager<ApplicationUser> users,
        ITokenService tokens,
        TicketHubDbContext db,
        IUnitOfWork uow,
        ICurrentUser currentUser,
        IOptions<JwtOptions> jwt,
        IOptions<AppOptions> app,
        IEmailSender email,
        ILogger<AuthService> log)
    {
        _users = users;
        _tokens = tokens;
        _db = db;
        _uow = uow;
        _currentUser = currentUser;
        _jwt = jwt.Value;
        _app = app.Value;
        _email = email;
        _log = log;
    }

    // =====================================================================
    // Register
    // =====================================================================

    public async Task<ServiceResult<AuthResponseDto>> RegisterAsync(
        RegisterDto dto, string? ip, CancellationToken ct = default)
    {
        var existing = await _users.FindByEmailAsync(dto.Email);
        if (existing is not null)
        {
            // A deliberate choice we are making with our eyes open.
            //
            // Telling the caller "that email is taken" is user enumeration: an attacker can
            // now discover which addresses have accounts here. The stricter alternative is to
            // always reply "check your email" and send either a welcome or a "someone tried
            // to register with your address" message.
            //
            // For a municipal service where the sign-up form has to be usable by everyone,
            // the usability win beats the disclosure. For a bank it would not. Know which
            // you are building — and make the choice consciously rather than by accident.
            return ServiceResult<AuthResponseDto>.Conflict("An account with that email already exists.");
        }

        var user = new ApplicationUser
        {
            UserName = dto.Email,
            Email = dto.Email,
            DisplayName = dto.DisplayName.Trim(),
            PhoneNumber = dto.PhoneNumber,

            // Self-registration ALWAYS produces a Citizen. There is no Role property on
            // RegisterDto, because if there were, anyone could register as an Admin.
            UserType = UserType.Citizen,
            IsActive = true
        };

        // CreateAsync hashes the password with PBKDF2, generates the security stamp, applies
        // the password policy from Program.cs, and saves. Never construct a PasswordHash
        // yourself — you would be reimplementing all of that, and worse.
        var created = await _users.CreateAsync(user, dto.Password);

        if (!created.Succeeded)
        {
            // Identity's messages are already user-facing ("Passwords must have at least one
            // digit"), so pass them straight through instead of flattening them to "invalid".
            return ServiceResult<AuthResponseDto>.Invalid(new Dictionary<string, string[]>
            {
                ["Password"] = created.Errors.Select(e => e.Description).ToArray()
            });
        }

        await _users.AddToRoleAsync(user, AppRoles.Citizen);

        _log.LogInformation("Registered new citizen {Email}", dto.Email);

        return await IssueTokensAsync(user, ip, null, ct);
    }

    // =====================================================================
    // Login
    // =====================================================================

    public async Task<ServiceResult<AuthResponseDto>> LoginAsync(
        LoginDto dto, string? ip, string? userAgent, CancellationToken ct = default)
    {
        var user = await _users.FindByEmailAsync(dto.Email);

        // ONE message for "no such user" and "wrong password", on purpose.
        //
        // Two different messages let anyone with the login form enumerate your users: try an
        // address, and "wrong password" means the account exists. Same text either way.
        const string genericFailure = "Email or password is incorrect.";

        if (user is null)
        {
            // Timing is a channel too. Returning instantly for an unknown address while a
            // known one takes ~100ms of hashing tells an attacker the same thing the message
            // would have. A production system runs a dummy hash here to even it out; we are
            // naming the issue rather than solving it, because Identity's lockout is the
            // control that actually matters at our scale.
            _log.LogWarning("Login attempt for unknown email {Email} from {Ip}", dto.Email, ip);
            return ServiceResult<AuthResponseDto>.Invalid(genericFailure);
        }

        if (!user.IsActive)
        {
            _log.LogWarning("Login attempt for deactivated account {UserId}", user.Id);
            return ServiceResult<AuthResponseDto>.Forbidden("This account has been deactivated.");
        }

        // CheckPasswordAsync does a constant-time comparison of the hashes. Comparing hash
        // strings with == leaks information through how long the comparison takes.
        if (!await _users.CheckPasswordAsync(user, dto.Password))
        {
            // Feeds Identity's lockout: after the configured number of failures the account
            // locks itself for a while. This is the whole brute-force defence, and it only
            // works if you remember to record the failure.
            await _users.AccessFailedAsync(user);

            _log.LogWarning("Failed login for {UserId} from {Ip}", user.Id, ip);
            return ServiceResult<AuthResponseDto>.Invalid(genericFailure);
        }

        if (await _users.IsLockedOutAsync(user))
        {
            return ServiceResult<AuthResponseDto>.Forbidden(
                "This account is temporarily locked after too many failed attempts. Try again later.");
        }

        // Success clears the failure counter. Forget this and a user who mistypes their
        // password four times over four months eventually locks themselves out.
        await _users.ResetAccessFailedCountAsync(user);

        user.LastLoginAt = DateTime.UtcNow;
        await _users.UpdateAsync(user);

        _log.LogInformation("User {UserId} logged in from {Ip}", user.Id, ip);

        return await IssueTokensAsync(user, ip, userAgent, ct);
    }

    // =====================================================================
    // Refresh — with rotation and reuse detection
    // =====================================================================

    public async Task<ServiceResult<AuthResponseDto>> RefreshAsync(
        string refreshToken, string? ip, CancellationToken ct = default)
    {
        var stored = await _db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.Token == refreshToken, ct);

        if (stored is null)
        {
            return ServiceResult<AuthResponseDto>.Invalid("Invalid refresh token.");
        }

        // ---------------------------------------------------------------
        // REUSE DETECTION — the part people leave out, and the part that matters
        // ---------------------------------------------------------------
        //
        // Every refresh consumes its token and issues a new one. So a token that has already
        // been revoked being presented again has only two explanations: a bug, or somebody
        // else has a copy.
        //
        // We cannot tell which, so we assume the worst and kill every token for that user.
        // Both the legitimate user and the thief are logged out; only the legitimate user can
        // log back in. Being wrong costs one re-login. Not doing it costs the account.
        if (stored.IsRevoked)
        {
            _log.LogWarning(
                "Refresh token REUSE detected for user {UserId} from {Ip}. Revoking the whole chain.",
                stored.UserId, ip);

            await RevokeAllForUserAsync(stored.UserId, "reuse detected", ct);

            return ServiceResult<AuthResponseDto>.Forbidden(
                "This session is no longer valid. Please sign in again.");
        }

        if (stored.IsExpired)
        {
            return ServiceResult<AuthResponseDto>.Invalid("Refresh token has expired. Please sign in again.");
        }

        if (!stored.User.IsActive)
        {
            return ServiceResult<AuthResponseDto>.Forbidden("This account has been deactivated.");
        }

        // ROTATION: spend the old one before minting the new one.
        stored.RevokedAt = DateTime.UtcNow;
        stored.RevokedReason = "rotated";

        var result = await IssueTokensAsync(stored.User, ip, stored.UserAgent, ct);

        if (result.IsSuccess && result.Value is not null)
        {
            // The forward pointer, so reuse detection can walk the chain later.
            stored.ReplacedByToken = result.Value.RefreshToken;
            await _db.SaveChangesAsync(ct);
        }

        return result;
    }

    public async Task<ServiceResult> LogoutAsync(string refreshToken, CancellationToken ct = default)
    {
        var stored = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.Token == refreshToken, ct);

        // Already gone? Still report success. Logout is idempotent by nature, and telling the
        // caller "that token does not exist" is another small enumeration oracle for free.
        if (stored is null || stored.IsRevoked)
        {
            return ServiceResult.Success();
        }

        stored.RevokedAt = DateTime.UtcNow;
        stored.RevokedReason = "logout";
        await _db.SaveChangesAsync(ct);

        // Worth being honest about what logout does NOT do: the access token stays valid
        // until it expires. Nothing checks a deny-list on every request. That is the deal
        // with stateless tokens, and it is why the access token's lifetime is 15 minutes.
        return ServiceResult.Success();
    }

    // =====================================================================
    // Password change
    // =====================================================================

    public async Task<ServiceResult> ChangePasswordAsync(
        ChangePasswordDto dto, CancellationToken ct = default)
    {
        if (_currentUser.UserId is null)
        {
            return ServiceResult.Forbidden();
        }

        var user = await _users.FindByIdAsync(_currentUser.UserId.Value.ToString());
        if (user is null)
        {
            return ServiceResult.NotFound("User not found.");
        }

        var changed = await _users.ChangePasswordAsync(user, dto.CurrentPassword, dto.NewPassword);

        if (!changed.Succeeded)
        {
            return ServiceResult.Invalid(new Dictionary<string, string[]>
            {
                ["Password"] = changed.Errors.Select(e => e.Description).ToArray()
            });
        }

        // ChangePasswordAsync rotates the SecurityStamp for us. Because that stamp is baked
        // into every access token and checked on every request, every token issued before
        // this moment is now dead. Revoking the refresh tokens too closes the other door.
        await RevokeAllForUserAsync(user.Id, "password changed", ct);

        _log.LogInformation("User {UserId} changed their password; all sessions revoked.", user.Id);

        return ServiceResult.Success();
    }

    public async Task<ServiceResult<UserDto>> GetCurrentUserAsync(CancellationToken ct = default)
    {
        if (_currentUser.UserId is null)
        {
            return ServiceResult<UserDto>.Forbidden();
        }

        var user = await _users.FindByIdAsync(_currentUser.UserId.Value.ToString());
        if (user is null)
        {
            return ServiceResult<UserDto>.NotFound("User not found.");
        }

        return ServiceResult<UserDto>.Success(await MapUserAsync(user, ct));
    }

    // =====================================================================
    // Account recovery
    // =====================================================================

    /// <summary>
    /// Step 1 of "I forgot my password": generate a token and email a link.
    /// </summary>
    /// <remarks>
    /// ⚠ THE MOST IMPORTANT LINE IN THIS METHOD IS THE LAST ONE.
    /// <para/>
    /// It returns success <b>whether or not the account exists</b>. If it returned 404 for an
    /// unknown address, this endpoint would become a free tool for checking which emails have
    /// accounts here — the same user-enumeration problem that makes login use one message for
    /// "no such user" and "wrong password".
    /// <para/>
    /// So: do the work if there is work to do, and answer identically either way.
    /// </remarks>
    public async Task<ServiceResult> ForgotPasswordAsync(
        ForgotPasswordDto dto, CancellationToken ct = default)
    {
        var user = await _users.FindByEmailAsync(dto.Email);

        // Only act for a real, active account — but do not tell the caller which case they hit.
        if (user is not null && user.IsActive)
        {
            // Identity generates the token. It is single-use, time-limited, and derived from
            // the user's SecurityStamp — which means it stops working the moment the password
            // changes, even if it has not expired. Two resets cannot both be redeemed.
            var token = await _users.GeneratePasswordResetTokenAsync(user);

            // The token is opaque bytes rendered as text and WILL contain characters that are
            // not URL-safe (+ / =). Escape it, or roughly one reset link in three arrives
            // broken and you spend an afternoon wondering why.
            var link = $"{_app.FrontendBaseUrl.TrimEnd('/')}/reset-password" +
                       $"?email={Uri.EscapeDataString(user.Email!)}" +
                       $"&token={Uri.EscapeDataString(token)}";

            await _email.SendAsync(
                user.Email!,
                $"{_app.Name} — reset your password",
                $"Someone asked to reset the password for this account.\n\n{link}\n\n" +
                "If that was not you, you can ignore this email — nothing has changed.",
                ct);

            _log.LogInformation("Password reset requested for user {UserId}", user.Id);
        }
        else
        {
            // Worth logging, because a burst of these is somebody probing for valid addresses.
            _log.LogWarning("Password reset requested for unknown or inactive email {Email}", dto.Email);
        }

        return ServiceResult.Success();
    }

    /// <summary>Step 2: redeem the token and set the new password.</summary>
    public async Task<ServiceResult> ResetPasswordAsync(
        ResetPasswordDto dto, CancellationToken ct = default)
    {
        var user = await _users.FindByEmailAsync(dto.Email);

        // Here we DO fail — but with a message that never says which part was wrong.
        // "That reset link is no longer valid" covers a bad email, a bad token, an expired
        // token and an already-used token, and gives an attacker nothing to work with.
        const string invalid = "That reset link is invalid or has expired. Please request a new one.";

        if (user is null || !user.IsActive)
        {
            return ServiceResult.Invalid(invalid);
        }

        var result = await _users.ResetPasswordAsync(user, dto.Token, dto.NewPassword);

        if (!result.Succeeded)
        {
            // Identity returns two very different kinds of failure here: "your token is bad"
            // and "your new password is too weak". The second one is genuinely useful to show,
            // the first one is not. Separate them.
            var passwordProblems = result.Errors
                .Where(e => !e.Code.Contains("Token", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Description)
                .ToArray();

            return passwordProblems.Length > 0
                ? ServiceResult.Invalid(new Dictionary<string, string[]> { ["NewPassword"] = passwordProblems })
                : ServiceResult.Invalid(invalid);
        }

        // ResetPasswordAsync rotates the SecurityStamp for us. Because that stamp is baked into
        // every access token and compared on every request (see OnTokenValidated in Program.cs),
        // every access token issued before this moment is now dead.
        //
        // Revoking the refresh tokens closes the other door: without this, whoever stole the
        // account could simply refresh their way back in with a token the reset did not touch.
        await RevokeAllForUserAsync(user.Id, "password reset", ct);

        _log.LogInformation("User {UserId} reset their password; all sessions revoked.", user.Id);

        return ServiceResult.Success();
    }

    /// <summary>
    /// Confirms an email address. Same token machinery, different generator.
    /// </summary>
    /// <remarks>
    /// Once this works, turn on <c>options.SignIn.RequireConfirmedEmail</c> in Program.cs.
    /// Until you do, anyone can register using someone else's address.
    /// </remarks>
    public async Task<ServiceResult> ConfirmEmailAsync(
        ConfirmEmailDto dto, CancellationToken ct = default)
    {
        var user = await _users.FindByEmailAsync(dto.Email);
        if (user is null)
        {
            return ServiceResult.Invalid("That confirmation link is invalid or has expired.");
        }

        if (user.EmailConfirmed)
        {
            // Idempotent. Users click the link twice, and mail clients pre-fetch links —
            // so the second visit must not look like an error.
            return ServiceResult.Success();
        }

        var result = await _users.ConfirmEmailAsync(user, dto.Token);

        return result.Succeeded
            ? ServiceResult.Success()
            : ServiceResult.Invalid("That confirmation link is invalid or has expired.");
    }

    public async Task<ServiceResult> ResendConfirmationAsync(
        ResendConfirmationDto dto, CancellationToken ct = default)
    {
        var user = await _users.FindByEmailAsync(dto.Email);

        // Same enumeration rule as forgot-password: always 204.
        if (user is not null && user.IsActive && !user.EmailConfirmed)
        {
            var token = await _users.GenerateEmailConfirmationTokenAsync(user);

            var link = $"{_app.FrontendBaseUrl.TrimEnd('/')}/confirm-email" +
                       $"?email={Uri.EscapeDataString(user.Email!)}" +
                       $"&token={Uri.EscapeDataString(token)}";

            await _email.SendAsync(
                user.Email!,
                $"{_app.Name} — confirm your email address",
                $"Welcome. Please confirm your address:\n\n{link}",
                ct);
        }

        return ServiceResult.Success();
    }

    // =====================================================================
    // Self-service profile and sessions
    // =====================================================================

    /// <summary>
    /// The caller edits their own display name and phone number. Nothing else.
    /// </summary>
    /// <remarks>
    /// Note there is no id parameter: the user comes from the token, so "whose profile?" has
    /// exactly one possible answer. And <see cref="UpdateProfileDto"/> has no <c>Roles</c> and
    /// no <c>IsActive</c> — a field that does not exist cannot be forged.
    /// </remarks>
    public async Task<ServiceResult<UserDto>> UpdateProfileAsync(
        UpdateProfileDto dto, CancellationToken ct = default)
    {
        if (_currentUser.UserId is null)
        {
            return ServiceResult<UserDto>.Forbidden();
        }

        var user = await _users.FindByIdAsync(_currentUser.UserId.Value.ToString());
        if (user is null)
        {
            return ServiceResult<UserDto>.NotFound("User not found.");
        }

        user.DisplayName = dto.DisplayName.Trim();
        user.PhoneNumber = dto.PhoneNumber;

        var updated = await _users.UpdateAsync(user);
        if (!updated.Succeeded)
        {
            return ServiceResult<UserDto>.Invalid(
                string.Join("; ", updated.Errors.Select(e => e.Description)));
        }

        // The display name is in the JWT as a claim, so it is stale until the next refresh.
        // We deliberately do NOT bump the security stamp here — forcing a full re-login
        // because someone fixed a typo in their own name would be a poor trade. Compare
        // UserService.UpdateAsync, which DOES bump it, because a role change must be immediate.
        return ServiceResult<UserDto>.Success(await MapUserAsync(user, ct));
    }

    /// <summary>Revokes every refresh token and invalidates every access token. "Sign out everywhere".</summary>
    public async Task<ServiceResult> LogoutEverywhereAsync(CancellationToken ct = default)
    {
        if (_currentUser.UserId is null)
        {
            return ServiceResult.Forbidden();
        }

        var user = await _users.FindByIdAsync(_currentUser.UserId.Value.ToString());
        if (user is null)
        {
            return ServiceResult.NotFound("User not found.");
        }

        // BOTH doors, again:
        //   1. the security stamp kills every access token already in the wild
        //   2. revoking the refresh tokens stops anyone minting a new one
        // Do only the first and they refresh straight back in; do only the second and they
        // keep working until the access token expires.
        await _users.UpdateSecurityStampAsync(user);
        await RevokeAllForUserAsync(user.Id, "logout everywhere", ct);

        _log.LogInformation("User {UserId} signed out of all sessions.", user.Id);

        return ServiceResult.Success();
    }

    /// <summary>The caller's active sign-ins, for a "your devices" screen.</summary>
    public async Task<ServiceResult<IReadOnlyList<SessionDto>>> GetSessionsAsync(
        string? currentRefreshToken, CancellationToken ct = default)
    {
        if (_currentUser.UserId is null)
        {
            return ServiceResult<IReadOnlyList<SessionDto>>.Forbidden();
        }

        var now = DateTime.UtcNow;

        var sessions = await _db.RefreshTokens
            .AsNoTracking()
            .Where(t => t.UserId == _currentUser.UserId.Value
                        && t.RevokedAt == null
                        && t.ExpiresAt > now)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new SessionDto
            {
                Id = t.Id,
                IpAddress = t.CreatedByIp,
                UserAgent = t.UserAgent,
                CreatedAt = t.CreatedAt,
                ExpiresAt = t.ExpiresAt,
                IsCurrent = currentRefreshToken != null && t.Token == currentRefreshToken
            })
            .ToListAsync(ct);

        // NOTE what is not here: t.Token itself. Listing your sessions must not hand back the
        // credentials for them — that would turn a read-only screen into a way to take over
        // every other device.
        return ServiceResult<IReadOnlyList<SessionDto>>.Success(sessions);
    }

    /// <summary>Kills one session — "sign out that phone I lost".</summary>
    public async Task<ServiceResult> RevokeSessionAsync(int sessionId, CancellationToken ct = default)
    {
        if (_currentUser.UserId is null)
        {
            return ServiceResult.Forbidden();
        }

        // The user id is part of the lookup, not a check afterwards. Otherwise session id 42
        // belonging to somebody else is loaded first and only then rejected — and one day
        // someone forgets the second half.
        var session = await _db.RefreshTokens
            .FirstOrDefaultAsync(t => t.Id == sessionId && t.UserId == _currentUser.UserId.Value, ct);

        if (session is null)
        {
            return ServiceResult.NotFound("Session not found.");
        }

        session.RevokedAt = DateTime.UtcNow;
        session.RevokedReason = "revoked by user";
        await _db.SaveChangesAsync(ct);

        return ServiceResult.Success();
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private async Task<ServiceResult<AuthResponseDto>> IssueTokensAsync(
        ApplicationUser user, string? ip, string? userAgent, CancellationToken ct)
    {
        var roles = await _users.GetRolesAsync(user);
        var agent = await _uow.Agents.GetByUserIdAsync(user.Id, ct);

        var (accessToken, accessExpires) = _tokens.CreateAccessToken(user, roles, agent);

        var refreshToken = new RefreshToken
        {
            Token = _tokens.CreateRefreshToken(),
            UserId = user.Id,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(_jwt.RefreshTokenDays),
            CreatedByIp = ip,
            UserAgent = userAgent
        };

        _db.RefreshTokens.Add(refreshToken);
        await _db.SaveChangesAsync(ct);

        return ServiceResult<AuthResponseDto>.Success(new AuthResponseDto
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken.Token,
            AccessTokenExpiresAt = accessExpires,
            RefreshTokenExpiresAt = refreshToken.ExpiresAt,
            User = await MapUserAsync(user, ct, roles, agent)
        });
    }

    private async Task RevokeAllForUserAsync(int userId, string reason, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // One UPDATE for every live token, instead of loading them all and saving each.
        await _db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.RevokedAt, now)
                      .SetProperty(t => t.RevokedReason, reason),
                ct);
    }

    private async Task<UserDto> MapUserAsync(
        ApplicationUser user,
        CancellationToken ct,
        IList<string>? roles = null,
        Agent? agent = null)
    {
        roles ??= await _users.GetRolesAsync(user);
        agent ??= await _uow.Agents.GetByUserIdAsync(user.Id, ct);

        string? departmentName = null;
        if (agent is not null)
        {
            var department = await _uow.Departments.GetByIdAsync(agent.DepartmentId, ct);
            departmentName = department?.Name;
        }

        return new UserDto
        {
            Id = user.Id,
            Email = user.Email ?? string.Empty,
            DisplayName = user.DisplayName,
            PhoneNumber = user.PhoneNumber,
            UserType = user.UserType,
            IsActive = user.IsActive,
            Roles = roles.ToList(),
            AgentId = agent?.Id,
            DepartmentId = agent?.DepartmentId,
            DepartmentName = departmentName,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt
        };
    }
}
