using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using DocRedock.Api;
using DocRedock.Gui;
using Xunit;

namespace DocRedock.Gui.HeadlessTests;

[CollectionDefinition("Package browser UI", DisableParallelization = true)]
public sealed class PackageBrowserTestCollection { }

// The MainWindow test temporarily changes the test host's shared settings file. It must
// finish restoring that file before another window's settings test starts.
[Collection("Package browser UI")]
public sealed class PackageBrowserTests
{
    private static void SaveFrame(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("DOCREDOCK_GUI_CAPTURE_DIRECTORY") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(directory, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static string Fixture(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "DocRedock.sln")))
                return Path.Combine(directory.FullName, "tests", "DocRedock.Tests", "Fixtures", "V033Review", name);
        throw new InvalidOperationException("Repository not found.");
    }

    [AvaloniaFact]
    public async Task Browser_verifies_searches_and_keeps_sources_out_of_the_copied_body()
    {
        var root = Directory.CreateTempSubdirectory("docredock-browser-ui-").FullName;
        var service = new DocumentService(null, null, discoverPdfRasterizer: false);
        var window = new PackageBrowserWindow();
        try
        {
            var result = await service.ExportAiPackageAsync(new(new(Fixture("sales.xlsx"), "unused.md"), Path.Combine(root, "package.zip"), AiPackageForm.Zip, 1500, true));
            window.Show();
            await window.LoadPackageAsync(result.OutputPath);
            Assert.True(window.CanRead);
            window.Search("ＳＴＯＲＥ＿０２５０");
            Assert.Single(window.VisiblePartIds);
            Assert.Contains("STORE_0250", window.CurrentMarkdown.Replace("\\", ""));
            Assert.Contains("A255", window.CurrentSources);
            SaveFrame(window, "package-browser.png");
            Assert.DoesNotContain("source_id", window.CurrentMarkdown.Replace("\\", ""));
            window.Search("no-such-value"); Assert.Empty(window.VisiblePartIds); Assert.Empty(window.CurrentMarkdown.Replace("\\", ""));
        }
        finally { window.Close(); Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task Browser_blocks_revision_body_until_review_and_clears_previous_content_on_a_failed_load()
    {
        var root = Directory.CreateTempSubdirectory("docredock-browser-review-").FullName;
        var service = new DocumentService(null, null, discoverPdfRasterizer: false);
        var window = new PackageBrowserWindow();
        try
        {
            var result = await service.ExportAiPackageAsync(new(new(Fixture("spec.docx"), "unused.md"), Path.Combine(root, "package")));
            window.Show(); await window.LoadPackageAsync(result.OutputPath);
            Assert.False(window.CanRead); Assert.Empty(window.CurrentMarkdown.Replace("\\", ""));
            Assert.Contains("確認が必要", window.CurrentStatus);
            SaveFrame(window, "package-review-gate.png");
            window.Search("SECRET"); Assert.Empty(window.CurrentMarkdown.Replace("\\", ""));
            window.ConfirmReview(); Assert.True(window.CanRead); Assert.Contains("DELETED_REVIEW_SECRET", window.CurrentMarkdown.Replace("\\", ""));
            Assert.Contains("確認済み", window.CurrentStatus); Assert.DoesNotContain("確認前", window.CurrentStatus);
            SaveFrame(window, "package-review-confirmed.png");
            window.Search("SECRET"); Assert.Contains("確認済み", window.CurrentStatus);
            window.ClearReviewConfirmation(); Assert.False(window.CanRead); Assert.Empty(window.CurrentMarkdown);
            Assert.Contains("確認が必要", window.CurrentStatus);
            window.ConfirmReview();
            File.AppendAllText(Path.Combine(result.OutputPath, result.Parts[0].Path), "TAMPERED");
            await Assert.ThrowsAsync<AiPackageVerificationException>(() => window.LoadPackageAsync(result.OutputPath));
            Assert.False(window.CanRead); Assert.Empty(window.CurrentMarkdown.Replace("\\", "")); Assert.Empty(window.CurrentSources);
        }
        finally { window.Close(); Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task Newer_package_load_wins_and_resets_the_previous_review_confirmation()
    {
        var root = Directory.CreateTempSubdirectory("docredock-browser-race-").FullName;
        var service = new DocumentService(null, null, discoverPdfRasterizer: false);
        var window = new PackageBrowserWindow();
        try
        {
            var plain = await service.ExportAiPackageAsync(new(new(Fixture("sales.xlsx"), "unused.md"), Path.Combine(root, "plain")));
            var revision = await service.ExportAiPackageAsync(new(new(Fixture("spec.docx"), "unused.md"), Path.Combine(root, "revision")));
            window.Show();
            var older = window.LoadPackageAsync(plain.OutputPath);
            await window.LoadPackageAsync(revision.OutputPath);
            try { await older; } catch (OperationCanceledException) { }
            Assert.False(window.CanRead); Assert.Empty(window.CurrentMarkdown); Assert.Empty(window.CurrentSources);
            window.ConfirmReview(); Assert.True(window.CanRead);
            window.Search("SECRET"); Assert.Equal("SECRET", window.CurrentQuery);
            await window.LoadPackageAsync(revision.OutputPath);
            Assert.False(window.CanRead); Assert.Empty(window.CurrentMarkdown);
            Assert.Empty(window.CurrentQuery); Assert.Contains("確認が必要", window.CurrentStatus);
        }
        finally { window.Close(); Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task Hidden_content_review_can_be_required_and_is_reset_when_policy_or_package_changes()
    {
        var root = Directory.CreateTempSubdirectory("docredock-browser-hidden-").FullName;
        var service = new DocumentService(null, null, discoverPdfRasterizer: false);
        var window = new PackageBrowserWindow();
        try
        {
            var complete = await service.ExportAiPackageAsync(new(new(Fixture("sales.xlsx"), "unused.md", ContentPolicy: "complete"), Path.Combine(root, "complete.zip"), AiPackageForm.Zip));
            window.Show(); await window.LoadPackageAsync(complete.OutputPath);
            Assert.True(window.CanRead);
            window.RequireHiddenReview = true;
            Assert.False(window.CanRead); Assert.Empty(window.CurrentMarkdown); Assert.Empty(window.CurrentSources);
            Assert.Contains("確認が必要", window.CurrentStatus);
            window.ConfirmReview(); Assert.True(window.CanRead); Assert.Contains("確認済み", window.CurrentStatus);
            SaveFrame(window, "package-hidden-confirmed.png");
            window.Search("INTERNAL_ONLY"); Assert.NotEmpty(window.CurrentMarkdown);
            await window.LoadPackageAsync(complete.OutputPath);
            Assert.True(window.RequireHiddenReview); Assert.False(window.CanRead); Assert.Empty(window.CurrentQuery);
            window.RequireHiddenReview = false; Assert.True(window.CanRead);
            window.RequireHiddenReview = true; Assert.False(window.CanRead);
        }
        finally { window.Close(); Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task Search_filter_survives_review_confirmation_and_policy_changes()
    {
        var root = Directory.CreateTempSubdirectory("docredock-browser-filter-").FullName;
        var service = new DocumentService(null, null, discoverPdfRasterizer: false);
        var window = new PackageBrowserWindow();
        try
        {
            var result = await service.ExportAiPackageAsync(new(new(Fixture("sales.xlsx"), "unused.md", ContentPolicy: "complete"),
                Path.Combine(root, "package"), TargetCharacters: 1500, TableRowBlocks: true));
            window.Show(); await window.LoadPackageAsync(result.OutputPath);
            window.RequireHiddenReview = true;
            window.Search("ＳＴＯＲＥ＿０２５０");
            Assert.False(window.CanRead); Assert.Empty(window.VisiblePartIds);
            window.ConfirmReview();
            Assert.Single(window.VisiblePartIds);
            Assert.Contains("STORE_0250", window.CurrentMarkdown.Replace("\\", ""));
            Assert.Equal("ＳＴＯＲＥ＿０２５０", window.CurrentQuery);
            Assert.Contains("1分割が一致", window.CurrentStatus);
            window.ClearReviewConfirmation();
            Assert.Empty(window.VisiblePartIds); Assert.Empty(window.CurrentMarkdown);
            window.ConfirmReview(); Assert.Single(window.VisiblePartIds);
            window.RequireHiddenReview = false;
            Assert.Single(window.VisiblePartIds);
            Assert.Contains("1分割が一致", window.CurrentStatus);
        }
        finally { window.Close(); Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task Clearing_search_and_showing_all_parts_clear_the_query_and_match_status()
    {
        var root = Directory.CreateTempSubdirectory("docredock-browser-clear-search-").FullName;
        var service = new DocumentService(null, null, discoverPdfRasterizer: false);
        var window = new PackageBrowserWindow();
        try
        {
            var result = await service.ExportAiPackageAsync(new(new(Fixture("sales.xlsx"), "unused.md"),
                Path.Combine(root, "package"), TargetCharacters: 1500, TableRowBlocks: true));
            window.Show(); await window.LoadPackageAsync(result.OutputPath);
            window.Search("no-such-value");
            Assert.Empty(window.VisiblePartIds); Assert.Contains("0分割が一致", window.CurrentStatus);
            window.Search("");
            Assert.Equal(result.Parts.Count, window.VisiblePartIds.Count);
            Assert.DoesNotContain("分割が一致", window.CurrentStatus);
            window.Search("ＳＴＯＲＥ＿０２５０"); Assert.Single(window.VisiblePartIds);
            var showAll = window.GetLogicalDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, "すべての分割"));
            showAll.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(window.CurrentQuery);
            Assert.Equal(result.Parts.Count, window.VisiblePartIds.Count);
            Assert.DoesNotContain("分割が一致", window.CurrentStatus);
        }
        finally { window.Close(); Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task Latest_load_wins_while_an_older_cancellation_callback_is_still_running()
    {
        var root = Directory.CreateTempSubdirectory("docredock-browser-cancellation-").FullName;
        var service = new DocumentService(null, null, discoverPdfRasterizer: false);
        var window = new PackageBrowserWindow();
        using var previous = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = previous.Token.Register(() =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Cancellation callback was not released.");
        });
        Task? superseded = null;
        try
        {
            var revision = await service.ExportAiPackageAsync(new(new(Fixture("spec.docx"), "unused.md"), Path.Combine(root, "revision"))).ConfigureAwait(true);
            window.Show();
            typeof(PackageBrowserWindow).GetField("loading", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(window, previous);
            superseded = window.LoadPackageAsync(Path.Combine(root, "missing.zip"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            await window.LoadPackageAsync(revision.OutputPath).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            Assert.False(window.CanRead);
            Assert.Empty(window.CurrentMarkdown);
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => superseded).ConfigureAwait(true);
            window.ConfirmReview();
            Assert.True(window.CanRead);
            Assert.Contains("DELETED_REVIEW_SECRET", window.CurrentMarkdown.Replace("\\", "", StringComparison.Ordinal), StringComparison.Ordinal);
        }
        finally
        {
            release.Set();
            if (superseded is not null)
                try { await superseded.ConfigureAwait(true); } catch (OperationCanceledException) { }
            window.Close();
            Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public async Task Main_window_exposes_row_splitting_before_advanced_options_and_does_not_persist_revision_consent()
    {
        var settingsPath = Environment.GetEnvironmentVariable("DOCREDOCK_GUI_SETTINGS_PATH")!;
        var previousSettings = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
        var window = new MainWindow();
        try
        {
            var package = window.FindControl<RadioButton>("AiPackageExportRadio")!;
            package.IsChecked = true;
            var options = window.FindControl<StackPanel>("AiPackageOptionsPanel")!;
            Assert.True(options.IsVisible);
            window.Show();
            var target = window.FindControl<NumericUpDown>("PackageTargetCharacters")!;
            var rowBlocks = window.FindControl<CheckBox>("TableRowBlocksCheckBox")!;
            var preview = window.FindControl<TextBlock>("PackagePreviewText")!;
            target.Value = 1500; rowBlocks.IsChecked = false;
            var file = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(Fixture("sales.xlsx")));
            Assert.NotNull(file);
            window.FilePickerOverride = _ => Task.FromResult<IReadOnlyList<Avalonia.Platform.Storage.IStorageFile>>([file!]);
            window.FindControl<Button>("ExportDropZone")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await WaitForPreview("予想: 1分割");
            rowBlocks.IsChecked = true;
            await WaitForPreview("予想: 12分割");
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var previewBottom = preview.TranslatePoint(new Point(0, preview.Bounds.Height), window)!.Value.Y;
            var actionTop = window.FindControl<Border>("ExportActionBar")!.TranslatePoint(default, window)!.Value.Y;
            Assert.True(previewBottom < actionTop, "The split estimate must be visible above the action bar at the default window size.");
            SaveFrame(window, "package-options.png");
            Assert.NotNull(window.FindControl<Button>("ReadPackageButton"));
            for (var parent = options.Parent; parent is not null; parent = parent.Parent)
                Assert.IsNotType<Expander>(parent);
            Assert.False(window.FindControl<CheckBox>("RevisionSharingConsent")!.IsChecked);
            window.FindControl<CheckBox>("RevisionSharingConsent")!.IsChecked = true;
            var next = new MainWindow();
            try { Assert.False(next.FindControl<CheckBox>("RevisionSharingConsent")!.IsChecked); }
            finally { next.Close(); }

            async Task WaitForPreview(string expected)
            {
                var limit = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < limit && preview.Text?.Contains(expected, StringComparison.Ordinal) != true) await Task.Delay(20);
                Assert.Contains(expected, preview.Text);
            }
        }
        finally
        {
            window.Close();
            if (previousSettings is null) File.Delete(settingsPath);
            else File.WriteAllText(settingsPath, previousSettings);
        }
    }
}
