using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MimeKit.Utils;

namespace KsgMail.Core;

public sealed record SendOutcome(DeliveryStatus Status, string Detail);
public interface IMailSender
{
    Task<SendOutcome> SendAsync(AppSettings settings, PreparedMail mail, string messageId, CancellationToken cancellationToken);
}

public sealed class SmtpSender(ISecretProtector secrets, Func<SmtpClient>? clientFactory = null) : IMailSender
{
    private SmtpClient CreateClient(int timeout)
    {
        var client = clientFactory?.Invoke() ?? new SmtpClient();
        client.Timeout = timeout;
        return client;
    }

    public async Task TestAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        Validation.Settings(settings);
        using var client = CreateClient(30000);
        await ConnectAsync(client, settings, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }

    private async Task ConnectAsync(SmtpClient client, AppSettings settings, CancellationToken cancellationToken)
    {
        await client.ConnectAsync(settings.Host, settings.Port,
            settings.Security == TransportSecurity.SslOnConnect ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls,
            cancellationToken);
        await client.AuthenticateAsync(settings.Username, secrets.Unprotect(settings.PasswordProtected), cancellationToken);
    }

    public async Task<SendOutcome> SendAsync(AppSettings settings, PreparedMail mail, string messageId, CancellationToken cancellationToken)
    {
        using var message = new MimeMessage { Subject = mail.Subject, MessageId = messageId, Date = DateTimeOffset.Now };
        message.From.Add(new MailboxAddress(settings.SenderName, settings.SenderEmail));
        message.To.Add(MailboxAddress.Parse(mail.Customer.Email));
        using var attachment = new FileStream(mail.ArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        message.Body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = mail.Body },
            new MimePart("application", "zip")
            {
                Content = new MimeContent(attachment),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                ContentTransferEncoding = ContentEncoding.Base64,
                FileName = $"dokumenty-{mail.Period}.zip"
            }
        };
        for (var attempt = 1; attempt <= settings.RetryCount; attempt++)
        {
            var sendStarted = false;
            using var client = CreateClient(60000);
            try
            {
                attachment.Position = 0;
                await ConnectAsync(client, settings, cancellationToken);
                using var encoded = new CountingStream();
                await message.WriteToAsync(encoded, cancellationToken);
                if (client.MaxSize > 0 && encoded.Length > client.MaxSize)
                    return new(DeliveryStatus.Failed, "Wiadomość MIME przekracza limit serwera SMTP.");
                sendStarted = true;
                await client.SendAsync(message, cancellationToken);
                try { await client.DisconnectAsync(true, CancellationToken.None); }
                catch (Exception exception) when (exception is IOException or SmtpProtocolException or SmtpCommandException) { }
                return new(DeliveryStatus.Success, "Serwer SMTP przyjął wiadomość.");
            }
            catch (Exception exception)
            {
                var explicitRejection = exception is SmtpCommandException;
                if (sendStarted && !explicitRejection)
                    return new(DeliveryStatus.Uncertain, "Połączenie przerwano podczas wysyłki. Sprawdź u odbiorcy przed ponowieniem.");
                if (cancellationToken.IsCancellationRequested)
                    return new(DeliveryStatus.Failed, "Anulowano przed przyjęciem wiadomości.");
                var transient = exception is SmtpCommandException command
                    ? (int)command.StatusCode is >= 400 and < 500
                    : exception is IOException or System.Net.Sockets.SocketException or SmtpProtocolException;
                if (!transient || attempt == settings.RetryCount)
                    return new(DeliveryStatus.Failed, ErrorMessages.Describe(exception));
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken); }
                catch (OperationCanceledException) { return new(DeliveryStatus.Failed, "Anulowano podczas oczekiwania na ponowienie."); }
            }
        }
        return new(DeliveryStatus.Failed, "Wyczerpano próby SMTP.");
    }

    public static string NewMessageId() => MimeUtils.GenerateMessageId();

    private sealed class CountingStream : Stream
    {
        private long length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => length;
        public override long Position { get => length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => length += count;
        public override void Write(ReadOnlySpan<byte> buffer) => length += buffer.Length;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

public static class ErrorMessages
{
    public static string Describe(Exception exception) => exception switch
    {
        UserException user => user.Message,
        MailKit.Security.AuthenticationException => "Błąd logowania SMTP. Sprawdź login i hasło / hasło aplikacji.",
        SslHandshakeException => "Błąd TLS lub certyfikatu serwera. Sprawdź host, port i tryb TLS.",
        SmtpCommandException command => $"Serwer SMTP odrzucił polecenie (kod {(int)command.StatusCode}). Sprawdź nadawcę, odbiorcę i limity.",
        UnauthorizedAccessException => "Brak uprawnień do katalogu lub pliku.",
        FileNotFoundException or DirectoryNotFoundException => "Nie znaleziono katalogu lub pliku.",
        IOException => "Błąd dostępu do plików lub połączenia. Sprawdź katalog i połączenie z serwerem.",
        OperationCanceledException => "Operacja anulowana.",
        System.Net.Sockets.SocketException => "Nie można połączyć się z serwerem SMTP.",
        _ => "Operacja nie powiodła się. Sprawdź konfigurację i dostępność serwera / plików."
    };
}
