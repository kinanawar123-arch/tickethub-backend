using TicketHub.BusinessLogic.Abstractions;

namespace TicketHub.Api.Services;

/// <summary>
/// The development implementation of <see cref="IEmailSender"/>: it writes the email to the
/// application log instead of sending it.
/// </summary>
/// <remarks>
/// WHY THIS IS NOT A HACK.
/// You have no SMTP credentials, and you should not need any to build and test a
/// password-reset flow. This class is a real, deliberate implementation of the contract —
/// it just happens to deliver to your terminal.
/// <para/>
/// Run the app, call <c>POST /api/auth/forgot-password</c>, and the reset link appears in the
/// console. Copy it into your <c>.http</c> file and carry on. The whole flow is exercisable
/// end to end on a laptop with no internet connection.
/// <para/>
/// SWAPPING IN A REAL SENDER is one line in <c>Program.cs</c>:
/// <code>
/// if (builder.Environment.IsDevelopment())
///     builder.Services.AddScoped&lt;IEmailSender, ConsoleEmailSender&gt;();
/// else
///     builder.Services.AddScoped&lt;IEmailSender, SmtpEmailSender&gt;();
/// </code>
/// <c>AuthService</c> does not change, does not recompile differently, and does not know.
/// That is what the interface bought you.
/// <para/>
/// ⚠ It logs at <c>Warning</c> on purpose, so it stands out in a noisy console — and as a
/// reminder that a production deployment accidentally running this would be silently sending
/// nobody anything.
/// </remarks>
public class ConsoleEmailSender : IEmailSender
{
    private readonly ILogger<ConsoleEmailSender> _log;

    public ConsoleEmailSender(ILogger<ConsoleEmailSender> log) => _log = log;

    public Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        _log.LogWarning(
            "\n📧 ───────────── DEV EMAIL (not actually sent) ─────────────\n" +
            "   To:      {To}\n" +
            "   Subject: {Subject}\n" +
            "   {Body}\n" +
            "───────────────────────────────────────────────────────────",
            to, subject, body);

        // Nothing async to do. Task.CompletedTask rather than an async method with no await,
        // which the compiler would warn about and which allocates a state machine for nothing.
        return Task.CompletedTask;
    }
}
