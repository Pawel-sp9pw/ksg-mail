using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KsgMail.Core;
using Microsoft.Win32;
using Validation = KsgMail.Core.Validation;

namespace KsgMail.App;

public partial class MainWindow : Window
{
    private readonly Repository repository;
    private readonly ISecretProtector secrets;
    private readonly SmtpSender sender;
    private readonly BatchService batches;
    private readonly WindowsScheduler scheduler = new();
    private readonly GitHubUpdateService updater = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private Customer editing = new();
    private AppSettings settings = new();
    private PreparedBatch? prepared;
    private bool preparedResend;
    private CancellationTokenSource? operation;
    private bool busy;
    private DateTime nextScheduleCheck = DateTime.MinValue;
    private UpdateInfo? availableUpdate;
    private bool initialUpdateCheckDone;
    private List<Customer> allCustomers = [];
    private bool loadingLogFilter;

    public MainWindow(Repository repository, ISecretProtector secrets, SmtpSender sender, BatchService batches)
    {
        this.repository = repository;
        this.secrets = secrets;
        this.sender = sender;
        this.batches = batches;
        InitializeComponent();
        Period.Text = DateTime.Now.ToString("yyyy-MM");
        LoadSettings();
        LoadCustomers();
        ShowCustomer(new Customer());
        LoadLogs();
        CurrentVersionText.Text = GitHubUpdateService.CurrentVersion.ToString(3);
        LatestVersionText.Text = "—";
        timer.Tick += ScheduleTick;
        timer.Start();
    }

    private void LoadCustomers()
    {
        allCustomers = repository.Customers().ToList();
        ApplyCustomerFilter();
    }

    private void ApplyCustomerFilter()
    {
        if (CustomersGrid == null) return;
        var search = CustomerSearch?.Text?.Trim() ?? "";
        var state = CustomerStateFilter?.SelectedIndex ?? 0;
        var filtered = allCustomers.Where(customer =>
            (search.Length == 0 ||
             customer.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
             customer.Email.Contains(search, StringComparison.CurrentCultureIgnoreCase)) &&
            (state == 0 || state == 1 && customer.Active || state == 2 && !customer.Active))
            .ToList();

        CustomersGrid.ItemsSource = filtered;
        if (CustomerCountText != null)
            CustomerCountText.Text = $"Widoczne: {filtered.Count} / {allCustomers.Count}";
    }

    private void CustomerFilterChanged(object senderObject, RoutedEventArgs args) => ApplyCustomerFilter();

    private List<Customer> SelectedCustomers() => CustomersGrid.SelectedItems.Cast<Customer>().ToList();

    private void SelectAllVisibleCustomers(object senderObject, RoutedEventArgs args)
    {
        CustomersGrid.SelectAll();
        StatusText.Text = $"Zaznaczono {CustomersGrid.SelectedItems.Count} widocznych klientów.";
    }

    private void ClearCustomerSelection(object senderObject, RoutedEventArgs args)
    {
        CustomersGrid.UnselectAll();
        StatusText.Text = "Wyczyszczono zaznaczenie klientów.";
    }

    private void ShowCustomer(Customer customer)
    {
        editing = customer;
        CustomerName.Text = customer.Name;
        CustomerEmail.Text = customer.Email;
        CustomerSubject.Text = customer.Subject;
        CustomerBody.Text = customer.Body;
        CustomerDirectory.Text = customer.SourceDirectory;
        CustomerPassword.Clear();
        ZipPasswordLabel.Text = customer.ZipPasswordProtected.Length == 0 ? "Hasło ZIP" : "Hasło ZIP (zapisane; wpisz, aby zmienić)";
        CustomerActive.IsChecked = customer.Active;
    }

    private void NewCustomer(object senderObject, RoutedEventArgs args)
    {
        CustomersGrid.SelectedItem = null;
        ShowCustomer(new Customer());
    }

    private void CustomerSelected(object senderObject, SelectionChangedEventArgs args)
    {
        if (CustomersGrid.SelectedItems.Count == 1 && CustomersGrid.SelectedItem is Customer customer)
            ShowCustomer(customer);
    }

    private void BrowseDirectory(object senderObject, RoutedEventArgs args)
    {
        var dialog = new OpenFolderDialog { Title = "Katalog dokumentów klienta" };
        if (dialog.ShowDialog(this) == true) CustomerDirectory.Text = dialog.FolderName;
    }

