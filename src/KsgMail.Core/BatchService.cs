namespace KsgMail.Core;

public sealed class BatchService(Repository repository, ArchiveService archives, IMailSender sender, string tempRoot)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<PreparedBatch> PrepareAsync(IEnumerable<Customer> customers, AppSettings settings, string period,
        bool allowResend, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        Validation.Settings(settings);
        if (!DateOnly.TryParseExact(period + "-01", "yyyy-MM-dd", out _)) throw new UserException("Niepoprawny okres wysyłki.");
        var directory = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var batch = new PreparedBatch(settings, directory);
        try
        {
            foreach (var customer in customers.Where(customer => customer.Active))
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Przygotowanie: {customer.Name}");
                var subject = Templates.Render(customer.Subject, customer, period);
                var body = Templates.Render(customer.Body, customer, period);
                var path = Path.Combine(directory, customer.Id + ".zip");
                if (!allowResend && repository.IsBlocked(customer.Id, period))
                {
                    batch.Items.Add(new(customer, period, subject, body, "", [], 0, null, true));
                    continue;
                }
                try
                {
                    Validation.Customer(customer);
                    var sourcePath = Path.GetFullPath(customer.SourceDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    var tempPath = Path.GetFullPath(tempRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (tempPath.StartsWith(sourcePath, StringComparison.OrdinalIgnoreCase) ||
                        sourcePath.StartsWith(tempPath, StringComparison.OrdinalIgnoreCase))
                        throw new UserException("Katalog dokumentów nie może obejmować katalogu roboczego KSG Mail.");
                    var files = await Task.Run(() => archives.Discover(customer.SourceDirectory, settings.MaxSourceMb * 1048576L), cancellationToken);
                    await Task.Run(() => archives.Create(path, files, customer, settings.MaxAttachmentMb * 1048576L, cancellationToken), cancellationToken);
                    batch.Items.Add(new(customer, period, subject, body, path, files, new FileInfo(path).Length, null, false));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    if (File.Exists(path)) File.Delete(path);
                    batch.Items.Add(new(customer, period, subject, body, "", [], 0, ErrorMessages.Describe(exception), false));
                }
            }
            return batch;
        }
        catch { batch.Dispose(); throw; }
    }

    public async Task SendAsync(PreparedBatch batch, bool allowResend, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (!await gate.WaitAsync(0, cancellationToken)) throw new UserException("Trwa już wysyłka.");
        try
        {
            foreach (var mail in batch.Items)
            {
                if (cancellationToken.IsCancellationRequested) break;
                progress?.Report($"Wysyłka: {mail.Name}");
                if (mail.Skipped)
                {
                    repository.Log(mail.Customer, mail.Period, DeliveryStatus.Skipped, mail.Status);
                    continue;
                }
                if (!mail.Ready)
                {
                    repository.Log(mail.Customer, mail.Period, DeliveryStatus.Failed, mail.Error!);
                    continue;
                }
                var messageId = SmtpSender.NewMessageId();
                if (!repository.Reserve(mail.Customer, mail.Period, messageId, allowResend))
                {
                    repository.Log(mail.Customer, mail.Period, DeliveryStatus.Skipped, "Blokada ponownej wysyłki.");
                    continue;
                }
                SendOutcome outcome;
                try { outcome = await sender.SendAsync(batch.Settings, mail, messageId, cancellationToken); }
                catch (Exception)
                {
                    outcome = new(DeliveryStatus.Uncertain, "Nie udało się ustalić wyniku wysyłki. Sprawdź u odbiorcy przed ponowieniem.");
                }
                repository.Finish(mail.Customer, mail.Period, outcome.Status, outcome.Detail, messageId);
                progress?.Report($"{mail.Name}: {outcome.Detail}");
            }
        }
        finally { gate.Release(); }
    }

    public async Task RunScheduledAsync(DateTime localNow, CancellationToken cancellationToken)
    {
        var settings = repository.Settings();
        if (!settings.IsDue(localNow)) return;
        var period = localNow.ToString("yyyy-MM");
        var customers = repository.Customers().Where(customer => customer.Active && !repository.IsBlocked(customer.Id, period)).ToList();
        if (customers.Count == 0) return;
        using var batch = await PrepareAsync(customers, settings, period, false, null, cancellationToken);
        await SendAsync(batch, false, null, cancellationToken);
    }
}
