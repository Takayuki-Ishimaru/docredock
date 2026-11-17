using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace DocRedock.Gui.HeadlessTests;

/// <summary>The "閲覧用HTML" output choice: one self-contained .html with embedded images, chosen from the
/// export format radio group and written by <see cref="GuiWorkflowService.ExportAsync"/>.</summary>
public sealed class GuiHtmlExportTests
{
    private static string Fixture() =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "complex-design-doc.pptx");

    [Fact]
    public async Task Html_export_writes_one_self_contained_file_and_numbers_the_next_one()
    {
        var root = Directory.CreateTempSubdirectory("docredock-gui-html-").FullName;
        try
        {
            var source = Path.Combine(root, "complex-design-doc.pptx");
            File.Copy(Fixture(), source);
            var output = Path.Combine(root, "out");
            var service = new GuiWorkflowService();

            var first = await service.ExportAsync(source, output, enableOcr: false, readable: true, useUniqueName: true, html: true, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(Path.Combine(output, "complex-design-doc.html"), first.MarkdownPath);
            Assert.True(File.Exists(first.MarkdownPath));
            Assert.Contains("data:image/png;base64,", File.ReadAllText(first.MarkdownPath), StringComparison.Ordinal);
            Assert.Empty(Directory.GetDirectories(output, "*.assets"));
            Assert.Empty(Directory.GetFiles(output, "*.md"));
            Assert.Equal("閲覧用HTML（片道変換）", first.Fidelity);
            Assert.True(first.IsReadable);
            Assert.NotNull(first.ReviewItems);

            var second = await service.ExportAsync(source, output, enableOcr: false, readable: true, useUniqueName: true, html: true, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(Path.Combine(output, "complex-design-doc (2).html"), second.MarkdownPath);
            Assert.True(File.Exists(second.MarkdownPath));
            Assert.True(File.Exists(first.MarkdownPath));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Html_export_requires_readable_output()
    {
        var root = Directory.CreateTempSubdirectory("docredock-gui-html-").FullName;
        try
        {
            var source = Path.Combine(root, "complex-design-doc.pptx");
            File.Copy(Fixture(), source);
            await Assert.ThrowsAsync<ArgumentException>(() =>
                new GuiWorkflowService().ExportAsync(source, Path.Combine(root, "out"), enableOcr: false, readable: false, html: true, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void Selecting_html_locks_embed_images_on_and_restores_the_previous_value()
    {
        var window = new MainWindow();
        try
        {
            var html = Get<RadioButton>(window, "HtmlExportRadio");
            var readable = Get<RadioButton>(window, "ReadableExportToggle");
            var embed = Get<CheckBox>(window, "EmbedReadableImagesCheckBox");
            Assert.Equal("閲覧用HTML", Avalonia.Automation.AutomationProperties.GetName(html));

            readable.IsChecked = true;
            embed.IsChecked = false;
            html.IsChecked = true;
            Assert.False(embed.IsEnabled);
            Assert.True(embed.IsChecked);
            Assert.True(Get<Control>(window, "ReadableOptionsPanel").IsVisible);
            Assert.False(Get<Control>(window, "AiPackageOptionsPanel").IsVisible);

            readable.IsChecked = true;
            Assert.True(embed.IsEnabled);
            Assert.False(embed.IsChecked);

            embed.IsChecked = true;
            html.IsChecked = true;
            readable.IsChecked = true;
            Assert.True(embed.IsChecked);
            embed.IsChecked = false;
        }
        finally
        {
            Get<RadioButton>(window, "ReadableExportToggle").IsChecked = true;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_html_choice_is_saved_and_restored_with_the_users_own_embed_value()
    {
        var path = Environment.GetEnvironmentVariable("DOCREDOCK_GUI_SETTINGS_PATH")!;
        var window = new MainWindow();
        try
        {
            var html = Get<RadioButton>(window, "HtmlExportRadio");
            var readable = Get<RadioButton>(window, "ReadableExportToggle");
            var embed = Get<CheckBox>(window, "EmbedReadableImagesCheckBox");
            readable.IsChecked = true;
            embed.IsChecked = false;
            html.IsChecked = true;
            var saved = File.ReadAllText(path);
            Assert.Contains("\"Html\":true", saved, StringComparison.Ordinal);
            Assert.Contains("\"EmbedReadableImages\":false", saved, StringComparison.Ordinal);

            // Reload the same control tree (headless windows share one radio group scope).
            var initialized = typeof(MainWindow).GetField("_componentsInitialized", BindingFlags.NonPublic | BindingFlags.Instance)!;
            initialized.SetValue(window, false);
            try
            {
                readable.IsChecked = true;
                typeof(MainWindow).GetMethod("LoadSettings", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                Assert.True(html.IsChecked);
                Assert.False(embed.IsEnabled);
                Assert.True(embed.IsChecked);
            }
            finally { initialized.SetValue(window, true); }

            readable.IsChecked = true;
            Assert.False(embed.IsChecked);
        }
        finally
        {
            Get<RadioButton>(window, "ReadableExportToggle").IsChecked = true;
            window.Close();
            File.Delete(path);
        }
    }

    private static T Get<T>(MainWindow window, string name) where T : Control =>
        window.FindControl<T>(name) ?? throw new InvalidOperationException(name);
}
