namespace TicketHub.BusinessLogic.Abstractions;

/// <summary>
/// "Send this person an email." Nothing about SMTP, SendGrid, or how it happens.
/// </summary>
/// <remarks>
/// THE SAME INVERSION AS <see cref="IRealtimeNotifier"/>, AND FOR THE SAME REASON.
/// <para/>
/// <c>AuthService</c> needs to send a password-reset link. It must not care whether that goes
/// through SMTP, an HTTP API, a queue, or — in development — straight into your terminal.
/// So the business layer declares <em>what it needs</em> and something outside it supplies
/// <em>how it is done</em>.
/// <para/>
/// The practical payoff is immediate: you can finish and test the entire forgot-password flow
/// today, with no SMTP credentials, no mail server, and no account on anybody's platform.
/// Swapping in a real provider later is one line in <c>Program.cs</c> and zero changes to
/// <c>AuthService</c>.
/// <para/>
/// Note it returns <c>Task</c> and not a success flag. Sending mail is best-effort and slow;
/// if delivery genuinely matters you queue it and retry, rather than making the user's HTTP
/// request wait for a third party.
/// </remarks>
public interface IEmailSender
{
    /// <param name="to">The recipient address.</param>
    /// <param name="subject">Subject line.</param>
    /// <param name="body">Plain text or HTML — the implementation decides how to treat it.</param>
    Task SendAsync(string to, string subject, string body, CancellationToken ct = default);
}
