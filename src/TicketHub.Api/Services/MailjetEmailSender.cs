using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TicketHub.BusinessLogic.Abstractions;
using TicketHub.BusinessLogic.Options;

namespace TicketHub.Api.Services;

/// <summary>
/// Sends email through Mailjet's HTTP API instead of SMTP.
/// </summary>
/// <remarks>
/// WHY AN HTTP API RATHER THAN SMTP, WHEN BOTH SEND THE SAME EMAIL?
/// <list type="bullet">
/// <item><b>It is just HTTPS.</b> Port 443, which is open everywhere. Many hosting providers
///       and most corporate networks block outbound port 587 or 25 to stop spam — so SMTP
///       works on your laptop and mysteriously does not in production.</item>
/// <item><b>Real error messages.</b> A failed API call returns JSON telling you the address
///       was malformed or the account is over quota. SMTP returns a numeric code and a
///       sentence written in 1982.</item>
/// <item><b>Batching.</b> One request can carry many messages — note the <c>Messages</c>
///       array below. Sending 500 notifications over SMTP is 500 conversations.</item>
/// <item><b>Delivery data.</b> Opens, bounces, spam complaints and webhooks, which SMTP has
///       no concept of.</item>
/// </list>
/// The cost is lock-in: this class knows Mailjet's JSON shape, so moving to SendGrid means
/// rewriting it. That is exactly why the rest of the application depends on
/// <see cref="IEmailSender"/> and not on this class — the lock-in stops at this file.
/// <para/>
/// ⚠ This compiles and the request shape matches Mailjet's Send API v3.1, but it has not been
/// exercised against the live service (that needs a real account). Treat it as a worked
/// example, not as tested code.
/// </remarks>
public class MailjetEmailSender : IEmailSender
{
    private readonly HttpClient _http;
    private readonly EmailOptions _options;
    private readonly ILogger<MailjetEmailSender> _log;

    /// <summary>
    /// <see cref="HttpClient"/> is injected, never newed up.
    /// </summary>
    /// <remarks>
    /// <c>new HttpClient()</c> per send exhausts the machine's sockets under load — every
    /// instance holds its connection in TIME_WAIT for minutes after disposal. This one comes
    /// from <c>IHttpClientFactory</c> (see the <c>AddHttpClient</c> call in Program.cs), which
    /// pools and recycles the underlying handlers for you.
    /// </remarks>
    public MailjetEmailSender(
        HttpClient http,
        IOptions<EmailOptions> options,
        ILogger<MailjetEmailSender> log)
    {
        _http = http;
        _options = options.Value;
        _log = log;
    }

    public async Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.MailjetApiKey) ||
            string.IsNullOrWhiteSpace(_options.MailjetApiSecret))
        {
            throw new InvalidOperationException(
                "Email:MailjetApiKey / MailjetApiSecret are not configured.");
        }

        // Mailjet authenticates with HTTP Basic: base64("apiKey:apiSecret").
        //
        // Basic auth sends the credential on every request, so it is only acceptable over
        // HTTPS — which is why the base address below is https and must stay that way.
        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_options.MailjetApiKey}:{_options.MailjetApiSecret}"));

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.mailjet.com/v3.1/send");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        // The Send API v3.1 payload. The Messages array is what makes batching possible —
        // we send one, but the shape allows hundreds.
        var payload = new
        {
            Messages = new[]
            {
                new
                {
                    From = new { Email = _options.FromAddress, Name = _options.FromName },
                    To = new[] { new { Email = to, Name = to } },
                    Subject = subject,
                    TextPart = body
                }
            }
        };

        request.Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        try
        {
            var response = await _http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                // The useful half of using an HTTP API: the body says what was wrong.
                var problem = await response.Content.ReadAsStringAsync(ct);

                _log.LogError("Mailjet rejected the message for {To}: {Status} {Body}",
                    to, (int)response.StatusCode, problem);

                return;
            }

            _log.LogInformation("Email sent to {To} via Mailjet", to);
        }
        catch (HttpRequestException ex)
        {
            // Same reasoning as SmtpEmailSender: a provider outage must not become a 500 on
            // the user's password-reset request.
            _log.LogError(ex, "Mailjet request for {To} failed", to);
        }
        catch (TaskCanceledException ex)
        {
            _log.LogError(ex, "Mailjet request for {To} timed out", to);
        }
    }
}
