using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using TicketHub.BusinessLogic.Options;
using TicketHub.Contracts.Abstractions;
using TicketHub.DataAccess.Entities;

namespace TicketHub.BusinessLogic.Services;

public interface ITokenService
{
    /// <summary>Mints a signed access token for a user with their roles and department.</summary>
    (string Token, DateTime ExpiresAt) CreateAccessToken(
        ApplicationUser user, IEnumerable<string> roles, Agent? agent);

    /// <summary>A cryptographically random opaque string. Not a JWT — it carries no data.</summary>
    string CreateRefreshToken();
}

/// <summary>
/// Builds JWTs. Small, and worth understanding line by line.
/// </summary>
/// <remarks>
/// WHAT A JWT ACTUALLY IS: three base64 sections joined by dots — header, payload, signature.
/// <para/>
/// The critical thing to internalise: <b>the payload is encoded, not encrypted</b>. Anyone
/// holding the token can paste it into jwt.io and read every claim. The signature does not
/// hide anything; it proves nothing was <em>changed</em>. So never put anything secret in a
/// claim — no password, no national id, no internal note. Claims are public data with a
/// tamper-proof seal.
/// </remarks>
public class JwtTokenService : ITokenService
{
    private readonly JwtOptions _options;

    public JwtTokenService(IOptions<JwtOptions> options) => _options = options.Value;

    public (string Token, DateTime ExpiresAt) CreateAccessToken(
        ApplicationUser user, IEnumerable<string> roles, Agent? agent)
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(_options.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            // "sub" — the subject. THE canonical "who is this" claim.
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),

            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),

            // "jti" — a unique id for this token. Lets you build a deny-list of specific
            // tokens later without changing the shape of anything.
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),

            // ASP.NET Core reads THIS claim type for User.Identity.Name and, importantly,
            // for the [Authorize] plumbing. The registered "sub" above is the standards-
            // compliant one; both are here because different parts of the stack look for
            // different names, and fighting that costs more than one extra claim.
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Email ?? string.Empty),

            new(AppClaimTypes.DisplayName, user.DisplayName),
            new(AppClaimTypes.UserType, user.UserType.ToString()),

            // The emergency brake. It goes into the token and is compared on every request:
            // change it on the user (password reset, forced logout) and every token minted
            // before that moment stops validating. This is how you revoke in a stateless
            // system — see the check in Program.cs's OnTokenValidated.
            new(AppClaimTypes.SecurityStamp, user.SecurityStamp ?? string.Empty)
        };

        // Roles as claims, so [Authorize(Roles = "Admin")] works with no database hit.
        // The cost is staleness: promote someone and their current token still says Agent
        // until it expires. Fifteen minutes of wrong is the price of zero queries per request.
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        if (agent is not null)
        {
            // The department in the token is what makes the security filter free. Without it
            // every single request would begin with "SELECT DepartmentId FROM Agents…".
            claims.Add(new Claim(AppClaimTypes.DepartmentId, agent.DepartmentId.ToString()));
            claims.Add(new Claim(AppClaimTypes.AgentId, agent.Id.ToString()));
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Key));

        // HMAC-SHA256: one shared secret both signs and verifies. Right for a system that
        // issues and consumes its own tokens.
        //
        // If a SEPARATE service had to verify our tokens, you would want RS256 instead:
        // sign with a private key, publish the public one, and nobody but us can mint tokens.
        // With HS256 anyone who can verify can also forge.
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAt,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    public string CreateRefreshToken()
    {
        // RandomNumberGenerator, NOT Random.
        //
        // Random is a predictable pseudo-random sequence: given a couple of outputs you can
        // compute the rest of them. That is fine for shuffling a deck and catastrophic for a
        // credential. This one is the cryptographic generator, and it is the only acceptable
        // source for anything that acts as a secret.
        var bytes = RandomNumberGenerator.GetBytes(64);
        return Convert.ToBase64String(bytes);
    }
}
