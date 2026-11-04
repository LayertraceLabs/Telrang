using System.Text.RegularExpressions;
using Telrang.Web.Features.Mail;
using Telrang.Web.Features.Settings;

namespace Telrang.Web.Tests;

/// <summary>Records sends instead of talking SMTP. Set <see cref="Fail"/> to make every send throw.</summary>
public sealed partial class FakeMailTransport : IMailTransport
{
    private readonly List<OutgoingMail> sent = [];

    public bool Fail { get; set; }

    public IReadOnlyList<OutgoingMail> Sent
    {
        get { lock (sent) return sent.ToList(); }
    }

    public Task SendAsync(OutgoingMail mail, MailSettings settings, string? password, CancellationToken ct)
    {
        if (Fail) throw new InvalidOperationException("SMTP is down (fake).");
        lock (sent) sent.Add(mail);
        return Task.CompletedTask;
    }

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex UrlRegex();

    /// <summary>The first URL in the last mail to <paramref name="to"/>, as a path+query for the test client.</summary>
    public string LinkTo(string to)
    {
        var mail = Sent.Last(m => string.Equals(m.To, to, StringComparison.OrdinalIgnoreCase));
        var url = new Uri(UrlRegex().Match(mail.TextBody).Value);
        return url.PathAndQuery;
    }
}
