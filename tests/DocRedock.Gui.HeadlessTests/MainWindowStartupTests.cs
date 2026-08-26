using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using DocRedock.Gui;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(DocRedock.Gui.HeadlessTests.GuiTestAppBuilder))]

namespace DocRedock.Gui.HeadlessTests;

public static class GuiTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class MainWindowStartupTests
{
    [AvaloniaFact]
    public void Main_window_constructs_without_xaml_initialization_crash()
    {
        var window = new MainWindow();
        try
        {
            Assert.NotNull(window);
        }
        finally
        {
            window.Close();
        }
    }
}
