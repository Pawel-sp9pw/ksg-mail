using System.Diagnostics;
using System.Security.Principal;
using System.Xml.Linq;

namespace KsgMail.Core;

public sealed class WindowsScheduler
{
    public string TaskName => "KSG-Mail-" + WindowsIdentity.GetCurrent().User!.Value;

    public async Task ApplyAsync(AppSettings settings, string executable)
    {
        if (!settings.ScheduleEnabled)
        {
            await RunAsync(["/Delete", "/TN", TaskName, "/F"], false);
            return;
        }
        if (!File.Exists(executable)) throw new UserException("Nie znaleziono aplikacji do harmonogramu. Zainstaluj wersję release.");
        XNamespace taskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var document = new XDocument(new XDeclaration("1.0", "utf-16", null),
            new XElement(taskNamespace + "Task", new XAttribute("version", "1.2"),
                new XElement(taskNamespace + "Triggers",
                    new XElement(taskNamespace + "CalendarTrigger",
                        new XElement(taskNamespace + "StartBoundary", "2020-01-01T" + settings.ScheduleTime + ":00"),
                        new XElement(taskNamespace + "Enabled", true),
                        new XElement(taskNamespace + "ScheduleByDay", new XElement(taskNamespace + "DaysInterval", 1))),
                    new XElement(taskNamespace + "LogonTrigger",
                        new XElement(taskNamespace + "Enabled", true),
                        new XElement(taskNamespace + "UserId", WindowsIdentity.GetCurrent().User!.Value))),
                new XElement(taskNamespace + "Principals",
                    new XElement(taskNamespace + "Principal", new XAttribute("id", "Author"),
                        new XElement(taskNamespace + "UserId", WindowsIdentity.GetCurrent().User!.Value),
                        new XElement(taskNamespace + "LogonType", "InteractiveToken"),
                        new XElement(taskNamespace + "RunLevel", "LeastPrivilege"))),
                new XElement(taskNamespace + "Settings",
                    new XElement(taskNamespace + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(taskNamespace + "DisallowStartIfOnBatteries", false),
                    new XElement(taskNamespace + "StopIfGoingOnBatteries", false),
                    new XElement(taskNamespace + "StartWhenAvailable", true),
                    new XElement(taskNamespace + "ExecutionTimeLimit", "PT0S"),
                    new XElement(taskNamespace + "Enabled", true)),
                new XElement(taskNamespace + "Actions", new XAttribute("Context", "Author"),
                    new XElement(taskNamespace + "Exec",
                        new XElement(taskNamespace + "Command", executable),
                        new XElement(taskNamespace + "Arguments", "--scheduled"),
                        new XElement(taskNamespace + "WorkingDirectory", Path.GetDirectoryName(executable))))));
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            document.Save(path);
            await RunAsync(["/Create", "/TN", TaskName, "/XML", path, "/F"], true);
        }
        finally { File.Delete(path); }
    }

    private static async Task RunAsync(IEnumerable<string> arguments, bool requireSuccess)
    {
        var start = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await output;
        await error;
        if (requireSuccess && process.ExitCode != 0)
            throw new UserException("Nie udało się zapisać zadania Windows. Sprawdź uprawnienia Harmonogramu zadań.");
    }
}
