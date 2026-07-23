using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using ScreenTranslator.App.Core;
using ScreenTranslator.App.Interop;
using ScreenTranslator.App.Services;
using ScreenTranslator.App.Windows;
using WpfResizeMode = System.Windows.ResizeMode;

namespace ScreenTranslator.Tests;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    [STAThread]
    private static int Main(string[] args)
    {
        Run("OCR layout preserves indentation and blank lines", TestOcrLayoutFormatting);
        Run("Application icon assets are valid", TestApplicationIconAssets);
        Run("Unchanged OCR text is not translated twice", TestRecognizedTextChangeTracking);
        Run("OCR churn keeps translation work progressing", TestLatestTranslationCoordination);
        Run("Global F1 and F2 hotkeys map to translation commands", TestGlobalHotkeyMapping);
        Run("Main controls do not stay topmost", TestMainWindowLayering);
        Run("Windows use sharp DPI-aware rendering without visible scrollbars", TestSharpRenderingSettings);
        Run("Region selector covers the virtual desktop", TestRegionSelectorWindow);
        Run("Selected region indicator is transparent and subtle", TestRegionIndicatorWindow);
        Run("Side panel supports locking, resizing and opacity", TestSidePanelControls);
        Run("Overlay preserves translated text and region", TestOverlayWindow);
        Run("Integrated Qwen translation preserves layout and caches lines", () => TestQwenLocalTranslationAsync().GetAwaiter().GetResult());
        Run("Integrated model download is verified and reused", () => TestLocalModelStoreAsync().GetAwaiter().GetResult());
        Run("LibreTranslate adapter preserves layout and batches local requests", () => TestLibreTranslateAdapterAsync().GetAwaiter().GetResult());
        Run("LibreTranslate readiness validates service and target models", () => TestLibreTranslateReadinessAsync().GetAwaiter().GetResult());
        Run("DeepSeek adapter preserves formatting", () => TestDeepSeekAdapterAsync().GetAwaiter().GetResult());
        Run("Cloud session cache reuses earlier translations", () => TestCloudSessionCacheAsync().GetAwaiter().GetResult());
        Run("Untranslated output retries once", () => TestUntranslatedOutputRetryAsync().GetAwaiter().GetResult());
        Run("A worse retry cannot replace the original response", () => TestWorseRetryIsRejectedAsync().GetAwaiter().GetResult());
        Run("Mixed target-language output is not retried", () => TestMixedTranslationIsAcceptedAsync().GetAwaiter().GetResult());
        Run("Generic OpenAI adapter omits DeepSeek-only fields", () => TestGenericAdapterAsync().GetAwaiter().GetResult());
        Run("Windows OCR recognizes a synthetic image", () => TestWindowsOcrAsync().GetAwaiter().GetResult());

        if (args.Contains("--screen-capture", StringComparer.OrdinalIgnoreCase))
        {
            Run("Screen capture and OCR work end to end", () => TestScreenCaptureAndOcrAsync().GetAwaiter().GetResult());
        }

        if (args.Contains("--render-ui", StringComparer.OrdinalIgnoreCase))
        {
            Run("Main window renders to a visual preview", TestRenderMainWindowPreview);
        }

        if (args.Contains("--local-model", StringComparer.OrdinalIgnoreCase))
        {
            Run("Official Qwen model translates locally end to end", () => TestIntegratedLocalModelAsync().GetAwaiter().GetResult());
        }

        Console.WriteLine($"RESULT passed={_passed} failed={_failed}");
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            _failed++;
            Console.WriteLine($"FAIL {name}");
            Console.WriteLine(exception);
        }
    }

    private static void TestOcrLayoutFormatting()
    {
        IReadOnlyList<RecognizedWordLayout>[] lines =
        [
            [new("TITLE", 10, 0, 50, 20)],
            [new("item", 30, 20, 40, 20)],
            [new("A", 10, 80, 10, 20), new("B", 30, 80, 10, 20)]
        ];

        var formatted = OcrLayoutFormatter.Format(lines);
        AssertEqual("TITLE\n  item\n\nA B", formatted.Replace("\r\n", "\n"));
    }

    private static void TestApplicationIconAssets()
    {
        var assetsDirectory = Path.Combine(
            Environment.CurrentDirectory,
            "src",
            "ScreenTranslator.App",
            "Assets");
        var pngPath = Path.Combine(assetsDirectory, "app-icon.png");
        var icoPath = Path.Combine(assetsDirectory, "app-icon.ico");

        Assert(File.Exists(pngPath), "The transparent application icon PNG must exist.");
        Assert(File.Exists(icoPath), "The Windows multi-size ICO must exist.");

        using var bitmap = new System.Drawing.Bitmap(pngPath);
        AssertEqual(512, bitmap.Width);
        AssertEqual(512, bitmap.Height);
        Assert(bitmap.GetPixel(0, 0).A == 0,
            "The application icon must retain transparent outer corners.");

        using var smallIcon = new System.Drawing.Icon(icoPath, 16, 16);
        Assert(smallIcon.Width == 16 && smallIcon.Height == 16,
            "The ICO must provide a taskbar-size representation.");

        var iconBytes = File.ReadAllBytes(icoPath);
        var imageCount = BitConverter.ToUInt16(iconBytes, 4);
        var iconSizes = Enumerable.Range(0, imageCount)
            .Select(index =>
            {
                var entryOffset = 6 + index * 16;
                var width = iconBytes[entryOffset] == 0 ? 256 : iconBytes[entryOffset];
                var height = iconBytes[entryOffset + 1] == 0 ? 256 : iconBytes[entryOffset + 1];
                return (Width: width, Height: height);
            })
            .ToArray();
        Assert(iconSizes.Contains((16, 16)) && iconSizes.Contains((256, 256)),
            "The ICO directory must contain both taskbar and high-resolution frames.");
    }

    private static void TestRecognizedTextChangeTracking()
    {
        var tracker = new RecognizedTextChangeTracker();
        Assert(!tracker.ShouldTranslate("   "), "Whitespace-only OCR output must not be translated.");
        Assert(tracker.ShouldTranslate("first"), "New OCR text must be translated.");
        Assert(tracker.ShouldTranslate("first"), "A failed translation attempt must remain retryable.");

        tracker.MarkTranslated("first");
        Assert(!tracker.ShouldTranslate("first"), "Successfully translated text must be deduplicated.");
        Assert(!tracker.ShouldTranslate("  FIRST\r\n"),
            "Whitespace, line-ending and casing noise must not retrigger translation.");

        Assert(tracker.ShouldTranslate("second"),
            "A genuinely changed OCR fingerprint must translate on its first observation.");
        Assert(tracker.ShouldTranslate("second"),
            "A failed translation must remain retryable.");

        tracker.MarkTranslated("second");
        Assert(!tracker.ShouldTranslate("SECOND"),
            "The new successful translation must become the deduplication baseline.");
        Assert(tracker.ShouldTranslate("third"),
            "Rapidly changing live text must not wait for a duplicate scan before translating.");

        tracker.MarkTranslated("Ｈｅｌｌｏ　ｗｏｒｌｄ");
        Assert(!tracker.ShouldTranslate("hello world"),
            "Unicode width variants from OCR must share the same fingerprint.");

        tracker.Reset();
        Assert(tracker.ShouldTranslate("first"), "Starting a new run must reset deduplication state.");
    }

    private static void TestLatestTranslationCoordination()
    {
        using var coordinator = new LatestTranslationCoordinator();

        Assert(coordinator.TryObserve("first", "zh-CN", out var chineseKey),
            "The first recognized translation state must be queued.");
        Assert(!coordinator.TryObserve("  FIRST\r\n", "zh-CN", out _),
            "Equivalent OCR noise must not queue duplicate work.");

        var chineseOperation = coordinator.TryBegin(chineseKey, CancellationToken.None)
            ?? throw new InvalidOperationException("The latest queued work must be allowed to start.");
        Assert(coordinator.TryObserve("second", "zh-CN", out _),
            "Changed source text must replace the pending translation state.");
        Assert(!chineseOperation.IsCancellationRequested,
            "OCR source changes must not repeatedly cancel a slow in-flight translation.");
        Assert(coordinator.CanPublish(chineseKey),
            "A completed request may publish while newer source text waits for the same target language.");

        Assert(coordinator.TryObserve("second", "en", out var englishKey),
            "Changing only the target language must queue a new translation.");
        Assert(chineseOperation.IsCancellationRequested,
            "Changing the target language must cancel the now-unusable in-flight request.");
        Assert(!coordinator.CanPublish(chineseKey) && coordinator.CanPublish(englishKey),
            "Only results for the current target language may update the output.");
        coordinator.End(chineseOperation);

        var englishOperation = coordinator.TryBegin(englishKey, CancellationToken.None)
            ?? throw new InvalidOperationException("The replacement request must be allowed to start.");
        coordinator.End(englishOperation);

        coordinator.AllowRetry(englishKey);
        Assert(coordinator.TryObserve("first", "en", out _),
            "A failed latest request must remain retryable on the next scan.");
    }

    private static void TestGlobalHotkeyMapping()
    {
        Assert(GlobalHotkeyManager.TryGetCommand(
                GlobalHotkeyManager.ReselectRegionId,
                out var reselectCommand),
            "F1 hotkey id must resolve to a command.");
        AssertEqual(GlobalHotkeyCommand.ReselectRegion, reselectCommand);

        Assert(GlobalHotkeyManager.TryGetCommand(
                GlobalHotkeyManager.StopTranslationId,
                out var stopCommand),
            "F2 hotkey id must resolve to a command.");
        AssertEqual(GlobalHotkeyCommand.StopTranslation, stopCommand);
        Assert(!GlobalHotkeyManager.TryGetCommand(-1, out _),
            "Unknown hotkey ids must be ignored.");
    }

    private static void TestSidePanelControls()
    {
        var panel = new TranslationPanelWindow();
        Assert(panel.Topmost, "Side panel must stay topmost.");
        Assert(!panel.AllowsTransparency,
            "The side panel must use a native window surface so translated text can use ClearType.");
        AssertEqual(WpfResizeMode.CanResizeWithGrip, panel.ResizeMode);
        var panelSurface = (Border)panel.FindName("PanelSurface");
        Assert(panelSurface.CornerRadius.TopLeft >= 10,
            "The redesigned side panel must use a clean rounded surface.");
        Assert(Math.Abs(panel.Opacity - 1) < 0.001,
            "The side panel must start fully opaque for maximum text contrast.");

        var controlDock = (Border)panel.FindName("ControlDock");
        Assert(controlDock.Opacity < 0.25,
            "Side-panel controls must stay visually unobtrusive until the user points at them.");

        var opacityToggle = (System.Windows.Controls.Primitives.ToggleButton)panel.FindName("OpacityToggle");
        var opacityPopup = (System.Windows.Controls.Primitives.Popup)panel.FindName("OpacityPopup");
        panel.Show();

        try
        {
            opacityToggle.IsChecked = true;
            panel.Dispatcher.Invoke(
                () => { },
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert(opacityPopup.IsOpen, "Opacity controls must open only when requested.");
            opacityToggle.IsChecked = false;
        }
        finally
        {
            opacityPopup.IsOpen = false;
            panel.Hide();
        }

        var lockToggle = (System.Windows.Controls.Primitives.ToggleButton)panel.FindName("LockToggle");
        lockToggle.IsChecked = true;
        AssertEqual(WpfResizeMode.NoResize, panel.ResizeMode);
        AssertEqual("已锁定", lockToggle.Content?.ToString());

        lockToggle.IsChecked = false;
        AssertEqual(WpfResizeMode.CanResizeWithGrip, panel.ResizeMode);

        var opacitySlider = (Slider)panel.FindName("OpacitySlider");
        opacitySlider.Value = 0.55;
        Assert(Math.Abs(panel.Opacity - 0.55) < 0.001, "Panel opacity did not follow its slider.");

        const string translated = "  第一行\n\n第二行  ";
        panel.SetTranslation(translated);
        var textBlock = (TextBlock)panel.FindName("TranslationText");
        AssertEqual(translated, textBlock.Text);
        panel.SetTypography("Arial", 22);
        AssertEqual("Arial", textBlock.FontFamily.Source);
        Assert(Math.Abs(textBlock.FontSize - 22) < 0.001, "Panel font size was not applied.");
        panel.AllowClose();
    }

    private static void TestMainWindowLayering()
    {
        if (System.Windows.Application.Current is null)
        {
            var application = new ScreenTranslator.App.App();
            application.InitializeComponent();
        }

        var mainWindow = new ScreenTranslator.App.MainWindow();
        Assert(!mainWindow.Topmost, "The main control window must not cover other applications permanently.");
        Assert(mainWindow.Icon is not null, "The main window must display the application icon.");
        var brandIcon = (System.Windows.Controls.Image)mainWindow.FindName("BrandIcon");
        var statusCard = (Border)mainWindow.FindName("StatusCard");
        var actionFooter = (Border)mainWindow.FindName("ActionFooter");
        Assert(brandIcon.Source is not null, "The redesigned header must show the brand icon.");
        Assert(statusCard.CornerRadius.TopLeft >= 10,
            "The redesigned status surface must use restrained rounded corners.");
        AssertEqual(2, Grid.GetRow(actionFooter));

        var accent = (System.Windows.Media.SolidColorBrush)System.Windows.Application.Current!
            .Resources["AccentBrush"];
        Assert(accent.Color.G > accent.Color.R && accent.Color.G >= accent.Color.B,
            "The simplified visual system must use the teal accent palette.");
        var fontFamily = (System.Windows.Controls.ComboBox)mainWindow.FindName("TranslationFontFamilyComboBox");
        var fontSize = (Slider)mainWindow.FindName("TranslationFontSizeSlider");
        var shortcutHelp = (TextBlock)mainWindow.FindName("ShortcutHelpText");
        var translationEngine = (System.Windows.Controls.ComboBox)mainWindow.FindName("TranslationEngineComboBox");
        var endpoint = (System.Windows.Controls.TextBox)mainWindow.FindName("EndpointTextBox");
        var endpointPanel = (StackPanel)mainWindow.FindName("EndpointPanel");
        var cloudOptions = (Grid)mainWindow.FindName("CloudOptionsPanel");
        var providerHint = (TextBlock)mainWindow.FindName("ProviderHintText");
        Assert(shortcutHelp.Text.Contains("F1", StringComparison.Ordinal) &&
               shortcutHelp.Text.Contains("F2", StringComparison.Ordinal),
            "The main window must explain both global hotkeys.");
        Assert(!fontFamily.IsEditable, "The font picker must stay limited to curated preview choices.");
        Assert(fontFamily.Items.Count is >= 1 and <= 8,
            "The font picker must expose no more than eight common installed fonts.");
        var previewTemplate = fontFamily.ItemTemplate;
        Assert(previewTemplate is not null, "Font choices must use a visual preview template.");

        AssertEqual(1, translationEngine.SelectedIndex);
        AssertEqual(System.Windows.Visibility.Collapsed, endpointPanel.Visibility);
        AssertEqual(System.Windows.Visibility.Collapsed, cloudOptions.Visibility);
        Assert(providerHint.Text.Contains("无需 API 密钥或后台服务", StringComparison.Ordinal),
            "The integrated provider must explain that it needs neither a key nor a background service.");
        Assert(translationEngine.SelectedItem is ComboBoxItem selectedEngine &&
               selectedEngine.Content?.ToString()?.Contains("内置离线", StringComparison.Ordinal) == true,
            "The integrated Qwen engine must be selected by default.");
        translationEngine.SelectedIndex = 0;
        AssertEqual(System.Windows.Visibility.Visible, endpointPanel.Visibility);
        AssertEqual(System.Windows.Visibility.Visible, cloudOptions.Visibility);
        translationEngine.SelectedIndex = 2;
        AssertEqual(System.Windows.Visibility.Visible, endpointPanel.Visibility);
        AssertEqual(System.Windows.Visibility.Collapsed, cloudOptions.Visibility);
        AssertEqual("http://127.0.0.1:5000", endpoint.Text);

        foreach (var fontOption in fontFamily.Items)
        {
            var optionType = fontOption.GetType();
            var displayName = optionType.GetProperty("DisplayName")!.GetValue(fontOption)?.ToString();
            var optionFontFamily = (System.Windows.Media.FontFamily)optionType
                .GetProperty("FontFamily")!
                .GetValue(fontOption)!;
            var preview = (Grid)previewTemplate!.LoadContent();
            preview.DataContext = fontOption;
            preview.Dispatcher.Invoke(
                () => { },
                System.Windows.Threading.DispatcherPriority.DataBind);
            var previewName = (TextBlock)preview.Children[0];
            var previewSample = (TextBlock)preview.Children[1];
            AssertEqual(displayName, previewName.Text);
            AssertEqual(optionFontFamily.Source, previewName.FontFamily.Source);
            AssertEqual(optionFontFamily.Source, previewSample.FontFamily.Source);
            Assert(previewSample.Text.Contains("译文", StringComparison.Ordinal),
                "Each font option must include a readable translation sample.");
        }
        Assert(fontSize.Minimum <= 10 && fontSize.Maximum >= 48,
            "The translation font-size control must expose the supported range.");

        var targetLanguage = (System.Windows.Controls.ComboBox)mainWindow.FindName("TargetLanguageComboBox");
        targetLanguage.SelectedIndex = 1;
        targetLanguage.Text = "简体中文";
        var getTargetLanguage = typeof(ScreenTranslator.App.MainWindow).GetMethod(
            "GetTargetLanguage",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        AssertEqual("zh-CN", getTargetLanguage.Invoke(mainWindow, null)?.ToString());
    }

    private static void TestSharpRenderingSettings()
    {
        var mainWindow = new ScreenTranslator.App.MainWindow();
        var mainSurface = (Border)mainWindow.FindName("MainSurface");
        var mainScrollViewer = (ScrollViewer)mainWindow.FindName("ContentScrollViewer");
        Assert(!mainWindow.AllowsTransparency,
            "The main window must not use layered transparency because it disables ClearType rendering.");
        Assert(mainSurface.Effect is null,
            "Effects must not wrap the main content tree because they soften text rendering.");
        Assert(mainWindow.UseLayoutRounding && mainWindow.SnapsToDevicePixels,
            "The main window must align layout to physical device pixels.");
        AssertEqual(
            System.Windows.Media.TextRenderingMode.ClearType,
            System.Windows.Media.TextOptions.GetTextRenderingMode(mainWindow));
        AssertEqual(ScrollBarVisibility.Hidden, mainScrollViewer.VerticalScrollBarVisibility);

        var panel = new TranslationPanelWindow();
        var panelScrollViewer = (ScrollViewer)panel.FindName("TranslationScrollViewer");
        AssertEqual(
            System.Windows.Media.TextRenderingMode.ClearType,
            System.Windows.Media.TextOptions.GetTextRenderingMode(panel));
        AssertEqual(ScrollBarVisibility.Hidden, panelScrollViewer.VerticalScrollBarVisibility);
        panel.AllowClose();

        var overlay = new TranslationOverlayWindow();
        var overlayScrollViewer = (ScrollViewer)overlay.FindName("TranslationScrollViewer");
        AssertEqual(ScrollBarVisibility.Hidden, overlayScrollViewer.VerticalScrollBarVisibility);
        overlay.Close();

        var projectPath = Path.Combine(
            Environment.CurrentDirectory,
            "src",
            "ScreenTranslator.App",
            "ScreenTranslator.App.csproj");
        var project = File.ReadAllText(projectPath);
        Assert(project.Contains(
                "<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>",
                StringComparison.Ordinal),
            "The application project must opt into per-monitor DPI awareness.");
    }

    private static void TestRenderMainWindowPreview()
    {
        if (System.Windows.Application.Current is null)
        {
            var application = new ScreenTranslator.App.App();
            application.InitializeComponent();
        }

        var mainWindow = new ScreenTranslator.App.MainWindow();
        var width = (int)mainWindow.Width;
        var height = (int)mainWindow.Height;
        var rootVisual = (System.Windows.FrameworkElement)mainWindow.Content;
        rootVisual.Measure(new System.Windows.Size(width, height));
        rootVisual.Arrange(new System.Windows.Rect(0, 0, width, height));
        rootVisual.UpdateLayout();

        var rendered = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width,
            height,
            96,
            96,
            System.Windows.Media.PixelFormats.Pbgra32);
        rendered.Render(rootVisual);

        var previewPath = Path.Combine(
            Environment.CurrentDirectory,
            "artifacts",
            "ui-preview.png");
        Directory.CreateDirectory(Path.GetDirectoryName(previewPath)!);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rendered));
        using var stream = File.Create(previewPath);
        encoder.Save(stream);

        Assert(new FileInfo(previewPath).Length > 10_000,
            "The rendered UI preview must contain visible interface content.");
    }

    private static void TestRegionSelectorWindow()
    {
        var selector = new RegionSelectionWindow();
        var canvas = (Canvas)selector.FindName("RootCanvas");
        Assert(selector.Topmost, "Region selector must stay above other windows while selecting.");
        Assert(selector.AllowsTransparency, "Region selector must be transparent.");
        Assert(canvas.Background is not null,
            "The full selector canvas must be hit-testable so drag and right-click input works anywhere.");
        Assert(selector.Width >= System.Windows.SystemParameters.VirtualScreenWidth - 1,
            "Region selector must cover the virtual desktop width.");
        Assert(selector.Height >= System.Windows.SystemParameters.VirtualScreenHeight - 1,
            "Region selector must cover the virtual desktop height.");
        selector.Close();
    }

    private static void TestRegionIndicatorWindow()
    {
        var indicator = new RegionIndicatorWindow();
        var border = (Border)indicator.FindName("IndicatorBorder");
        var windowBackground = (System.Windows.Media.SolidColorBrush)indicator.Background;
        var borderBrush = (System.Windows.Media.SolidColorBrush)border.BorderBrush;

        Assert(indicator.Topmost, "The selected-region indicator must remain visible above the source app.");
        Assert(!indicator.ShowActivated && !indicator.Focusable,
            "The selected-region indicator must not steal focus.");
        Assert(windowBackground.Color.A == 0 && border.Background is null,
            "The selected-region indicator interior must be completely transparent.");
        Assert(border.BorderThickness.Left <= 1 && borderBrush.Color.A is > 0 and <= 0x40,
            "The selected-region border must be a barely visible thin line.");

        indicator.SetRegion(new ScreenRegion(96, 96, 320, 180));
        Assert(indicator.Width > 0 && indicator.Height > 0,
            "The selected-region indicator must map to a positive display area.");
        indicator.Close();
    }

    private static void TestOverlayWindow()
    {
        var overlay = new TranslationOverlayWindow();
        Assert(overlay.Topmost, "Direct overlay must stay topmost.");
        Assert(!overlay.ShowActivated, "Direct overlay must not steal focus.");
        var overlaySurface = (Border)overlay.FindName("OverlaySurface");
        Assert(overlaySurface.CornerRadius.TopLeft >= 8,
            "The redesigned overlay must use a clean rounded surface.");

        const string translated = "1. Alpha\n   2. Beta";
        overlay.SetTranslation(translated);
        overlay.SetTypography("Arial", 20);
        overlay.SetRegion(new ScreenRegion(96, 96, 320, 180));

        var textBlock = (TextBlock)overlay.FindName("TranslationText");
        AssertEqual(translated, textBlock.Text);
        AssertEqual("Arial", textBlock.FontFamily.Source);
        Assert(Math.Abs(textBlock.FontSize - 20) < 0.001, "Overlay font size was not applied.");
        Assert(overlay.Width > 0 && overlay.Height > 0, "Overlay region must have a positive size.");
        overlay.Close();
    }

    private static async Task TestLibreTranslateAdapterAsync()
    {
        const string source = "  Hello\r\n\r\nWorld  \nHello";
        const string translated = "  你好\r\n\r\n世界  \n你好";
        var handler = new RecordingHandler(_ => JsonResponse(new
        {
            translatedText = new[] { "你好", "世界" },
            detectedLanguage = new[]
            {
                new { confidence = 100, language = "en" },
                new { confidence = 100, language = "en" }
            }
        }));
        using var client = new HttpClient(handler);
        var service = new LibreTranslateTranslationService(client);
        service.Configure(new Uri("http://127.0.0.1:5000"));

        var result = await service.TranslateAsync(source, "auto", "zh-CN");
        AssertEqual(source, result.SourceText);
        AssertEqual(translated, result.TranslatedText);
        AssertEqual(1, handler.RequestCount);
        AssertEqual("http://127.0.0.1:5000/translate", handler.RequestUri?.ToString());
        Assert(handler.Authorization is null, "A self-hosted local provider must not send an API key.");

        using var document = JsonDocument.Parse(handler.RequestBody!);
        var root = document.RootElement;
        AssertEqual("auto", root.GetProperty("source").GetString());
        AssertEqual("zh", root.GetProperty("target").GetString());
        AssertEqual("text", root.GetProperty("format").GetString());
        var batch = root.GetProperty("q");
        AssertEqual(JsonValueKind.Array, batch.ValueKind);
        AssertEqual(2, batch.GetArrayLength());
        AssertEqual("Hello", batch[0].GetString());
        AssertEqual("World", batch[1].GetString());

        var cachedResult = await service.TranslateAsync(source, "auto", "zh-CN");
        AssertEqual(translated, cachedResult.TranslatedText);
        AssertEqual(1, handler.RequestCount);
    }

    private static async Task TestQwenLocalTranslationAsync()
    {
        const string source = "  Hello\r\n\r\nWorld  \nHello";
        const string translated = "  你好\r\n\r\n世界  \n你好";
        var runtime = new FakeLocalModelRuntime(["“你好”", "Translation: 世界\n"]);
        var service = new QwenLocalTranslationService(runtime);

        var result = await service.TranslateAsync(source, "auto", "zh-CN");

        AssertEqual(source, result.SourceText);
        AssertEqual(translated, result.TranslatedText);
        AssertEqual("auto", result.SourceLanguage);
        AssertEqual("zh", result.TargetLanguage);
        AssertEqual(2, runtime.Prompts.Count);
        Assert(runtime.Prompts.All(prompt =>
                prompt.Contains("Simplified Chinese", StringComparison.Ordinal) &&
                prompt.Contains("Detect the source language automatically", StringComparison.Ordinal)),
            "Every local request must explicitly auto-detect the source language and use the selected target.");
        var normalizedPrompt = runtime.Prompts[0].Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert(normalizedPrompt.Contains("<source>\nHello\n</source>", StringComparison.Ordinal),
            "The source text must be isolated from model instructions.");
        AssertEqual(1, runtime.InitializeCount);

        var cached = await service.TranslateAsync(source, "auto", "zh-CN");
        AssertEqual(translated, cached.TranslatedText);
        AssertEqual(2, runtime.Prompts.Count);
    }

    private static async Task TestLocalModelStoreAsync()
    {
        var payload = Encoding.UTF8.GetBytes("small verified model fixture");
        var descriptor = new LocalModelDescriptor(
            "fixture.gguf",
            new Uri("https://example.test/models/fixture.gguf"),
            payload.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(payload)));
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        });
        using var client = new HttpClient(handler);
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"ScreenTranslator-model-test-{Guid.NewGuid():N}");
        var store = new LocalModelStore(client, descriptor, directory);
        var updates = new List<LocalModelProgress>();
        var progress = new DelegateProgress<LocalModelProgress>(updates.Add);

        try
        {
            var firstPath = await store.EnsureModelAsync(progress);
            var secondPath = await store.EnsureModelAsync(progress);

            AssertEqual(firstPath, secondPath);
            AssertEqual(1, handler.RequestCount);
            Assert(File.ReadAllBytes(firstPath).SequenceEqual(payload),
                "The downloaded model bytes must match the verified payload.");
            Assert(updates.Any(update =>
                    update.BytesReceived == payload.LongLength &&
                    update.TotalBytes == payload.LongLength),
                "Model preparation must report completed byte progress.");
        }
        finally
        {
            if (File.Exists(store.ModelPath))
            {
                File.Delete(store.ModelPath);
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory);
            }
        }
    }

    private static async Task TestIntegratedLocalModelAsync()
    {
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var store = new LocalModelStore(client);
        using var runtime = new LlamaSharpLocalModelRuntime(store);
        var service = new QwenLocalTranslationService(runtime);
        var progress = new DelegateProgress<LocalModelProgress>(
            update => Console.WriteLine($"MODEL {update.Title}: {update.Detail}"));

        await service.InitializeAsync(progress);
        var result = await service.TranslateAsync("Hello world", "auto", "zh-CN");
        Console.WriteLine($"LOCAL TRANSLATION {result.TranslatedText}");

        Assert(!string.IsNullOrWhiteSpace(result.TranslatedText),
            "The integrated model must produce a non-empty translation.");
        Assert(result.TranslatedText.Any(character => character is >= '\u3400' and <= '\u9fff'),
            $"Expected a Chinese translation but received: {result.TranslatedText}");
    }

    private static async Task TestLibreTranslateReadinessAsync()
    {
        var readyHandler = new RecordingHandler(request =>
        {
            AssertEqual(HttpMethod.Get, request.Method);
            return JsonResponse(new[]
            {
                new
                {
                    code = "en",
                    name = "English",
                    targets = new[] { "fr", "zh" }
                }
            });
        });
        using var readyClient = new HttpClient(readyHandler);
        var readyService = new LibreTranslateTranslationService(readyClient);
        readyService.Configure(new Uri("http://127.0.0.1:5000/api/translate?ignored=true"));

        var ready = await readyService.CheckReadinessAsync("zh-CN");
        Assert(ready.IsReady, ready.Message);
        AssertEqual(TranslationServiceReadinessState.Ready, ready.State);
        AssertEqual("http://127.0.0.1:5000/api/languages", readyHandler.RequestUri?.ToString());
        AssertEqual(1, readyHandler.RequestCount);

        var missingModelHandler = new RecordingHandler(_ => JsonResponse(new[]
        {
            new
            {
                code = "en",
                name = "English",
                targets = new[] { "fr" }
            }
        }));
        using var missingModelClient = new HttpClient(missingModelHandler);
        var missingModelService = new LibreTranslateTranslationService(missingModelClient);
        missingModelService.Configure(new Uri("http://127.0.0.1:5000"));

        var missingModel = await missingModelService.CheckReadinessAsync("zh-CN");
        Assert(!missingModel.IsReady, "A missing target model must block offline translation startup.");
        AssertEqual(
            TranslationServiceReadinessState.TargetLanguageUnavailable,
            missingModel.State);
        Assert(missingModel.Message.Contains("zh", StringComparison.Ordinal),
            "The missing-model message must identify the normalized target language.");

        var unavailableHandler = new RecordingHandler(
            _ => throw new HttpRequestException("Connection refused"));
        using var unavailableClient = new HttpClient(unavailableHandler);
        var unavailableService = new LibreTranslateTranslationService(unavailableClient);
        unavailableService.Configure(new Uri("http://127.0.0.1:5000"));

        var unavailable = await unavailableService.CheckReadinessAsync("zh-CN");
        Assert(!unavailable.IsReady, "An unreachable local service must block startup.");
        AssertEqual(
            TranslationServiceReadinessState.ServiceUnavailable,
            unavailable.State);
        Assert(unavailable.Message.Contains("启动 LibreTranslate", StringComparison.Ordinal),
            "The unavailable-service message must contain an actionable startup instruction.");

        var malformedHandler = new RecordingHandler(_ => JsonResponse(new object[]
        {
            "not-a-language",
            new { name = "Missing code" }
        }));
        using var malformedClient = new HttpClient(malformedHandler);
        var malformedService = new LibreTranslateTranslationService(malformedClient);
        malformedService.Configure(new Uri("http://127.0.0.1:5000"));

        var malformed = await malformedService.CheckReadinessAsync("zh-CN");
        Assert(!malformed.IsReady,
            "A malformed language list must produce a readiness failure instead of throwing.");
        AssertEqual(
            TranslationServiceReadinessState.ServiceUnavailable,
            malformed.State);
    }

    private static async Task TestDeepSeekAdapterAsync()
    {
        const string source = "  item 1\n\n    item 2  ";
        const string translated = "  项目 1\n\n    项目 2  ";
        var handler = new RecordingHandler(_ => JsonResponse(new
        {
            choices = new[] { new { message = new { content = translated } } }
        }));
        using var client = new HttpClient(handler);
        var service = new OpenAiCompatibleTranslationService(client);
        service.Configure(new TranslationProviderOptions(
            new Uri("https://api.deepseek.com/chat/completions"),
            "deepseek-v4-flash",
            "unit-test-key"));

        var result = await service.TranslateAsync(
            source,
            "auto",
            "zh-CN",
            sourceLanguageHint: "en-US");
        AssertEqual(source, result.SourceText);
        AssertEqual(translated, result.TranslatedText);
        AssertEqual("Bearer unit-test-key", handler.Authorization);

        using var document = JsonDocument.Parse(handler.RequestBody!);
        var root = document.RootElement;
        AssertEqual("disabled", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert(Math.Abs(root.GetProperty("temperature").GetDouble()) < 0.001,
            "Translation requests must use zero temperature for deterministic output.");
        AssertEqual(source, root.GetProperty("messages")[1].GetProperty("content").GetString());
        var prompt = root.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert(prompt.Contains("Detect the source language", StringComparison.Ordinal),
            "Automatic source-language detection must be explicit in the prompt.");
        Assert(!prompt.Contains("en-US", StringComparison.Ordinal),
            "The installed OCR language pack must not bias model source-language detection.");
        Assert(prompt.Contains("blank lines", StringComparison.Ordinal), "Formatting prompt must preserve blank lines.");
        Assert(prompt.Contains("indentation", StringComparison.Ordinal), "Formatting prompt must preserve indentation.");
        Assert(prompt.Contains("prioritize accurate, natural translation", StringComparison.Ordinal),
            "Formatting constraints must not override translation accuracy.");
    }

    private static async Task TestCloudSessionCacheAsync()
    {
        var responses = new Queue<string>(["你好", "世界", "您好"]);
        var handler = new RecordingHandler(_ => JsonResponse(new
        {
            choices = new[] { new { message = new { content = responses.Dequeue() } } }
        }));
        using var client = new HttpClient(handler);
        var service = new OpenAiCompatibleTranslationService(client);
        var endpoint = new Uri("https://api.deepseek.com/chat/completions");
        service.Configure(new TranslationProviderOptions(endpoint, "model-a", "unit-test-key"));

        var first = await service.TranslateAsync("Hello", "auto", "zh-CN");
        var second = await service.TranslateAsync("World", "auto", "zh-CN");
        var repeated = await service.TranslateAsync("  HELLO\r\n", "auto", "zh-CN");

        AssertEqual("你好", first.TranslatedText);
        AssertEqual("世界", second.TranslatedText);
        AssertEqual(first.TranslatedText, repeated.TranslatedText);
        AssertEqual(2, handler.RequestCount);

        service.Configure(new TranslationProviderOptions(endpoint, "model-b", "unit-test-key"));
        var reconfigured = await service.TranslateAsync("Hello", "auto", "zh-CN");
        AssertEqual("您好", reconfigured.TranslatedText);
        AssertEqual(3, handler.RequestCount);
    }

    private static async Task TestGenericAdapterAsync()
    {
        var handler = new RecordingHandler(_ => JsonResponse(new
        {
            choices = new[] { new { message = new { content = "bonjour" } } }
        }));
        using var client = new HttpClient(handler);
        var service = new OpenAiCompatibleTranslationService(client);
        service.Configure(new TranslationProviderOptions(
            new Uri("https://example.test/v1/chat/completions"),
            "compatible-model",
            "unit-test-key"));

        var result = await service.TranslateAsync("hello", "en", "fr");
        AssertEqual("bonjour", result.TranslatedText);

        using var document = JsonDocument.Parse(handler.RequestBody!);
        Assert(!document.RootElement.TryGetProperty("thinking", out _),
            "Generic OpenAI-compatible requests must omit DeepSeek-only thinking fields.");
    }

    private static async Task TestUntranslatedOutputRetryAsync()
    {
        var responseIndex = 0;
        var handler = new RecordingHandler(_ => JsonResponse(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content = responseIndex++ == 0 ? "Open settings" : "打开设置"
                    }
                }
            }
        }));
        using var client = new HttpClient(handler);
        var service = new OpenAiCompatibleTranslationService(client);
        service.Configure(new TranslationProviderOptions(
            new Uri("https://example.test/v1/chat/completions"),
            "compatible-model",
            "unit-test-key"));

        var result = await service.TranslateAsync("Open settings", "auto", "zh-CN");

        AssertEqual("打开设置", result.TranslatedText);
        AssertEqual(2, handler.RequestCount);
        using var document = JsonDocument.Parse(handler.RequestBody!);
        var prompt = document.RootElement
            .GetProperty("messages")[0]
            .GetProperty("content")
            .GetString()!;
        Assert(prompt.Contains("previous attempt echoed the source", StringComparison.Ordinal),
            "The retry must use an explicit corrective translation prompt.");
    }

    private static async Task TestWorseRetryIsRejectedAsync()
    {
        var responseIndex = 0;
        var handler = new RecordingHandler(_ => JsonResponse(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content = responseIndex++ == 0 ? "Open settings" : "Open preferences"
                    }
                }
            }
        }));
        using var client = new HttpClient(handler);
        var service = new OpenAiCompatibleTranslationService(client);
        service.Configure(new TranslationProviderOptions(
            new Uri("https://example.test/v1/chat/completions"),
            "compatible-model",
            "unit-test-key"));

        var result = await service.TranslateAsync("Open settings", "auto", "zh-CN");

        AssertEqual("Open settings", result.TranslatedText);
        AssertEqual(2, handler.RequestCount);
    }

    private static async Task TestMixedTranslationIsAcceptedAsync()
    {
        var handler = new RecordingHandler(_ => JsonResponse(new
        {
            choices = new[] { new { message = new { content = "打开 Settings" } } }
        }));
        using var client = new HttpClient(handler);
        var service = new OpenAiCompatibleTranslationService(client);
        service.Configure(new TranslationProviderOptions(
            new Uri("https://example.test/v1/chat/completions"),
            "compatible-model",
            "unit-test-key"));

        var result = await service.TranslateAsync("Open settings", "auto", "zh-CN");

        AssertEqual("打开 Settings", result.TranslatedText);
        AssertEqual(1, handler.RequestCount);
    }

    private static async Task TestWindowsOcrAsync()
    {
        var frame = CreateTextFrame("HELLO 314159");
        var recognizer = new WindowsOcrTextRecognizer();
        var result = await recognizer.RecognizeAsync(frame);
        var normalized = new string(result.Text.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

        Assert(normalized.Contains("HELLO", StringComparison.Ordinal),
            $"OCR did not recognize HELLO. Actual: {result.Text}");
        Assert(normalized.Contains("314159", StringComparison.Ordinal),
            $"OCR did not recognize 314159. Actual: {result.Text}");
    }

    private static async Task TestScreenCaptureAndOcrAsync()
    {
        using var form = new System.Windows.Forms.Form
        {
            BackColor = System.Drawing.Color.White,
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
            Location = new System.Drawing.Point(120, 120),
            ShowInTaskbar = false,
            Size = new System.Drawing.Size(1000, 260),
            StartPosition = System.Windows.Forms.FormStartPosition.Manual,
            TopMost = true
        };
        using var label = new System.Windows.Forms.Label
        {
            BackColor = System.Drawing.Color.White,
            Dock = System.Windows.Forms.DockStyle.Fill,
            Font = new System.Drawing.Font(
                "Arial",
                64,
                System.Drawing.FontStyle.Bold,
                System.Drawing.GraphicsUnit.Pixel),
            ForeColor = System.Drawing.Color.Black,
            Text = "CAPTURE 271828",
            TextAlign = System.Drawing.ContentAlignment.MiddleCenter
        };
        form.Controls.Add(label);

        try
        {
            form.Show();
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(500);
            System.Windows.Forms.Application.DoEvents();

            var bounds = form.RectangleToScreen(form.ClientRectangle);
            var capture = new GdiScreenCaptureService();
            var frame = await capture.CaptureAsync(new ScreenRegion(
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height));
            var recognizer = new WindowsOcrTextRecognizer();
            var result = await recognizer.RecognizeAsync(frame);
            var normalized = new string(result.Text.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

            Assert(normalized.Contains("CAPTURE", StringComparison.Ordinal),
                $"Captured OCR did not recognize CAPTURE. Actual: {result.Text}");
            Assert(normalized.Contains("271828", StringComparison.Ordinal),
                $"Captured OCR did not recognize 271828. Actual: {result.Text}");
        }
        finally
        {
            form.Close();
            System.Windows.Forms.Application.DoEvents();
        }
    }

    private static ScreenCaptureFrame CreateTextFrame(string text)
    {
        const int width = 1000;
        const int height = 220;
        using var bitmap = new System.Drawing.Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        using (var font = new System.Drawing.Font(
                   "Arial",
                   72,
                   System.Drawing.FontStyle.Bold,
                   System.Drawing.GraphicsUnit.Pixel))
        {
            graphics.Clear(System.Drawing.Color.White);
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            graphics.DrawString(text, font, System.Drawing.Brushes.Black, new System.Drawing.PointF(24, 52));
        }

        var rectangle = new System.Drawing.Rectangle(0, 0, width, height);
        var bitmapData = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        try
        {
            var stride = width * 4;
            var pixels = new byte[stride * height];

            for (var row = 0; row < height; row++)
            {
                var sourceRow = IntPtr.Add(bitmapData.Scan0, row * bitmapData.Stride);
                Marshal.Copy(sourceRow, pixels, row * stride, stride);
            }

            return new ScreenCaptureFrame(width, height, stride, pixels);
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }
    }

    private static HttpResponseMessage JsonResponse(object value)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected <{expected}> but found <{actual}>.");
        }
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        public string? Authorization { get; private set; }

        public int RequestCount { get; private set; }

        public Uri? RequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization?.ToString();
            RequestUri = request.RequestUri;
            RequestCount++;
            return responseFactory(request);
        }
    }

    private sealed class FakeLocalModelRuntime(IEnumerable<string> responses) : ILocalModelRuntime
    {
        private readonly Queue<string> _responses = new(responses);

        public int InitializeCount { get; private set; }

        public List<string> Prompts { get; } = [];

        public Task InitializeAsync(
            IProgress<LocalModelProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InitializeCount++;
            return Task.CompletedTask;
        }

        public Task<string> GenerateAsync(
            string prompt,
            int maximumTokens,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert(maximumTokens > 0, "Local generation must have a positive token budget.");
            Prompts.Add(prompt);
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class DelegateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
