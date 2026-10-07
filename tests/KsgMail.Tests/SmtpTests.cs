using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ICSharpCode.SharpZipLib.Zip;
using KsgMail.Core;
using MailKit.Net.Smtp;
using MimeKit;
using Xunit;

namespace KsgMail.Tests;

public sealed class SmtpTests
{
    [Theory]
    [InlineData(ServerBehavior.Accept, DeliveryStatus.Success, 1)]
    [InlineData(ServerBehavior.TemporaryThenAccept, DeliveryStatus.Success, 2)]
    [InlineData(ServerBehavior.Reject, DeliveryStatus.Failed, 1)]
    [InlineData(ServerBehavior.DropAfterData, DeliveryStatus.Uncertain, 1)]
    public async Task TlsSmtpSendsEncryptedAttachmentAndRetriesOnlySafeFailures(ServerBehavior behavior, DeliveryStatus expected, int attempts)
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.Source, "dokument.txt"), "tajny dokument");
        await using var server = new LocalSmtpServer(behavior);
        var settings = workspace.Settings with { Host = "localhost", Port = server.Port,
            Security = TransportSecurity.SslOnConnect, RetryCount = 3 };
        var sender = new SmtpSender(workspace.Secrets, () => new SmtpClient
        {
            ServerCertificateValidationCallback = (_, certificate, _, _) =>
                certificate?.GetCertHashString() == server.Certificate.GetCertHashString()
        });
        var service = new BatchService(workspace.Repository, new ArchiveService(workspace.Secrets), sender, workspace.Temp);
        using var batch = await service.PrepareAsync([workspace.Customer], settings, "2026-10", false, null, CancellationToken.None);
        var outcome = await sender.SendAsync(settings, batch.Items.Single(), "fixed-message@example.test", CancellationToken.None);
        Assert.True(expected == outcome.Status, $"Expected {expected}, got {outcome.Status}: {outcome.Detail}; server: {server.LastError}");
        Assert.Equal(attempts, server.Connections);
        if (expected == DeliveryStatus.Success)
        {
            using var message = MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(server.Message!)));
            Assert.Equal("fixed-message@example.test", message.MessageId);
            Assert.Equal("customer@example.test", message.To.Mailboxes.Single().Address);
            Assert.Contains("2026-10", message.TextBody);
            var attachment = Assert.IsType<MimePart>(message.Attachments.Single());
            using var bytes = new MemoryStream();
            attachment.Content!.DecodeTo(bytes);
            bytes.Position = 0;
            using var zip = new ZipFile(bytes) { Password = "zip-secret" };
            Assert.Equal(256, zip[0].AESKeySize);
            using var text = new StreamReader(zip.GetInputStream(zip[0]));
            Assert.Equal("tajny dokument", text.ReadToEnd());
        }
    }

    [Fact]
    public async Task UntrustedCertificateFailsWithoutSending()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.Source, "document.txt"), "test");
        await using var server = new LocalSmtpServer(ServerBehavior.Accept);
        var settings = workspace.Settings with { Host = "localhost", Port = server.Port,
            Security = TransportSecurity.SslOnConnect };
        var sender = new SmtpSender(workspace.Secrets);
        var service = new BatchService(workspace.Repository, new ArchiveService(workspace.Secrets), sender, workspace.Temp);
        using var batch = await service.PrepareAsync([workspace.Customer], settings, "2026-10", false, null, CancellationToken.None);
        var outcome = await sender.SendAsync(settings, batch.Items.Single(), "tls-test@example.test", CancellationToken.None);
        Assert.Equal(DeliveryStatus.Failed, outcome.Status);
        Assert.Equal(1, server.Connections);
        Assert.Null(server.Message);
    }

    public enum ServerBehavior { Accept, TemporaryThenAccept, Reject, DropAfterData }

    private sealed class LocalSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Task loop;
        private readonly ServerBehavior behavior;
        private int connections;
        public int Connections => connections;
        public int Port { get; }
        public string? Message { get; private set; }
        public string? LastError { get; private set; }
        public X509Certificate2 Certificate { get; }

        public LocalSmtpServer(ServerBehavior behavior)
        {
            this.behavior = behavior;
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            Certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx));
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            loop = Task.Run(RunAsync);
        }

        private async Task RunAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var connection = await listener.AcceptTcpClientAsync(stop.Token);
                    Interlocked.Increment(ref connections);
                    using var tls = new SslStream(connection.GetStream(), false);
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = Certificate }, stop.Token);
                    using var reader = new StreamReader(tls, Encoding.UTF8);
                    using var writer = new StreamWriter(tls, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };
                    await writer.WriteLineAsync("220 localhost ESMTP");
                    while (!stop.IsCancellationRequested && await reader.ReadLineAsync(stop.Token) is { } command)
                    {
                        if (command.StartsWith("EHLO", StringComparison.Ordinal))
                            await writer.WriteLineAsync("250-localhost\r\n250-AUTH PLAIN\r\n250 SIZE 30000000");
                        else if (command.StartsWith("AUTH", StringComparison.Ordinal))
                        {
                            if (command == "AUTH PLAIN")
                            {
                                await writer.WriteLineAsync("334 ");
                                await reader.ReadLineAsync(stop.Token);
                            }
                            await writer.WriteLineAsync("235 authenticated");
                        }
                        else if (command.StartsWith("MAIL FROM:", StringComparison.Ordinal))
                            await writer.WriteLineAsync("250 OK");
                        else if (command.StartsWith("RCPT TO:", StringComparison.Ordinal))
                            await writer.WriteLineAsync(behavior == ServerBehavior.Reject ? "550 recipient rejected"
                                : behavior == ServerBehavior.TemporaryThenAccept && connections == 1 ? "451 try again" : "250 OK");
                        else if (command == "DATA")
                        {
                            await writer.WriteLineAsync("354 send data");
                            var content = new StringBuilder();
                            while (await reader.ReadLineAsync(stop.Token) is { } line && line != ".")
                                content.AppendLine(line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line);
                            if (behavior == ServerBehavior.DropAfterData) break;
                            Message = content.ToString();
                            await writer.WriteLineAsync("250 queued");
                        }
                        else if (command == "QUIT")
                        {
                            await writer.WriteLineAsync("221 goodbye");
                            break;
                        }
                        else await writer.WriteLineAsync("250 OK");
                    }
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException or
                    System.Security.Authentication.AuthenticationException or SocketException or ObjectDisposedException)
                { LastError = exception.Message; }
            }
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            await loop;
            stop.Dispose();
            Certificate.Dispose();
        }
    }
}
