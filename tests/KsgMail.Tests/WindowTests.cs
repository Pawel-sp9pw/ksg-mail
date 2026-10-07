using System.Windows;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KsgMail.Core;
using Xunit;

namespace KsgMail.Tests;

public sealed class WindowTests
{
    [Fact]
    public void MainWindowLoadsAndRendersAllFourTabs()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var workspace = new TestWorkspace();
                var secrets = workspace.Secrets;
                var application = new KsgMail.App.App();
                application.InitializeComponent();
                var sender = new SmtpSender(secrets);
                var batches = new BatchService(workspace.Repository, new ArchiveService(secrets), sender, workspace.Temp);
                var window = new KsgMail.App.MainWindow(workspace.Repository, secrets, sender, batches);
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(1136, 776));
                content.Arrange(new Rect(0, 0, 1136, 776));
                content.UpdateLayout();
                var bitmap = new RenderTargetBitmap(1180, 820, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var tabs = (System.Windows.Controls.TabControl)window.FindName("Tabs");
                Assert.Equal(4, tabs.Items.Count);
                var screenshotDirectory = Environment.GetEnvironmentVariable("KSG_MAIL_SCREENSHOTS");
                foreach (var index in Enumerable.Range(0, tabs.Items.Count))
                {
                    tabs.SelectedIndex = index;
                    content.UpdateLayout();
                    var tabBitmap = new RenderTargetBitmap(1180, 820, 96, 96, PixelFormats.Pbgra32);
                    tabBitmap.Render(content);
                    if (!string.IsNullOrEmpty(screenshotDirectory))
                    {
                        Directory.CreateDirectory(screenshotDirectory);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(tabBitmap));
                        using var output = File.Create(Path.Combine(screenshotDirectory, $"tab-{index}.png"));
                        encoder.Save(output);
                    }
                }
                Assert.Equal("KSG Mail — miesięczna wysyłka", window.Title);
                window.Close();
            }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        Assert.Null(error);
    }
}
