using System.IO;
using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.Zip;
using KsgMail.Core;
using Xunit;

namespace KsgMail.Tests;

public sealed class CoreTests
{
    [Fact]
    public void SecretsAndDatabaseNeverPersistPlaintextPasswords()
    {
        using var workspace = new TestWorkspace();
        var customer = workspace.Customer;
        workspace.Repository.SaveCustomer(customer);
        workspace.Repository.SaveSettings(workspace.Settings);
        Assert.Equal("zip-secret", workspace.Secrets.Unprotect(workspace.Repository.Customers().Single().ZipPasswordProtected));
        Assert.Equal("smtp-secret", workspace.Secrets.Unprotect(workspace.Repository.Settings().PasswordProtected));
        Assert.DoesNotContain("zip-secret", customer.ZipPasswordProtected);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(workspace.Root, "data.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Data FROM Customers UNION ALL SELECT Data FROM Settings";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            Assert.DoesNotContain("zip-secret", reader.GetString(0));
            Assert.DoesNotContain("smtp-secret", reader.GetString(0));
        }
        Assert.Throws<UserException>(() => workspace.Secrets.Unprotect("invalid-base64"));
    }

    [Fact]
    public void ArchiveUsesAes256AndPreservesNestedUnicodeFiles()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.Source, "Zażółć"));
        File.WriteAllText(Path.Combine(workspace.Source, "Zażółć", "faktura.txt"), "Treść dokumentu");
        var service = new ArchiveService(workspace.Secrets);
        var files = service.Discover(workspace.Source, 100000);
        var path = Path.Combine(workspace.Root, "encrypted.zip");
        service.Create(path, files, workspace.Customer, 100000, CancellationToken.None);
        using var zip = new ZipFile(path) { Password = "zip-secret" };
        var entry = zip[0];
        Assert.Equal(256, entry.AESKeySize);
        Assert.True(entry.IsCrypted);
        Assert.Equal("Zażółć/faktura.txt", entry.Name);
        using var content = new StreamReader(zip.GetInputStream(entry));
        Assert.Equal("Treść dokumentu", content.ReadToEnd());
        zip.Password = "wrong-secret";
        Assert.Throws<ZipException>(() => zip.GetInputStream(entry).ReadByte());
    }

    [Fact]
    public void MissingEmptyChangedAndOversizedFilesAreRejected()
    {
        using var workspace = new TestWorkspace();
        var service = new ArchiveService(workspace.Secrets);
        Assert.Throws<UserException>(() => service.Discover(Path.Combine(workspace.Root, "missing"), 100));
        Assert.Throws<UserException>(() => service.Discover(workspace.Source, 100));
        var path = Path.Combine(workspace.Source, "file.bin");
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(4096));
        Assert.Throws<UserException>(() => service.Discover(workspace.Source, 100));
        var files = service.Discover(workspace.Source, 10000);
        Assert.Throws<UserException>(() => service.Create(Path.Combine(workspace.Root, "large.zip"), files, workspace.Customer, 100, CancellationToken.None));
        File.AppendAllText(path, "changed");
        Assert.Throws<UserException>(() => service.Create(Path.Combine(workspace.Root, "changed.zip"), files, workspace.Customer, 10000, CancellationToken.None));
    }

    [Fact]
    public void ReservationRecoveryAndSuccessProtectAgainstDuplicateSending()
    {
        using var workspace = new TestWorkspace();
        var customer = workspace.Customer;
        var repository = workspace.Repository;
        Assert.True(repository.Reserve(customer, "2026-10", "first", false));
        Assert.False(repository.Reserve(customer, "2026-10", "second", false));
        repository.RecoverInterrupted();
        Assert.Equal(DeliveryStatus.Uncertain, repository.Logs().Single().Status);
        Assert.True(repository.IsBlocked(customer.Id, "2026-10"));
        Assert.False(repository.Reserve(customer, "2026-10", "third", false));
        Assert.True(repository.Reserve(customer, "2026-10", "fourth", true));
        repository.Finish(customer, "2026-10", DeliveryStatus.Success, "accepted", "fourth");
        repository.RecoverInterrupted();
        Assert.Equal(DeliveryStatus.Success, repository.Logs().First().Status);
        Assert.False(repository.Reserve(customer, "2026-10", "fifth", false));
        Assert.True(repository.Reserve(customer, "2026-11", "next-month", false));
    }

    [Fact]
    public void FailedDeliveryCanRetryAndPendingCannotBeForced()
    {
        using var workspace = new TestWorkspace();
        var customer = workspace.Customer;
        var repository = workspace.Repository;
        Assert.True(repository.Reserve(customer, "2026-10", "first", false));
        Assert.False(repository.Reserve(customer, "2026-10", "forced-pending", true));
        repository.Finish(customer, "2026-10", DeliveryStatus.Failed, "rejected", "first");
        Assert.True(repository.Reserve(customer, "2026-10", "retry", false));
    }

    [Theory]
    [InlineData("alice@example.test,bob@example.test")]
    [InlineData("Alice <alice@example.test>")]
    [InlineData("alice@example.test\r\nBcc: evil@example.test")]
    [InlineData("not-an-address")]
    public void InvalidOrMultipleRecipientsAreRejected(string value) =>
        Assert.Throws<UserException>(() => Validation.Email(value, "Klient"));

    [Fact]
    public void MonthlyScheduleHonorsDayTimeAndDisabledFlag()
    {
        using var workspace = new TestWorkspace();
        var settings = workspace.Settings with { ScheduleEnabled = true, ScheduleDay = 28, ScheduleTime = "09:30" };
        Validation.Settings(settings);
        Assert.False(settings.IsDue(new DateTime(2026, 2, 28, 9, 29, 0)));
        Assert.True(settings.IsDue(new DateTime(2026, 2, 28, 9, 30, 0)));
        Assert.False((settings with { ScheduleEnabled = false }).IsDue(new DateTime(2026, 2, 28, 10, 0, 0)));
        Assert.Throws<UserException>(() => Validation.Settings(settings with { ScheduleDay = 31 }));
        Assert.Throws<UserException>(() => Validation.Settings(settings with { ScheduleTime = "25:00" }));
    }

    [Fact]
    public async Task PreviewSnapshotAndBatchHandleInactiveMissingAndRepeatedCustomers()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.Source, "file.txt"), "original");
        var customer = workspace.Customer;
        var inactive = workspace.Customer with { Active = false };
        var missing = workspace.Customer with { SourceDirectory = Path.Combine(workspace.Root, "missing") };
        var sender = new RecordingSender();
        var service = new BatchService(workspace.Repository, new ArchiveService(workspace.Secrets), sender, workspace.Temp);
        using var batch = await service.PrepareAsync([customer, inactive, missing], workspace.Settings, "2026-10", false, null, CancellationToken.None);
        Assert.Equal(2, batch.Items.Count);
        Assert.Single(batch.Items, mail => mail.Ready);
        Assert.NotNull(batch.Items.Single(mail => mail.Customer.Id == missing.Id).Error);
        File.WriteAllText(Path.Combine(workspace.Source, "file.txt"), "modified after preview");
        await service.SendAsync(batch, false, null, CancellationToken.None);
        Assert.Equal(1, sender.Count);
        Assert.Equal("original", sender.AttachmentText);
        await service.SendAsync(batch, false, null, CancellationToken.None);
        Assert.Equal(1, sender.Count);
        Assert.Contains(workspace.Repository.Logs(), log => log.Status == DeliveryStatus.Failed);
        Assert.Contains(workspace.Repository.Logs(), log => log.Status == DeliveryStatus.Skipped);
        var directory = Path.GetDirectoryName(batch.Items.First().ArchivePath)!;
        batch.Dispose();
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task ScheduledRunSkipsAlreadySentCustomersForSameMonth()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.Source, "file.txt"), "document");
        workspace.Repository.SaveCustomer(workspace.Customer);
        workspace.Repository.SaveSettings(workspace.Settings with { ScheduleEnabled = true, ScheduleTime = "00:00" });
        var sender = new RecordingSender();
        var service = new BatchService(workspace.Repository, new ArchiveService(workspace.Secrets), sender, workspace.Temp);
        await service.RunScheduledAsync(new DateTime(2026, 10, 1), CancellationToken.None);
        await service.RunScheduledAsync(new DateTime(2026, 10, 2), CancellationToken.None);
        Assert.Equal(1, sender.Count);
        await service.RunScheduledAsync(new DateTime(2026, 11, 1), CancellationToken.None);
        Assert.Equal(2, sender.Count);
    }

    private sealed class RecordingSender : IMailSender
    {
        public int Count { get; private set; }
        public string? AttachmentText { get; private set; }
        public Task<SendOutcome> SendAsync(AppSettings settings, PreparedMail mail, string messageId, CancellationToken cancellationToken)
        {
            Count++;
            using var zip = new ZipFile(mail.ArchivePath) { Password = "zip-secret" };
            using var reader = new StreamReader(zip.GetInputStream(zip[0]));
            AttachmentText = reader.ReadToEnd();
            return Task.FromResult(new SendOutcome(DeliveryStatus.Success, "accepted"));
        }
    }
}
