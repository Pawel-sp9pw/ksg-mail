using ICSharpCode.SharpZipLib.Zip;

namespace KsgMail.Core;

public sealed record PreparedMail(Customer Customer, string Period, string Subject, string Body,
    string ArchivePath, IReadOnlyList<SourceFile> Files, long ArchiveBytes, string? Error, bool Skipped)
{
    public string Name => Customer.Name;
    public string Email => Customer.Email;
    public string Status => Skipped ? "Pominięty: już wysłano / status niepewny" : Error ?? "Gotowy";
    public string ZipSize => ArchiveBytes == 0 ? "—" : $"{ArchiveBytes / 1048576d:F2} MB";
    public int FileCount => Files.Count;
    public bool Ready => Error == null && !Skipped;
}

public sealed class PreparedBatch(AppSettings settings, string directory) : IDisposable
{
    public AppSettings Settings { get; } = settings;
    public List<PreparedMail> Items { get; } = [];
    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}

public sealed class ArchiveService(ISecretProtector secrets)
{
    public const string SentArchiveDirectoryName = "archiwum";
    public IReadOnlyList<SourceFile> Discover(string directory, long maxSourceBytes)
    {
        if (!Directory.Exists(directory)) throw new UserException("Katalog źródłowy nie istnieje lub jest niedostępny.");
        var root = Path.GetFullPath(directory);
        var pending = new Stack<string>();
        pending.Push(root);
        var files = new List<SourceFile>();
        long total = 0;
        while (pending.TryPop(out var current))
        {
            RejectLink(current);
            foreach (var subdirectory in Directory.EnumerateDirectories(current))
            {
                if (Path.GetFullPath(current).Equals(root, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(subdirectory).Equals(SentArchiveDirectoryName, StringComparison.OrdinalIgnoreCase))
                    continue;
                pending.Push(subdirectory);
            }
            foreach (var path in Directory.EnumerateFiles(current))
            {
                RejectLink(path);
                var info = new FileInfo(path);
                total = checked(total + info.Length);
                if (total > maxSourceBytes) throw new UserException("Pliki źródłowe przekraczają skonfigurowany limit.");
                files.Add(new SourceFile(path, Path.GetRelativePath(root, path).Replace('\\', '/'), info.Length, info.LastWriteTimeUtc));
                if (files.Count > 10000) throw new UserException("Katalog zawiera ponad 10 000 plików.");
            }
        }
        if (files.Count == 0) throw new UserException("Katalog jest pusty. Wiadomość nie zostanie wysłana.");
        return files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToList();
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UserException("Katalog zawiera dowiązanie lub plik chmurowy. Użyj zwykłego lokalnego katalogu.");
    }

    public IReadOnlyList<string> ArchiveSent(string sourceDirectory, IReadOnlyList<SourceFile> files, DateOnly sentDate)
    {
        var sourceRoot = Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var archiveRoot = Path.Combine(sourceRoot, SentArchiveDirectoryName, sentDate.ToString("yyyy-MM-dd"));
        var moved = new List<string>();

        foreach (var file in files)
        {
            var source = Path.GetFullPath(file.FullPath);
            if (!source.StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new UserException("Plik do archiwizacji znajduje się poza katalogiem klienta.");
            if (!File.Exists(source))
                throw new UserException($"Plik wysłany nie istnieje już w katalogu źródłowym: {file.RelativePath}");
            RejectLink(source);
            var info = new FileInfo(source);
            if (info.Length != file.Length || info.LastWriteTimeUtc != file.LastWriteUtc)
                throw new UserException($"Plik zmienił się po przygotowaniu wiadomości i nie został przeniesiony do archiwum: {file.RelativePath}");
        }

        foreach (var file in files)
        {
            var source = Path.GetFullPath(file.FullPath);
            var relative = Path.GetRelativePath(sourceRoot, source);
            if (relative.StartsWith(SentArchiveDirectoryName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;

            var destination = Path.Combine(archiveRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            destination = UniqueDestination(destination);
            File.Move(source, destination);
            File.SetLastWriteTimeUtc(destination, DateTime.UtcNow);
            moved.Add(destination);
        }

        RemoveEmptySourceDirectories(sourceRoot);
        return moved;
    }

    private static string UniqueDestination(string path)
    {
        if (!File.Exists(path)) return path;
        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static void RemoveEmptySourceDirectories(string sourceRoot)
    {
        var archiveRoot = Path.Combine(sourceRoot, SentArchiveDirectoryName);
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories)
                     .Where(path => !path.StartsWith(archiveRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(path => path.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
    }

    public void Create(string destination, IReadOnlyList<SourceFile> files, Customer customer,
        long maxArchiveBytes, CancellationToken cancellationToken)
    {
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var zip = new ZipOutputStream(output) { Password = secrets.Unprotect(customer.ZipPasswordProtected), IsStreamOwner = false };
        zip.SetLevel(6);
        var buffer = new byte[81920];
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectLink(file.FullPath);
            using var input = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length != file.Length || File.GetLastWriteTimeUtc(file.FullPath) != file.LastWriteUtc)
                throw new UserException("Pliki zmieniły się podczas przygotowania. Przygotuj podgląd ponownie.");
            zip.PutNextEntry(new ZipEntry(file.RelativePath) { AESKeySize = 256, Size = file.Length, DateTime = file.LastWriteUtc });
            int count;
            while ((count = input.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                zip.Write(buffer, 0, count);
                if (output.Position > maxArchiveBytes) throw new UserException("ZIP przekracza limit załącznika.");
            }
            zip.CloseEntry();
        }
        zip.Finish();
        output.Flush();
        if (output.Length > maxArchiveBytes) throw new UserException("ZIP przekracza limit załącznika.");
    }
}
