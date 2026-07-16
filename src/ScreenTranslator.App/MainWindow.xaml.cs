using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScreenTranslator.App.Core;
using ScreenTranslator.App.Interop;
using ScreenTranslator.App.Services;
using ScreenTranslator.App.Windows;
using Color = System.Windows.Media.Color;
using WpfFontFamily = System.Windows.Media.FontFamily;

namespace ScreenTranslator.App;

public partial class MainWindow : Window
{
    private static readonly (string FontFamilyName, string DisplayName)[] PreferredTranslationFonts =
    [
        ("Microsoft YaHei UI", "微软雅黑"),
        ("DengXian", "等线"),
        ("SimSun", "宋体"),
        ("SimHei", "黑体"),
        ("KaiTi", "楷体"),
        ("FangSong", "仿宋"),
        ("Arial", "Arial"),
        ("Times New Roman", "Times New Roman")
    ];

    private readonly IScreenCaptureService _screenCaptureService = new GdiScreenCaptureService();
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(45) };
    private readonly TranslationOverlayWindow _overlayWindow = new();
    private readonly TranslationPanelWindow _panelWindow = new();
    private readonly RegionIndicatorWindow _regionIndicatorWindow = new();
    private readonly GlobalHotkeyManager _globalHotkeyManager = new();
    private readonly OpenAiCompatibleTranslationService _translationService;
    private readonly RecognizedTextChangeTracker _textChangeTracker = new();

    private ITextRecognizer? _textRecognizer;
    private ScreenRegion? _selectedRegion;
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private string? _lastTranslation;
    private bool _isSelectingRegion;

    public MainWindow()
    {
        InitializeComponent();
        _translationService = new OpenAiCompatibleTranslationService(_httpClient);
        InitializeTranslationTypography();

        SourceInitialized += MainWindow_SourceInitialized;
        Closed += MainWindow_Closed;

        var environmentKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if (!string.IsNullOrWhiteSpace(environmentKey))
        {
            ApiKeyPasswordBox.Password = environmentKey;
            StatusDetailText.Text = "已从 DEEPSEEK_API_KEY 读取密钥";
        }

        UpdateOverlayOpacityLabel();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private async void SelectRegionButton_Click(object sender, RoutedEventArgs e)
    {
        await SelectRegionAsync();
    }

    private async Task SelectRegionAsync()
    {
        if (_isSelectingRegion)
        {
            return;
        }

        _isSelectingRegion = true;
        var resumeTranslation = _runCancellation is not null;

        try
        {
            await StopTranslationAsync();
            _overlayWindow.Hide();
            _panelWindow.Hide();
            _regionIndicatorWindow.Hide();

            SetStatus("请拖动鼠标框选翻译区域", "按 Esc 或鼠标右键取消", isError: false);
            Hide();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            var selector = new RegionSelectionWindow();
            var accepted = selector.ShowDialog() == true;

            Show();
            Activate();

            if (!accepted || selector.SelectedRegion is not { } region)
            {
                if (_selectedRegion is { } existingRegion)
                {
                    ShowRegionIndicator(existingRegion);
                }

                if (resumeTranslation)
                {
                    StartTranslation();
                }
                else
                {
                    SetStatus("已取消框选", "原有区域保持不变", isError: false);
                }

                return;
            }

            _selectedRegion = region;
            RegionText.Text = $"X {region.X}, Y {region.Y}, {region.Width} × {region.Height} px";
            _overlayWindow.SetRegion(region);
            ShowRegionIndicator(region);
            PlacePanelNextTo(region);

            if (resumeTranslation)
            {
                StartTranslation();
            }
            else
            {
                SetStatus("区域已选择", "配置模型后即可开始实时翻译", isError: false);
            }
        }
        finally
        {
            _isSelectingRegion = false;
        }
    }

    private async void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runCancellation is not null)
        {
            await StopTranslationAsync();
            return;
        }

        StartTranslation();
    }

    private void StartTranslation()
    {
        if (_selectedRegion is not { } region)
        {
            SetStatus("请先框选屏幕区域", "点击“框选区域”后拖动鼠标", isError: true);
            return;
        }

        if (!Uri.TryCreate(EndpointTextBox.Text.Trim(), UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp))
        {
            SetStatus("接口地址无效", "请输入完整的 http 或 https 地址", isError: true);
            return;
        }

        var model = ModelTextBox.Text.Trim();
        var apiKey = ApiKeyPasswordBox.Password.Trim();

        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(apiKey))
        {
            SetStatus("模型或 API 密钥为空", "可输入密钥或设置 DEEPSEEK_API_KEY", isError: true);
            return;
        }

        try
        {
            _textRecognizer ??= new WindowsOcrTextRecognizer();
            _translationService.Configure(new TranslationProviderOptions(endpoint, model, apiKey));
        }
        catch (Exception exception)
        {
            SetStatus("OCR 初始化失败", exception.Message, isError: true);
            return;
        }

        _textChangeTracker.Reset();
        _lastTranslation = null;
        _runCancellation = new CancellationTokenSource();
        StartStopButton.Content = "停止实时翻译";
        SelectRegionButton.IsEnabled = false;
        SetStatus("正在识别所选区域", "只在文字变化时调用翻译接口", isError: false);
        ShowActiveOutput("正在识别…", region);

        _runTask = RunTranslationLoopAsync(region, _runCancellation.Token);
    }

    private async Task RunTranslationLoopAsync(ScreenRegion region, CancellationToken cancellationToken)
    {
        var interval = GetSelectedInterval();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var frame = await _screenCaptureService.CaptureAsync(region, cancellationToken);
                var recognized = await _textRecognizer!.RecognizeAsync(frame, cancellationToken);

                if (string.IsNullOrWhiteSpace(recognized.Text))
                {
                    SetStatus("未识别到文字", "继续监视所选区域", isError: false);
                }
                else if (_textChangeTracker.ShouldTranslate(recognized.Text))
                {
                    const string languageDetection = "源语言：由模型根据正文自动检测";
                    SetStatus("发现文字变化，正在翻译", languageDetection, isError: false);
                    var translated = await _translationService.TranslateAsync(
                        recognized.Text,
                        "auto",
                        GetTargetLanguage(),
                        cancellationToken);

                    _textChangeTracker.MarkTranslated(recognized.Text);
                    _lastTranslation = translated.TranslatedText;
                    ShowActiveOutput(translated.TranslatedText, region);
                    SetStatus(
                        "实时翻译运行中",
                        $"{languageDetection} · 更新：{DateTime.Now:HH:mm:ss}",
                        isError: false);
                }

                await Task.Delay(interval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                SetStatus("本轮翻译失败，将自动重试", exception.Message, isError: true);
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(3000, interval.TotalMilliseconds)), cancellationToken);
            }
        }
    }

    private async Task StopTranslationAsync()
    {
        var cancellation = _runCancellation;
        var task = _runTask;
        _runCancellation = null;
        _runTask = null;

        if (cancellation is not null)
        {
            cancellation.Cancel();
        }

        if (task is not null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                // Expected while stopping.
            }
        }

        cancellation?.Dispose();
        StartStopButton.Content = "开始实时翻译";
        SelectRegionButton.IsEnabled = true;

        if (IsLoaded)
        {
            SetStatus("实时翻译已停止", "译文窗口保留，可重新启动", isError: false);
        }
    }

    private async Task ExitTranslationModeAsync()
    {
        await StopTranslationAsync();
        _overlayWindow.Hide();
        _panelWindow.Hide();
        _lastTranslation = null;
        SetStatus("已退出实时翻译", "按 F1 可重选区域，或点击开始按钮重新启动", isError: false);
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        NativeWindowBehavior.ExcludeFromCapture(this);
        _globalHotkeyManager.Pressed += GlobalHotkeyManager_Pressed;

        try
        {
            var unavailableHotkeys = _globalHotkeyManager.Register(this);
            if (unavailableHotkeys.Count > 0)
            {
                SetStatus(
                    "部分快捷键不可用",
                    $"{string.Join("、", unavailableHotkeys)} 已被其他程序占用，仍可使用窗口按钮",
                    isError: true);
            }
        }
        catch (Exception exception)
        {
            SetStatus("快捷键初始化失败", exception.Message, isError: true);
        }
    }

    private async void GlobalHotkeyManager_Pressed(GlobalHotkeyCommand command)
    {
        try
        {
            switch (command)
            {
                case GlobalHotkeyCommand.ReselectRegion:
                    await SelectRegionAsync();
                    break;
                case GlobalHotkeyCommand.StopTranslation:
                    await ExitTranslationModeAsync();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(command), command, null);
            }
        }
        catch (Exception exception)
        {
            SetStatus("快捷键操作失败", exception.Message, isError: true);
        }
    }

    private void ShowActiveOutput(string text, ScreenRegion region)
    {
        if (ModeComboBox.SelectedIndex == 0)
        {
            _panelWindow.Hide();
            _overlayWindow.SetRegion(region);
            _overlayWindow.SetTranslation(text);
            _overlayWindow.Opacity = OverlayOpacitySlider.Value;

            if (!_overlayWindow.IsVisible)
            {
                _overlayWindow.Show();
            }
        }
        else
        {
            _overlayWindow.Hide();
            _panelWindow.SetTranslation(text);

            if (!_panelWindow.IsVisible)
            {
                _panelWindow.Show();
            }
        }
    }

    private void ModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _selectedRegion is not { } region || string.IsNullOrEmpty(_lastTranslation))
        {
            return;
        }

        ShowActiveOutput(_lastTranslation, region);
    }

    private void OverlayOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _overlayWindow.Opacity = e.NewValue;
        UpdateOverlayOpacityLabel();
    }

    private void InitializeTranslationTypography()
    {
        var installedFonts = Fonts.SystemFontFamilies
            .GroupBy(font => font.Source, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var previewOptions = PreferredTranslationFonts
            .Where(option => installedFonts.ContainsKey(option.FontFamilyName))
            .Select(option => new FontPreviewOption(
                option.DisplayName,
                installedFonts[option.FontFamilyName]))
            .ToArray();

        if (previewOptions.Length == 0)
        {
            previewOptions =
            [
                new FontPreviewOption("系统默认", new WpfFontFamily("Microsoft YaHei UI"))
            ];
        }

        TranslationFontFamilyComboBox.ItemsSource = previewOptions;
        TranslationFontFamilyComboBox.SelectedIndex = 0;

        ApplyTranslationTypography();
    }

    private void TranslationFontFamilyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyTranslationTypography();
    }

    private void TranslationFontSizeSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        ApplyTranslationTypography();
    }

    private void ApplyTranslationTypography()
    {
        if (TranslationFontFamilyComboBox is null ||
            TranslationFontSizeSlider is null ||
            TranslationFontSizeText is null)
        {
            return;
        }

        var fontFamily = TranslationFontFamilyComboBox.SelectedItem is FontPreviewOption selectedFont
            ? selectedFont.FontFamily.Source
            : "Microsoft YaHei UI";

        var fontSize = TranslationFontSizeSlider.Value;
        _overlayWindow.SetTypography(fontFamily, fontSize);
        _panelWindow.SetTypography(fontFamily, fontSize);
        TranslationFontSizeText.Text = $"{fontSize:0}";
    }

    private sealed record FontPreviewOption(string DisplayName, WpfFontFamily FontFamily);

    private void UpdateOverlayOpacityLabel()
    {
        if (OverlayOpacityText is not null)
        {
            OverlayOpacityText.Text = $"{OverlayOpacitySlider.Value:P0}";
        }
    }

    private string GetTargetLanguage()
    {
        var visibleText = TargetLanguageComboBox.Text.Trim();

        foreach (var option in TargetLanguageComboBox.Items.OfType<ComboBoxItem>())
        {
            if (option.Tag is not string tag)
            {
                continue;
            }

            var displayText = option.Content?.ToString();
            if (visibleText.Equals(displayText, StringComparison.CurrentCultureIgnoreCase) ||
                visibleText.Equals(tag, StringComparison.OrdinalIgnoreCase))
            {
                return tag;
            }
        }

        if (!string.IsNullOrWhiteSpace(visibleText))
        {
            return visibleText;
        }

        return TargetLanguageComboBox.SelectedItem is ComboBoxItem selectedItem &&
               selectedItem.Tag is string selectedTag
            ? selectedTag
            : "zh-CN";
    }

    private TimeSpan GetSelectedInterval()
    {
        if (IntervalComboBox.SelectedItem is ComboBoxItem item &&
            item.Tag is string millisecondsText &&
            int.TryParse(millisecondsText, out var milliseconds))
        {
            return TimeSpan.FromMilliseconds(milliseconds);
        }

        return TimeSpan.FromSeconds(1);
    }

    private void PlacePanelNextTo(ScreenRegion region)
    {
        var bounds = NativeWindowBehavior.PixelsToDips(region);
        var workArea = SystemParameters.WorkArea;
        var desiredLeft = bounds.Right + 12;

        _panelWindow.Left = desiredLeft + _panelWindow.Width <= workArea.Right
            ? desiredLeft
            : Math.Max(workArea.Left, bounds.Left - _panelWindow.Width - 12);
        _panelWindow.Top = Math.Clamp(bounds.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - _panelWindow.Height));
    }

    private void ShowRegionIndicator(ScreenRegion region)
    {
        _regionIndicatorWindow.SetRegion(region);

        if (!_regionIndicatorWindow.IsVisible)
        {
            _regionIndicatorWindow.Show();
        }
    }

    private void SetStatus(string title, string detail, bool isError)
    {
        StatusText.Text = title;
        StatusDetailText.Text = detail;
        StatusDot.Fill = new SolidColorBrush(isError
            ? Color.FromRgb(255, 107, 107)
            : Color.FromRgb(88, 214, 141));
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _globalHotkeyManager.Pressed -= GlobalHotkeyManager_Pressed;
        _globalHotkeyManager.Dispose();
        _runCancellation?.Cancel();
        _overlayWindow.Close();
        _regionIndicatorWindow.Close();
        _panelWindow.AllowClose();
        _httpClient.Dispose();
        System.Windows.Application.Current.Shutdown();
    }
}
