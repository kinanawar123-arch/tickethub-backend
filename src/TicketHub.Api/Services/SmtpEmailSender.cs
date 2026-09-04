using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;
using TicketHub.BusinessLogic.Abstractions;
using TicketHub.BusinessLogic.Options;

namespace TicketHub.Api.Services;

/// <summary>
/// Sends real email over SMTP — the protocol every mail provider speaks.
/// </summary>
/// <remarks>
/// SMTP IS THE LOWEST COMMON DENOMINATOR. Gmail, Office 365, Mailjet, SendGrid, Mailtrap and
/// your company's own mail server all accept it. You give it a host, a port and credentials,
/// and it behaves the same everywhere. That makes it the right first choice: no SDK, no
/// vendor lock-in, one class.
/// <para/>
/// Compare <see cref="MailjetEmailSender"/>, which uses an HTTP API instead. The trade is in
/// the comments there.
/// <para/>
/// NOTE ON <c>SmtpClient</c>: Microsoft marks it "obsolete for new development" and points at
/// MailKit for anything serious — better async, better TLS negotiation, and it does not hold a
/// connection open awkwardly. It is used here because it is in the box and this is a teaching
/// project. In production: <c>dotnet add package MailKit</c>.
/// </remarks>
public class SmtpEmailSender : IEmailSender
{
    private readonly EmailOptions _options;
    private readonly ILogger<SmtpEmailSender> _log;

    public SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> log)
    {
        _options = options.Value;
        _log = log;
    }

    public async Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.SmtpHost))
        {
            throw new InvalidOperationException(
                "Email:SmtpHost is not configured. Set it, or switch Email:Provider to Console.");
        }

        using var client = new SmtpClient(_options.SmtpHost, _options.SmtpPort)
        {
            // STARTTLS: the connection begins in plain text and is upgraded to TLS before
            // credentials are sent. Turn this off and you are posting your password — and
            // every email — across the network in the clear.
            EnableSsl = _options.SmtpUseStartTls,

            Credentials = new NetworkCredential(_options.SmtpUser, _options.SmtpPassword)
        };

        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromName),
            Subject = subject,
            Body = body,

            // Plain text here because our messages are a sentence and a link. Set this true
            // and pass HTML if you want branding — but then you must also send a plain-text
            // alternative, or spam filters will mark you down for having only one part.
            IsBodyHtml = false
        };

        message.To.Add(to);

        try
        {
            // SendMailAsync does NOT accept a CancellationToken on this API. If the provider
            // hangs, this call hangs — which is one more reason a real system queues email on
            // a background worker instead of sending it inside an HTTP request.
            await client.SendMailAsync(message);

            _log.LogInformation("Email sent to {To} via SMTP {Host}", to, _options.SmtpHost);
        }
        catch (SmtpException ex)
        {
            // Swallowed on purpose. A failed password-reset email must not turn the user's
            // request into a 500 — from their side the flow is "we sent you a link if that
            // account exists", and a provider outage should not change the response shape or
            // leak that the address was real.
            //
            // The trade: the user waits for an email that never arrives. That is why anything
            // serious writes to an outbox table and retries, rather than fire-and-forget.
            _log.LogError(ex, "SMTP send to {To} failed", to);
        }
    }
}
