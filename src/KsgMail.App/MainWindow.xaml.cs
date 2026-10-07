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
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private Customer editing = new();
    private AppSettings settings = new();
    private PreparedBatch? prepared;
    private bool preparedResend;
    private CancellationTokenSource? operation;
    private bool busy;
    private DateTime nextScheduleCheck = DateTime.MinValue;

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
        timer.Tick += ScheduleTick;
        timer.Start();
    }

    private void LoadCustomers() => CustomersGrid.ItemsSource = repository.Customers();

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
        if (CustomersGrid.SelectedItem is Customer customer) ShowCustomer(customer);
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
        CustomersGrid.SelectedItem = repository.Customers().First(item => item.Id == customer.Id);
        ShowCustomer(customer);
        StatusText.Text = "Zapisano klienta.";
    });

    private void DeleteCustomer(object senderObject, RoutedEventArgs args) => Guard(() =>
    {
        if (CustomersGrid.SelectedItem is not Customer customer) throw new UserException("Wybierz klienta.");
        if (MessageBox.Show(this, $"Usunąć klienta {customer.Name}? Historia wysyłek pozostanie.", "Usuń klienta",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        repository.DeleteCustomer(customer.Id);
        InvalidatePreview();
        LoadCustomers();
        ShowCustomer(new Customer());
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
        preparedResend = AllowResend.IsChecked == true;
        prepared = await batches.PrepareAsync(customers, repository.Settings(), Period.Text.Trim(), preparedResend,
            new Progress<string>(text => StatusText.Text = text), cancellationToken);
        PreviewGrid.ItemsSource = prepared.Items;
        SendButton.IsEnabled = prepared.Items.Any(mail => mail.Ready);
        StatusText.Text = $"Podgląd gotowy: {prepared.Items.Count(mail => mail.Ready)} do wysłania, " +
            $"{prepared.Items.Count(mail => mail.Error != null)} błędów, {prepared.Items.Count(mail => mail.Skipped)} pominiętych.";
    });

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
        var warning = preparedResend ? "\nUWAGA: zezwolono na powtórną wysyłkę; możliwe duplikaty." : "";
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
        PreviewSubject.Text = "";
        PreviewBody.Text = "";
        PreviewFiles.ItemsSource = null;
        SendButton.IsEnabled = false;
    }

    private void LoadLogs() => LogsGrid.ItemsSource = repository.Logs().Select(log => new
    {
        LocalTime = log.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
        log.CustomerName, log.Period, log.Detail,
        Status = log.Status switch
        {
            DeliveryStatus.Success => "Wysłano", DeliveryStatus.Failed => "Błąd",
            DeliveryStatus.Uncertain => "Niepewny", DeliveryStatus.Pending => "W toku", _ => "Pominięty"
        }
    }).ToList();

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
