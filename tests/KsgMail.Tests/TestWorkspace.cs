using System.IO;
using KsgMail.Core;
using Microsoft.Data.Sqlite;

namespace KsgMail.Tests;

public sealed class TestWorkspace : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "KsgMailTests", Guid.NewGuid().ToString("N"));
    public string Source => Path.Combine(Root, "documents");
    public string Temp => Path.Combine(Root, "archives");
    public WindowsSecretProtector Secrets { get; } = new();
    public Repository Repository { get; }
    public AppSettings Settings => new()
    {
        Host = "smtp.example.test", Username = "test", SenderEmail = "sender@example.test",
        PasswordProtected = Secrets.Protect("smtp-secret"), RetryCount = 1
    };
    public Customer Customer => new()
    {
        Name = "Klient testowy", Email = "customer@example.test", SourceDirectory = Source,
        ZipPasswordProtected = Secrets.Protect("zip-secret")
    };

    public TestWorkspace()
    {
        Directory.CreateDirectory(Source);
        Repository = new Repository(Path.Combine(Root, "data.db"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(Root, true);
    }
}
