using System.Threading.Channels;
using MailKit.Net.Smtp;
using MailKit.Security;
using MatPlatform;
using MimeKit;

namespace MatDrive.Services.Mail;

/// <summary>One mail waiting to be sent.</summary>
public sealed record OutgoingMail(string To, string ToName, string Subject, string Text);

/// <summary>
/// Mail goes through MatSMTP (the relay of the Mattiverse family) and only when the environment names it (<c>MATSMTP_HOST</c> and friends, <see cref="MatSmtpSettings"/>;
/// Mattiverse sets them). Without it MatDrive sends nothing and everything that needs mail is hidden. Mail is queued and sent in the background, so a page never waits for
/// the mail server and a mail server that is down never breaks a page.
/// </summary>
public sealed class MailService(IConfiguration configuration, ILogger<MailService> logger) : BackgroundService
{
    private readonly MatSmtpSettings _smtp = MatSmtpSettings.From(configuration);
    private readonly Channel<OutgoingMail> _queue = Channel.CreateBounded<OutgoingMail>(new BoundedChannelOptions(200) { FullMode = BoundedChannelFullMode.DropOldest });

    /// <summary>Is there a mail server (MatSMTP)?</summary>
    public bool IsConfigured => _smtp.IsConfigured;

    /// <summary>One sentence that says where the mail goes (never the password), or <c>null</c> without MatSMTP.</summary>
    public string? Describe(string language) => _smtp.Describe(language);

    /// <summary>Puts a mail into the queue; without a mail server nothing happens.</summary>
    public void Enqueue(OutgoingMail mail)
    {
        if (IsConfigured)
        {
            _queue.Writer.TryWrite(mail);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var mail in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await SendAsync(mail, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "A mail to {To} could not be sent.", Redact(mail.To));
            }
        }
    }

    private async Task SendAsync(OutgoingMail mail, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_smtp.FromAddress ?? $"matdrive@{_smtp.Host}"));
        message.To.Add(new MailboxAddress(mail.ToName, mail.To));
        message.Subject = mail.Subject;
        message.Body = new TextPart("plain") { Text = mail.Text };

        using var client = new SmtpClient { Timeout = 15000 };
        var security = _smtp.UseTls ? SecureSocketOptions.SslOnConnect : _smtp.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None;
        await client.ConnectAsync(_smtp.Host!, _smtp.Port, security, ct);
        if (_smtp.HasCredentials)
        {
            await client.AuthenticateAsync(_smtp.Username!, _smtp.Password ?? string.Empty, ct);
        }

        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }

    private static string Redact(string address)
    {
        var at = address.IndexOf('@', StringComparison.Ordinal);
        return at > 1 ? $"{address[0]}***{address[at..]}" : "***";
    }
}
