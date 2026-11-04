using Telrang.Web.Features.Settings;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Telrang.Web.Features.Mail;

public sealed record OutgoingMail(string To, string Subject, string TextBody, string? HtmlBody);

/// <summary>Delivers one message. Throws on failure (the sender records the error and retries).</summary>
public interface IMailTransport
{
    Task SendAsync(OutgoingMail mail, MailSettings settings, string? password, CancellationToken ct);
}

/// <summary>SMTP via MailKit. Any SMTP submission server (e.g. port 587, STARTTLS, token as password).</summary>
public sealed class SmtpMailTransport : IMailTransport
{
    public async Task SendAsync(OutgoingMail mail, MailSettings settings, string? password, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName ?? "", settings.FromAddress ?? throw new InvalidOperationException("Mail.FromAddress is not set.")));
        message.To.Add(MailboxAddress.Parse(mail.To));
        message.Subject = mail.Subject;
        var body = new BodyBuilder { TextBody = mail.TextBody, HtmlBody = mail.HtmlBody };
        message.Body = body.ToMessageBody();

        using var client = new SmtpClient { Timeout = 30_000 };
        var security = settings.Security switch
        {
            MailSecurity.None => SecureSocketOptions.None,
            MailSecurity.ImplicitTls => SecureSocketOptions.SslOnConnect,
            _ => SecureSocketOptions.StartTls,
        };
        await client.ConnectAsync(settings.Host ?? throw new InvalidOperationException("Mail.Host is not set."), settings.Port, security, ct);
        if (!string.IsNullOrEmpty(settings.Username))
        {
            // Never send the credential in clear text (pentest AV-11); the admin page refuses this combination too.
            if (settings.Security == MailSecurity.None)
                throw new InvalidOperationException("SMTP login without TLS is refused. Pick STARTTLS or implicit TLS.");
            await client.AuthenticateAsync(settings.Username, password ?? "", ct);
        }
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(quit: true, ct);
    }
}
