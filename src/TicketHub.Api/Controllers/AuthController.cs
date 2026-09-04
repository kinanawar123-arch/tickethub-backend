using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using TicketHub.BusinessLogic.Services;
using TicketHub.Contracts.Auth;

namespace TicketHub.Api.Controllers;

/// <summary>
/// Register, sign in, refresh, sign out.
/// </summary>
/// <remarks>
/// The only controller with anonymous endpoints — everything else in the API requires a token,
/// and you have to be able to get one somehow.
/// </remarks>
[Route("api/auth")]
[Produces("application/json")]
// EVERY endpoint in this controller is rate limited. This is the most attacked URL prefix
// you will ever ship: login, password reset and registration are where credential-stuffing
// and password-spraying arrive. Identity's lockout protects ONE account from many guesses;
// this protects the whole system from one attacker touching many accounts.
[EnableRateLimiting("auth")]
public class AuthController : ApiControllerBase
{
    private readonly IAuthService _auth;

    public AuthController(IAuthService auth) => _auth = auth;

    /// <summary>Creates a Citizen account and signs them straight in.</summary>
    /// <remarks>
    /// There is no way to register as staff. Staff accounts are created by an Admin through
    /// <c>POST /api/users</c> — if this endpoint accepted a role, anyone could make themselves
    /// an administrator with one extra line of JSON.
    /// </remarks>
    /// <response code="200">Registered. Tokens are in the body.</response>
    /// <response code="409">That email is already in use.</response>
    [HttpPost("register")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AuthResponseDto>> Register(
        [FromBody] RegisterDto dto, CancellationToken ct)
        => ToActionResult(await _auth.RegisterAsync(dto, GetClientIp(), ct));

    /// <summary>Exchanges an email and password for an access token and a refresh token.</summary>
    /// <response code="200">Signed in.</response>
    /// <response code="400">Email or password is wrong (the same answer for both, on purpose).</response>
    /// <response code="403">The account is deactivated or locked out.</response>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AuthResponseDto>> Login(
        [FromBody] LoginDto dto, CancellationToken ct)
        => ToActionResult(await _auth.LoginAsync(
            dto, GetClientIp(), Request.Headers.UserAgent.ToString(), ct));

