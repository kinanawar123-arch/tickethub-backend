using System.ComponentModel.DataAnnotations;

namespace TicketHub.BusinessLogic.Options;

/// <summary>
/// Which email provider to use, and the credentials it needs.
/// </summary>
/// <remarks>
/// Every sender reads from this one section, so switching providers is a configuration change
/// plus one line in <c>Program.cs</c> — never an edit to <c>AuthService</c>.
/// </remarks>
public class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>Console, Smtp or Mailjet. Chosen in Program.cs.</summary>
    public string Provider { get; set; } = "Console";

    /// <summary>The address recipients see in the From field.</summary>
    [Required, EmailAddress]
    public string FromAddress { get; set; } = "no-reply@tickethub.local";

    [Required]
    public string FromName { get; set; } = "TicketHub";

    // ----- SMTP (any provider: Gmail, Office 365, Mailtrap, your own server) -----
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseStartTls { get; set; } = true;
    public string? SmtpUser { get; set; }

    /// <summary>
    /// ⚠ Never in appsettings.json. Supply it from user-secrets locally and from an
    /// environment variable or key vault in production.
    /// </summary>
    public string? SmtpPassword { get; set; }

    // ----- Mailjet (HTTP API) -----
    public string? MailjetApiKey { get; set; }

    /// <summary>⚠ Same rule as SmtpPassword. This is a credential.</summary>
    public string? MailjetApiSecret { get; set; }
}