    private void SaveCustomer(object senderObject, RoutedEventArgs args) => Guard(() =>
    {
        var customer = editing with
        {
            Name = CustomerName.Text.Trim(), Email = CustomerEmail.Text.Trim(),
            Subject = CustomerSubject.Text.Trim(), Body = CustomerBody.Text,
            SourceDirectory = CustomerDirectory.Text.Trim(), Active = CustomerActive.IsChecked == true,
            ZipPasswordProtected = CustomerPassword.Password.Length == 0 ? editing.ZipPasswordProtected : secrets.Protect(CustomerPassword.Password)
        };
        var dataRoot = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KsgMail"));
        var source = Path.GetFullPath(customer.SourceDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if ((dataRoot + Path.DirectorySeparatorChar).StartsWith(source, StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith(dataRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UserException("Wybierz katalog dokumentów poza katalogiem danych KSG Mail.");
        repository.SaveCustomer(customer);
        InvalidatePreview();
        LoadCustomers();
        CustomersGrid.SelectedItem = allCustomers.FirstOrDefault(item => item.Id == customer.Id);
        ShowCustomer(customer);
        StatusText.Text = "Zapisano klienta.";
    });

    private void DeleteCustomer(object senderObject, RoutedEventArgs args) => Guard(() =>
    {
        var selected = SelectedCustomers();
        if (selected.Count == 0) throw new UserException("Zaznacz co najmniej jednego klienta.");
        var message = selected.Count == 1
            ? $"Usunąć klienta {selected[0].Name}? Historia wysyłek pozostanie."
            : $"Usunąć {selected.Count} zaznaczonych klientów? Historia wysyłek pozostanie.";
        if (MessageBox.Show(this, message, "Usuń klientów",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        foreach (var customer in selected) repository.DeleteCustomer(customer.Id);
        InvalidatePreview();
        LoadCustomers();
        ShowCustomer(new Customer());
        StatusText.Text = $"Usunięto {selected.Count} klientów.";
    });

    private void ActivateSelectedCustomers(object senderObject, RoutedEventArgs args) => Guard(() =>
    {
        var selected = SelectedCustomers();
        if (selected.Count == 0) throw new UserException("Zaznacz co najmniej jednego klienta.");

        int activated = 0, skipped = 0;
        foreach (var customer in selected)
        {
            var active = customer with { Active = true };
            try
            {
                Validation.Customer(active);
                repository.SaveCustomer(active);
                activated++;
            }
            catch (UserException)
            {
                skipped++;
            }
        }

        InvalidatePreview();
        LoadCustomers();
        StatusText.Text = skipped == 0
            ? $"Aktywowano {activated} klientów."
            : $"Aktywowano {activated} klientów. Pominięto {skipped} z niepełną konfiguracją.";
    });

    private void DeactivateSelectedCustomers(object senderObject, RoutedEventArgs args) => Guard(() =>
    {
        var selected = SelectedCustomers();
        if (selected.Count == 0) throw new UserException("Zaznacz co najmniej jednego klienta.");

        foreach (var customer in selected)
            repository.SaveCustomer(customer with { Active = false });

        InvalidatePreview();
        LoadCustomers();
        StatusText.Text = $"Dezaktywowano {selected.Count} klientów.";
    });

    private void LoadSettings()
    {
        settings = repository.Settings();
        SmtpHost.Text = settings.Host;
        SmtpPort.Text = settings.Port.ToString(CultureInfo.InvariantCulture);
        SmtpSecurity.SelectedIndex = settings.Security == TransportSecurity.StartTls ? 0 : 1;
        SmtpUsername.Text = settings.Username;
        SmtpPassword.Clear();
        SmtpPasswordLabel.Text = settings.PasswordProtected.Length == 0 ? "Hasło SMTP" : "Hasło SMTP (zapisane; wpisz, aby zmienić)";
        SenderEmail.Text = settings.SenderEmail;
        SenderName.Text = settings.SenderName;
        AttachmentLimit.Text = settings.MaxAttachmentMb.ToString(CultureInfo.InvariantCulture);
        SourceLimit.Text = settings.MaxSourceMb.ToString(CultureInfo.InvariantCulture);
        RetryCount.Text = settings.RetryCount.ToString(CultureInfo.InvariantCulture);
        ScheduleEnabled.IsChecked = settings.ScheduleEnabled;
        ScheduleDay.Text = settings.ScheduleDay.ToString(CultureInfo.InvariantCulture);
        ScheduleTime.Text = settings.ScheduleTime;
    }

    private static int Number(TextBox field, string name)
    {
        if (!int.TryParse(field.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            throw new UserException($"{name}: podaj liczbę całkowitą.");
        return number;
    }

    private AppSettings ReadSettings()
    {
        var result = settings with
        {
            Host = SmtpHost.Text.Trim(), Port = Number(SmtpPort, "Port"),
            Security = SmtpSecurity.SelectedIndex == 0 ? TransportSecurity.StartTls : TransportSecurity.SslOnConnect,
            Username = SmtpUsername.Text.Trim(),
            PasswordProtected = SmtpPassword.Password.Length == 0 ? settings.PasswordProtected : secrets.Protect(SmtpPassword.Password),
            SenderEmail = SenderEmail.Text.Trim(), SenderName = SenderName.Text.Trim(),
            MaxAttachmentMb = Number(AttachmentLimit, "Limit ZIP"), MaxSourceMb = Number(SourceLimit, "Limit plików"),
            RetryCount = Number(RetryCount, "Liczba prób"), ScheduleEnabled = ScheduleEnabled.IsChecked == true,
            ScheduleDay = Number(ScheduleDay, "Dzień"), ScheduleTime = ScheduleTime.Text.Trim()
        };
        Validation.Settings(result);
        return result;
    }

    private async void SaveSettings(object senderObject, RoutedEventArgs args) => await RunAsync(async cancellationToken =>
    {
        var result = ReadSettings();
        await scheduler.ApplyAsync(result, Path.Combine(AppContext.BaseDirectory, "KsgMail.exe"));
        repository.SaveSettings(result);
        InvalidatePreview();
        LoadSettings();
        StatusText.Text = "Zapisano SMTP, limity i harmonogram.";
    });

    private async void TestConnection(object senderObject, RoutedEventArgs args) => await RunAsync(async cancellationToken =>
    {
        await sender.TestAsync(ReadSettings(), cancellationToken);
        StatusText.Text = "Połączenie TLS i logowanie SMTP poprawne. Nie wysłano wiadomości.";
    });

    private async void PreviewAll(object senderObject, RoutedEventArgs args) =>
        await PrepareAsync(repository.Customers().Where(customer => customer.Active).ToList());

    private async void PreviewSelected(object senderObject, RoutedEventArgs args)
    {
        if (CustomersGrid.SelectedItem is not Customer customer)
        {
            MessageBox.Show(this, "Najpierw wybierz klienta w zakładce Klienci.", "KSG Mail");
            return;
        }
        await PrepareAsync([customer]);
    }

    private async Task PrepareAsync(IReadOnlyList<Customer> customers) => await RunAsync(async cancellationToken =>
    {
        if (!customers.Any(customer => customer.Active)) throw new UserException("Brak aktywnych klientów.");
        InvalidatePreview();
        preparedResend = true;
        prepared = await batches.PrepareAsync(customers, repository.Settings(), Period.Text.Trim(), true,
            new Progress<string>(text => StatusText.Text = text), cancellationToken);
        ApplyPreviewFilter();
        SendButton.IsEnabled = prepared.Items.Any(mail => mail.Ready);
        StatusText.Text = $"Podgląd ręczny gotowy: {prepared.Items.Count(mail => mail.Ready)} do wysłania, " +
            $"{prepared.Items.Count(mail => mail.Error != null && !mail.Skipped)} błędów, {prepared.Items.Count(mail => mail.Skipped)} pominiętych.";
    });

    private void PreviewFilterChanged(object senderObject, RoutedEventArgs args) => ApplyPreviewFilter();

    private void ApplyPreviewFilter()
    {
        if (PreviewGrid == null) return;
        if (prepared == null)
        {
            PreviewGrid.ItemsSource = null;
            if (PreviewCountText != null) PreviewCountText.Text = "Widoczne: 0 / 0";
            return;
        }

        var search = PreviewSearch?.Text?.Trim() ?? "";
        var status = PreviewStatusFilter?.SelectedIndex ?? 0;
        var filtered = prepared.Items.Where(mail =>
            (search.Length == 0 ||
             mail.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
             mail.Email.Contains(search, StringComparison.CurrentCultureIgnoreCase)) &&
            (status == 0 ||
             status == 1 && mail.Ready ||
             status == 2 && mail.Error != null && !mail.Skipped ||
             status == 3 && mail.Skipped))
            .ToList();

        PreviewGrid.ItemsSource = filtered;
        if (PreviewCountText != null)
            PreviewCountText.Text = $"Widoczne: {filtered.Count} / {prepared.Items.Count}";
    }

    private void PreviewSelectedMail(object senderObject, SelectionChangedEventArgs args)
    {
        if (PreviewGrid.SelectedItem is not PreparedMail mail) return;
        PreviewSubject.Text = mail.Subject;
        PreviewBody.Text = mail.Body;
        PreviewFiles.ItemsSource = mail.Files;
    }

    private async void SendPrepared(object senderObject, RoutedEventArgs args)
    {
        if (prepared == null) return;
        var count = prepared.Items.Count(mail => mail.Ready);
        var warning = "\nUWAGA: wysyłka ręczna może ponowić wysyłkę w tym samym okresie; możliwe duplikaty.";
        if (MessageBox.Show(this, $"Wysłać {count} wiadomości z przygotowanego podglądu?{warning}",
            "Potwierdź wysyłkę", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunAsync(async cancellationToken =>
        {
            await batches.SendAsync(prepared, preparedResend,
                new Progress<string>(text => StatusText.Text = text), cancellationToken);
            InvalidatePreview();
            StatusText.Text = "Zakończono wysyłkę. Sprawdź historię i statusy.";
        });
    }

    private void ClearPreview(object senderObject, RoutedEventArgs args) => Guard(InvalidatePreview);

    private void InvalidatePreview()
    {
        prepared?.Dispose();
        prepared = null;
        PreviewGrid.ItemsSource = null;
        if (PreviewCountText != null) PreviewCountText.Text = "Widoczne: 0 / 0";
        PreviewSubject.Text = "";
        PreviewBody.Text = "";
        PreviewFiles.ItemsSource = null;
        SendButton.IsEnabled = false;
    }

    private void LoadLogs()
    {
        var logs = repository.Logs();
        var selectedCompany = LogCustomerFilter?.SelectedItem as string ?? "Wszystkie firmy";
        var companies = logs.Select(log => log.CustomerName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (LogCustomerFilter != null)
        {
            loadingLogFilter = true;
            LogCustomerFilter.ItemsSource = new[] { "Wszystkie firmy" }.Concat(companies).ToList();
            LogCustomerFilter.SelectedItem = companies.Contains(selectedCompany, StringComparer.CurrentCultureIgnoreCase)
                ? companies.First(name => name.Equals(selectedCompany, StringComparison.CurrentCultureIgnoreCase))
                : "Wszystkie firmy";
            loadingLogFilter = false;
            selectedCompany = LogCustomerFilter.SelectedItem as string ?? "Wszystkie firmy";
        }

        var filtered = selectedCompany == "Wszystkie firmy"
            ? logs
            : logs.Where(log => log.CustomerName.Equals(selectedCompany, StringComparison.CurrentCultureIgnoreCase)).ToList();

        LogsGrid.ItemsSource = filtered.Select(log => new
        {
            LocalTime = log.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            log.CustomerName, log.Period, log.Detail,
            Status = log.Status switch
            {
                DeliveryStatus.Success => "Wysłano", DeliveryStatus.Failed => "Błąd",
                DeliveryStatus.Uncertain => "Niepewny", DeliveryStatus.Pending => "W toku", _ => "Pominięty"
            }
        }).ToList();

        if (LogCountText != null)
            LogCountText.Text = $"Widoczne: {filtered.Count} / {logs.Count}";
    }

    private void LogCustomerFilterChanged(object senderObject, SelectionChangedEventArgs args)
    {
        if (!loadingLogFilter) LoadLogs();
    }

    private async void WindowContentRendered(object? senderObject, EventArgs args)
    {
        if (initialUpdateCheckDone) return;
        initialUpdateCheckDone = true;
        try
        {
            await CheckForUpdatesAsync(false, CancellationToken.None);
        }
        catch
        {
            UpdateStatusText.Text = "Nie udało się automatycznie sprawdzić aktualizacji. Możesz użyć przycisku Sprawdź aktualizacje.";
        }
    }

    private async void CheckUpdates(object senderObject, RoutedEventArgs args) =>
        await RunAsync(async cancellationToken => await CheckForUpdatesAsync(true, cancellationToken));

    private async Task CheckForUpdatesAsync(bool showMessages, CancellationToken cancellationToken)
    {
        UpdateStatusText.Text = "Sprawdzanie najnowszej wersji na GitHub…";
        var info = await updater.CheckAsync(cancellationToken);
        LatestVersionText.Text = info.LatestVersion.ToString(3);
        availableUpdate = info.IsUpdateAvailable ? info : null;
        InstallUpdateButton.IsEnabled = info.IsUpdateAvailable;

        if (info.IsUpdateAvailable)
        {
            UpdateStatusText.Text = $"Dostępna jest nowa wersja KSG Mail {info.LatestVersion.ToString(3)}.";
            if (showMessages)
                MessageBox.Show(this, $"Dostępna jest nowa wersja KSG Mail {info.LatestVersion.ToString(3)}.",
                    "Aktualizacja KSG Mail", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            UpdateStatusText.Text = "Masz najnowszą dostępną wersję KSG Mail.";
            if (showMessages)
                MessageBox.Show(this, "Masz najnowszą dostępną wersję KSG Mail.",
                    "Aktualizacja KSG Mail", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void InstallUpdate(object senderObject, RoutedEventArgs args)
    {
        var update = availableUpdate;
        if (update == null)
        {
            await RunAsync(async cancellationToken => await CheckForUpdatesAsync(true, cancellationToken));
            update = availableUpdate;
            if (update == null) return;
        }

        if (MessageBox.Show(this,
                $"Pobrać i zainstalować KSG Mail {update.LatestVersion.ToString(3)}?\n\nPo weryfikacji instalatora aplikacja zostanie zamknięta.",
                "Aktualizacja KSG Mail", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        string? installerPath = null;
        await RunAsync(async cancellationToken =>
        {
            installerPath = await updater.DownloadVerifiedInstallerAsync(update,
                new Progress<string>(text =>
                {
                    StatusText.Text = text;
                    UpdateStatusText.Text = text;
                }), cancellationToken);
            UpdateStatusText.Text = "Instalator zweryfikowany. Uruchamianie aktualizacji…";
        });

        if (installerPath == null) return;
        try
        {
            GitHubUpdateService.StartInstaller(installerPath);
            Application.Current.Shutdown();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void RefreshLogs(object senderObject, RoutedEventArgs args) => Guard(LoadLogs);
    private void CancelOperation(object senderObject, RoutedEventArgs args) => operation?.Cancel();

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (busy) return;
        busy = true;
        Tabs.IsEnabled = false;
        CancelButton.IsEnabled = true;
        operation = new CancellationTokenSource();
        try { await action(operation.Token); }
        catch (Exception exception) { ShowError(exception); }
        finally
        {
            operation.Dispose();
            operation = null;
            busy = false;
            Tabs.IsEnabled = true;
            CancelButton.IsEnabled = false;
            LoadLogs();
        }
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception exception) { ShowError(exception); }
    }

    private void ShowError(Exception exception)
    {
        var message = ErrorMessages.Describe(exception);
        StatusText.Text = message;
        MessageBox.Show(this, message, "KSG Mail", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void ScheduleTick(object? senderObject, EventArgs args)
    {
        if (busy || prepared != null || DateTime.Now < nextScheduleCheck || !repository.Settings().IsDue(DateTime.Now)) return;
        nextScheduleCheck = DateTime.Now.AddHours(1);
        busy = true;
        Tabs.IsEnabled = false;
        CancelButton.IsEnabled = true;
        operation = new CancellationTokenSource();
        try
        {
            StatusText.Text = "Sprawdzanie miesięcznej wysyłki automatycznej…";
            await batches.RunScheduledAsync(DateTime.Now, operation.Token);
            StatusText.Text = "Sprawdzono harmonogram. Wyniki są w historii.";
        }
        catch (Exception exception)
        {
            repository.Log(new Customer { Name = "Harmonogram" }, DateTime.Now.ToString("yyyy-MM"),
                DeliveryStatus.Failed, ErrorMessages.Describe(exception));
            StatusText.Text = ErrorMessages.Describe(exception);
        }
        finally
        {
            operation.Dispose();
            operation = null;
            busy = false;
            Tabs.IsEnabled = true;
            CancelButton.IsEnabled = false;
            LoadLogs();
        }
    }

    private void OnClosing(object? senderObject, CancelEventArgs args)
    {
        if (busy)
        {
            args.Cancel = true;
            operation?.Cancel();
            StatusText.Text = "Anulowanie operacji. Zamknij aplikację po jej zakończeniu.";
            return;
        }
        timer.Stop();
        prepared?.Dispose();
    }
}