    /// <summary>Trades a refresh token for a new pair.</summary>
    /// <remarks>
    /// The old refresh token is consumed. Presenting a spent one is treated as a stolen
    /// credential and revokes every session for that user — see <c>AuthService.RefreshAsync</c>.
    /// </remarks>
    /// <response code="403">Reuse detected. Everything has been revoked; sign in again.</response>
    [HttpPost("refresh")]
    [AllowAnonymous]   // by definition the access token has expired, so this cannot require one
    [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AuthResponseDto>> Refresh(
        [FromBody] RefreshTokenRequestDto dto, CancellationToken ct)
        => ToActionResult(await _auth.RefreshAsync(dto.RefreshToken, GetClientIp(), ct));

    /// <summary>Revokes one refresh token.</summary>
    /// <remarks>
    /// Be clear about what this does and does not do: the <em>access</em> token stays valid
    /// until it expires, because nothing checks a deny-list on every request. That is the
    /// deal with stateless tokens, and it is why they only live 15 minutes.
    /// </remarks>
    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> Logout([FromBody] RefreshTokenRequestDto dto, CancellationToken ct)
        => ToActionResult(await _auth.LogoutAsync(dto.RefreshToken, ct));

    /// <summary>Whoever the current token belongs to.</summary>
    /// <remarks>
    /// The endpoint a front end calls on start-up to find out who is signed in and what they
    /// are allowed to see. Note there is no id parameter — the answer comes from the token,
    /// so it can only ever be about the caller.
    /// </remarks>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<UserDto>> Me(CancellationToken ct)
        => ToActionResult(await _auth.GetCurrentUserAsync(ct));

    /// <summary>Changes the caller's own password and signs every session out.</summary>
    [HttpPost("change-password")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> ChangePassword(
        [FromBody] ChangePasswordDto dto, CancellationToken ct)
        => ToActionResult(await _auth.ChangePasswordAsync(dto, ct));

    // =====================================================================
    // Account recovery
    // =====================================================================

    /// <summary>Starts a password reset. Emails a link if the account exists.</summary>
    /// <remarks>
    /// Always 204, whether or not the address is registered. Returning 404 for unknown emails
    /// would turn this into a free tool for discovering which addresses have accounts here.
    /// </remarks>
    /// <response code="204">Request accepted. Check your email — if you have an account.</response>
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> ForgotPassword(
        [FromBody] ForgotPasswordDto dto, CancellationToken ct)
        => ToActionResult(await _auth.ForgotPasswordAsync(dto, ct));

    /// <summary>Completes a password reset using the token from the email.</summary>
    /// <response code="204">Password changed. Every existing session has been revoked.</response>
    /// <response code="400">The link is invalid or expired, or the new password is too weak.</response>
    [HttpPost("reset-password")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> ResetPassword(
        [FromBody] ResetPasswordDto dto, CancellationToken ct)
        => ToActionResult(await _auth.ResetPasswordAsync(dto, ct));

    /// <summary>Confirms an email address from the registration link.</summary>
    [HttpPost("confirm-email")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> ConfirmEmail(
        [FromBody] ConfirmEmailDto dto, CancellationToken ct)
        => ToActionResult(await _auth.ConfirmEmailAsync(dto, ct));

    /// <summary>Sends the confirmation email again. Always 204, same reason as forgot-password.</summary>
    [HttpPost("resend-confirmation")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> ResendConfirmation(
        [FromBody] ResendConfirmationDto dto, CancellationToken ct)
        => ToActionResult(await _auth.ResendConfirmationAsync(dto, ct));

    // =====================================================================
    // Self-service profile and sessions
    // =====================================================================

    /// <summary>Updates the caller's own display name and phone number.</summary>
    /// <remarks>
    /// No id in the route, and no <c>Roles</c> or <c>IsActive</c> on the DTO. Changing someone
    /// else — or changing your own role — goes through <c>PUT /api/users/{id}</c>, which is
    /// Admin-only.
    /// </remarks>
    [HttpPut("me")]
    [Authorize]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<UserDto>> UpdateProfile(
        [FromBody] UpdateProfileDto dto, CancellationToken ct)
        => ToActionResult(await _auth.UpdateProfileAsync(dto, ct));

    /// <summary>Signs the caller out of every device.</summary>
    /// <remarks>
    /// Unlike <c>/logout</c>, this one <b>does</b> kill the access tokens too — it bumps the
    /// security stamp, and every token minted before that moment stops validating on its next
    /// request.
    /// </remarks>
    [HttpPost("logout-all")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> LogoutEverywhere(CancellationToken ct)
        => ToActionResult(await _auth.LogoutEverywhereAsync(ct));

    /// <summary>The caller's active sessions — the "your devices" screen.</summary>
    /// <param name="currentRefreshToken">
    /// Optional. Pass the refresh token this client holds and the matching session comes back
    /// flagged <c>isCurrent</c>, so the UI can say "this device".
    /// </param>
    [HttpGet("sessions")]
    [Authorize]
    [ProducesResponseType(typeof(IReadOnlyList<SessionDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<SessionDto>>> Sessions(
        [FromQuery] string? currentRefreshToken, CancellationToken ct)
        => ToActionResult(await _auth.GetSessionsAsync(currentRefreshToken, ct));

    /// <summary>Revokes one session — "sign out the phone I lost".</summary>
    [HttpDelete("sessions/{id:int}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> RevokeSession(int id, CancellationToken ct)
        => ToActionResult(await _auth.RevokeSessionAsync(id, ct));

    /// <summary>
    /// The caller's IP, for the security log.
    /// </summary>
    /// <remarks>
    /// Behind a reverse proxy or load balancer, <c>RemoteIpAddress</c> is the proxy, not the
    /// user. <c>X-Forwarded-For</c> carries the original — but it is a header, so a client can
    /// put anything in it. Only trust it when your own proxy sets it, which is what
    /// <c>UseForwardedHeaders</c> with a known-proxies list is for. Recorded here for
    /// diagnostics, never used to make a security decision.
    /// </remarks>
    private string? GetClientIp()
    {
        var forwarded = Request.Headers["X-Forwarded-For"].FirstOrDefault();

        return !string.IsNullOrWhiteSpace(forwarded)
            ? forwarded.Split(',')[0].Trim()
            : HttpContext.Connection.RemoteIpAddress?.ToString();
    }
}
