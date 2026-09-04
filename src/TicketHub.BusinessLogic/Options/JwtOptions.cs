using System.ComponentModel.DataAnnotations;

namespace TicketHub.BusinessLogic.Options;

/// <summary>
/// Everything the token service needs, bound from the <c>Jwt</c> section of configuration.
/// </summary>
/// <remarks>
/// THE OPTIONS PATTERN, AND WHY IT BEATS <c>IConfiguration["Jwt:Key"]</c>:
/// <list type="bullet">
/// <item>A class with properties gives you compile-time names. A string key gives you a typo
///       that returns null at runtime.</item>
/// <item>Registered with <c>ValidateDataAnnotations().ValidateOnStart()</c>, a missing or
///       too-short key stops the application <b>at startup</b> with a clear message — instead
///       of at 3am on the first login attempt.</item>
/// <item>A service that takes <c>IOptions&lt;JwtOptions&gt;</c> can be unit-tested by handing
///       it a plain object. One that reads <c>IConfiguration</c> needs a configuration
///       builder in every test.</item>
/// </list>
/// </remarks>
public class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>Who issued the token. Checked on every request.</summary>
    [Required]
    public string Issuer { get; set; } = string.Empty;

    /// <summary>Who the token is for. Also checked, and this is not decoration.</summary>
    /// <remarks>
    /// Skipping audience validation is how a token minted by your identity provider for a
    /// completely different application ends up being accepted by yours.
    /// </remarks>
    [Required]
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// The symmetric signing key. Anyone holding it can mint a valid token for any user.
    /// </summary>
    /// <remarks>
    /// ⚠ IT IS IN <c>appsettings.Development.json</c> IN THIS PROJECT, AND THAT IS ONLY OK
    /// BECAUSE THIS IS A TRAINING DATABASE ON YOUR LAPTOP. In anything real it comes from
    /// user-secrets locally and from a key vault or environment variable in production, and
    /// it never, ever enters source control. A signing key in a public repository is the
    /// whole authentication system handed over.
    /// <para/>
    /// The 32-character minimum is not arbitrary: HMAC-SHA256 needs at least 256 bits, and
    /// the library will throw if you give it less.
    /// </remarks>
    [Required]
    [MinLength(32, ErrorMessage = "The JWT signing key must be at least 32 characters (256 bits) for HMAC-SHA256.")]
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// How long an access token lives. Short, because it cannot be revoked.
    /// </summary>
    /// <remarks>
    /// The server does not store access tokens — it only verifies the signature — so a stolen
    /// one is valid until it expires. Fifteen minutes is the usual compromise between that
    /// exposure window and hammering the refresh endpoint.
    /// </remarks>
    [Range(1, 1440)]
    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>How long a refresh token lives. Long, because it IS revocable.</summary>
    [Range(1, 365)]
    public int RefreshTokenDays { get; set; } = 7;

    /// <summary>
    /// Tolerance for clock differences between the issuing and validating servers.
    /// </summary>
    /// <remarks>
    /// The default in .NET is five minutes, which quietly extends every token's life by five
    /// minutes. We set it to zero and rely on the machines having correct clocks — which,
    /// in 2026, they do.
    /// </remarks>
    public int ClockSkewSeconds { get; set; }
}

/// <summary>Where uploaded files go and what we accept.</summary>
public class FileStorageOptions
{
    public const string SectionName = "FileStorage";

    /// <summary>Root folder for uploads, relative to the content root.</summary>
    public string RootPath { get; set; } = "uploads";

    /// <summary>Anything bigger is rejected before it is written to disk.</summary>
    public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// An ALLOW list, not a block list.
    /// </summary>
    /// <remarks>
    /// A block list has to anticipate every dangerous extension, and it will always be
    /// missing one. An allow list only has to know what you actually support, and everything
    /// else is refused by default. Whenever you can choose, choose the allow list.
    /// <para/>
    /// ⚠ THE DEFAULT IS DELIBERATELY EMPTY, AND THAT IS NOT AN OVERSIGHT.
    /// <para/>
    /// .NET's configuration binder <b>appends</b> to an existing array rather than replacing
    /// it. Give this property a default of eight extensions and bind eight from
    /// appsettings.json, and you get <b>sixteen</b> — the same eight twice. That is merely
    /// ugly here, but the same behaviour means a value you <em>remove</em> from configuration
    /// is still allowed, because the C# default silently puts it back. An allow list that
    /// cannot be narrowed is not an allow list.
    /// <para/>
    /// Empty default + <c>[MinLength(1)]</c> = configuration is the single source of truth,
    /// and a missing section fails at start-up instead of silently accepting nothing.
    /// </remarks>
    [MinLength(1, ErrorMessage = "FileStorage:AllowedExtensions must list at least one extension.")]
    public string[] AllowedExtensions { get; set; } = Array.Empty<string>();
}
