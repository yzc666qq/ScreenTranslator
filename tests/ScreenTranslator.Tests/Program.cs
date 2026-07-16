using System.Drawing.Imaging;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using ScreenTranslator.App.Core;
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
        Run("Unchanged OCR text is not translated twice", TestRecognizedTextChangeTracking);
        Run("Main controls do not stay topmost", TestMainWindowLayering);
        Run("Region selector covers the virtual desktop", TestRegionSelectorWindow);
        Run("Side panel supports locking, resizing and opacity", TestSidePanelControls);
        Run("Overlay preserves translated text and region", TestOverlayWindow);
        Run("DeepSeek adapter preserves formatting", () => TestDeepSeekAdapterAsync().GetAwaiter().GetResult());
        Run("Generic OpenAI adapter omits DeepSeek-only fields", () => TestGenericAdapterAsync().GetAwaiter().GetResult());
        Run("Windows OCR recognizes a synthetic image", () => TestWindowsOcrAsync().GetAwaiter().GetResult());

        if (args.Contains("--screen-capture", StringComparer.OrdinalIgnoreCase))
        {
            Run("Screen capture and OCR work end to end", () => TestScreenCaptureAndOcrAsync().GetAwaiter().GetResult());
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

    private static void TestRecognizedTextChangeTracking()
    {
        var tracker = new RecognizedTextChangeTracker();
        Assert(!tracker.ShouldTranslate("   "), "Whitespace-only OCR output must not be translated.");
        Assert(tracker.ShouldTranslate("first"), "New OCR text must be translated.");
        Assert(tracker.ShouldTranslate("first"), "A failed translation attempt must remain retryable.");

        tracker.MarkTranslated("first");
        Assert(!tracker.ShouldTranslate("first"), "Successfully translated text must be deduplicated.");
        Assert(tracker.ShouldTranslate("second"), "Changed OCR text must be translated.");

        tracker.Reset();
        Assert(tracker.ShouldTranslate("first"), "Starting a new run must reset deduplication state.");
    }

    private static void TestSidePanelControls()
    {
        var panel = new TranslationPanelWindow();
        Assert(panel.Topmost, "Side panel must stay topmost.");
        AssertEqual(WpfResizeMode.CanResizeWithGrip, panel.ResizeMode);

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
        var fontFamily = (System.Windows.Controls.ComboBox)mainWindow.FindName("TranslationFontFamilyComboBox");
        var fontSize = (Slider)mainWindow.FindName("TranslationFontSizeSlider");
        Assert(fontFamily.IsEditable, "Users must be able to enter any installed translation font.");
        Assert(fontSize.Minimum <= 10 && fontSize.Maximum >= 48,
            "The translation font-size control must expose the supported range.");
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

    private static void TestOverlayWindow()
    {
        var overlay = new TranslationOverlayWindow();
        Assert(overlay.Topmost, "Direct overlay must stay topmost.");
        Assert(!overlay.ShowActivated, "Direct overlay must not steal focus.");

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
        AssertEqual(source, root.GetProperty("messages")[1].GetProperty("content").GetString());
        var prompt = root.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert(prompt.Contains("自动判断源语言", StringComparison.Ordinal),
            "Automatic source-language detection must be explicit in the prompt.");
        Assert(prompt.Contains("en-US", StringComparison.Ordinal) &&
               prompt.Contains("仅作为线索", StringComparison.Ordinal),
            "OCR language must be treated as a fallible hint.");
        Assert(prompt.Contains("空行", StringComparison.Ordinal), "Formatting prompt must preserve blank lines.");
        Assert(prompt.Contains("相对缩进", StringComparison.Ordinal), "Formatting prompt must preserve indentation.");
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

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization?.ToString();
            return responseFactory(request);
        }
    }
}
