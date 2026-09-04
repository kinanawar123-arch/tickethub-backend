using System.ComponentModel.DataAnnotations;

namespace TicketHub.BusinessLogic.Options;

/// <summary>
/// Settings about the application as a whole, rather than about one subsystem.
/// </summary>
public class AppOptions
{
    public const string SectionName = "App";

    /// <summary>
    /// Where the <b>front end</b> lives — not this API.
    /// </summary>
    /// <remarks>
    /// A password-reset link has to open a page with a form on it, and that page belongs to the
    /// SPA. So the link we email is <c>{FrontendBaseUrl}/reset-password?email=…&amp;token=…</c>,
    /// and the SPA posts those two values back to <c>POST /api/auth/reset-password</c>.
    /// <para/>
    /// It is configuration and not a hard-coded string because it is different on every
    /// environment — localhost, staging, production — and getting it wrong emails your users a
    /// link to a server they cannot reach.
    /// </remarks>
    [Required]
    [Url]
    public string FrontendBaseUrl { get; set; } = "http://localhost:5173";

    /// <summary>Shown as the sender name in emails.</summary>
    public string Name { get; set; } = "TicketHub";
}
