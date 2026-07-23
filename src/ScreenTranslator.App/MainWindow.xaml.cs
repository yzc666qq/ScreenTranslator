using System.Net.Http;
using System.Threading.Channels;
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
    private const string CloudEndpoint = "https://api.deepseek.com/chat/completions";
    private const string LibreTranslateEndpoint = "http://127.0.0.1:5000";

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
    private readonly HttpClient _modelHttpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly TranslationOverlayWindow _overlayWindow = new();
    private readonly TranslationPanelWindow _panelWindow = new();
    private readonly RegionIndicatorWindow _regionIndicatorWindow = new();
    private readonly GlobalHotkeyManager _globalHotkeyManager = new();
    private readonly OpenAiCompatibleTranslationService _cloudTranslationService;
    private readonly QwenLocalTranslationService _integratedTranslationService;
    private readonly LlamaSharpLocalModelRuntime _integratedModelRuntime;
    private readonly LibreTranslateTranslationService _libreTranslateTranslationService;

    private ITextRecognizer? _textRecognizer;
    private ScreenRegion? _selectedRegion;
    private CancellationTokenSource? _startupCancellation;
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private string? _lastTranslation;
    private bool _isSelectingRegion;
    private bool _isStartingTranslation;
    private bool _providerUiReady;
    private TranslationEngineKind _selectedEngine = TranslationEngineKind.IntegratedQwen;
    private string _cloudEndpoint = CloudEndpoint;
    private string _libreTranslateEndpoint = LibreTranslateEndpoint;

    public MainWindow()
    {
        InitializeComponent();
        _cloudTranslationService = new OpenAiCompatibleTranslationService(_httpClient);
        _integratedModelRuntime = new LlamaSharpLocalModelRuntime(
            new LocalModelStore(_modelHttpClient));
        _integratedTranslationService = new QwenLocalTranslationService(_integratedModelRuntime);
        _libreTranslateTranslationService = new LibreTranslateTranslationService(_httpClient);
        InitializeTranslationTypography();
        UpdateTranslationEngineUi();

        SourceInitialized += MainWindow_SourceInitialized;
        Closed += MainWindow_Closed;

        var environmentKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if (!string.IsNullOrWhiteSpace(environmentKey))
        {
            ApiKeyPasswordBox.Password = environmentKey;

            if (_selectedEngine == TranslationEngineKind.OpenAiCompatible)
            {
                StatusDetailText.Text = "已从 DEEPSEEK_API_KEY 读取密钥";
            }
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

    private void TranslationEngineComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateTranslationEngineUi();
    }

    private void UpdateTranslationEngineUi()
    {
        if (TranslationEngineComboBox is null ||
            EndpointPanel is null ||
            EndpointTextBox is null ||
            CloudOptionsPanel is null ||
            ProviderHintText is null)
        {
            return;
        }

        if (_providerUiReady)
        {
            if (_selectedEngine == TranslationEngineKind.ExternalLibreTranslate)
            {
                _libreTranslateEndpoint = EndpointTextBox.Text.Trim();
            }
            else if (_selectedEngine == TranslationEngineKind.OpenAiCompatible)
            {
                _cloudEndpoint = EndpointTextBox.Text.Trim();
            }
        }

        _selectedEngine = GetSelectedTranslationEngine();
        var isIntegrated = _selectedEngine == TranslationEngineKind.IntegratedQwen;
        EndpointPanel.Visibility = isIntegrated ? Visibility.Collapsed : Visibility.Visible;
        CloudOptionsPanel.Visibility = _selectedEngine == TranslationEngineKind.OpenAiCompatible
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!isIntegrated)
        {
            EndpointTextBox.Text = _selectedEngine == TranslationEngineKind.ExternalLibreTranslate
                ? _libreTranslateEndpoint
                : _cloudEndpoint;
        }

        ProviderHintText.Text = _selectedEngine switch
        {
            TranslationEngineKind.IntegratedQwen =>
                "无需 API 密钥或后台服务；首次自动下载约 469 MB 模型，之后完全离线。",
            TranslationEngineKind.ExternalLibreTranslate =>
                "高级选项：连接用户自行维护的 LibreTranslate 服务。",
            _ => "也可使用 DEEPSEEK_API_KEY；仅在识别文本变化时请求模型。"
        };
        _providerUiReady = true;
    }

    private TranslationEngineKind GetSelectedTranslationEngine()
    {
        var tag = (TranslationEngineComboBox.SelectedItem as ComboBoxItem)?.Tag as string;

        return tag?.ToLowerInvariant() switch
        {
            "qwen-local" => TranslationEngineKind.IntegratedQwen,
            "libretranslate" => TranslationEngineKind.ExternalLibreTranslate,
            _ => TranslationEngineKind.OpenAiCompatible
        };
    }

    private async void SelectRegionButton_Click(object sender, RoutedEventArgs e)
    {
        await SelectRegionAsync();
    }

    private async Task SelectRegionAsync()
    {
        if (_isSelectingRegion || _isStartingTranslation)
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
                    await StartTranslationAsync();
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
                await StartTranslationAsync();
            }
            else
            {
                SetStatus("区域已选择", "配置翻译引擎后即可开始实时翻译", isError: false);
            }
        }
        finally
        {
            _isSelectingRegion = false;
        }
    }

    private async void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isStartingTranslation)
        {
            return;
        }

        if (_runCancellation is not null)
        {
            await StopTranslationAsync();
            return;
        }

        await StartTranslationAsync();
    }

    private async Task StartTranslationAsync()
    {
        if (_isStartingTranslation)
        {
            return;
        }

        _isStartingTranslation = true;

        try
        {
            if (_selectedRegion is not { } region)
            {
                SetStatus("请先框选屏幕区域", "点击“框选区域”后拖动鼠标", isError: true);
                return;
            }

            var selectedEngine = GetSelectedTranslationEngine();
            Uri? endpoint = null;

            if (selectedEngine != TranslationEngineKind.IntegratedQwen &&
                (!Uri.TryCreate(EndpointTextBox.Text.Trim(), UriKind.Absolute, out endpoint) ||
                 (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp)))
            {
                SetStatus("接口地址无效", "请输入完整的 http 或 https 地址", isError: true);
                return;
            }

            var model = ModelTextBox.Text.Trim();
            var apiKey = ApiKeyPasswordBox.Password.Trim();

            if (selectedEngine == TranslationEngineKind.OpenAiCompatible &&
                (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(apiKey)))
            {
                SetStatus("模型或 API 密钥为空", "可输入密钥或设置 DEEPSEEK_API_KEY", isError: true);
                return;
            }

            ITranslationService translationService;

            try
            {
                if (selectedEngine == TranslationEngineKind.IntegratedQwen)
                {
                    _startupCancellation = new CancellationTokenSource();
                    SetStartupControlsEnabled(false);
                    var progress = new Progress<LocalModelProgress>(modelProgress =>
                        SetStatus(
                            modelProgress.Title,
                            modelProgress.Detail,
                            isError: false));
                    await _integratedTranslationService.InitializeAsync(
                        progress,
                        _startupCancellation.Token);
                    translationService = _integratedTranslationService;
                }
                else if (selectedEngine == TranslationEngineKind.ExternalLibreTranslate)
                {
                    _libreTranslateTranslationService.Configure(endpoint!);
                    _startupCancellation = new CancellationTokenSource();
                    SetStartupControlsEnabled(false);
                    SetStatus(
                        "正在检查本地翻译服务",
                        "验证服务连接和目标语言模型…",
                        isError: false);

                    var readiness = await _libreTranslateTranslationService.CheckReadinessAsync(
                        GetTargetLanguage(),
                        _startupCancellation.Token);

                    if (!readiness.IsReady)
                    {
                        var status = readiness.State ==
                                     TranslationServiceReadinessState.TargetLanguageUnavailable
                            ? "缺少目标语言模型"
                            : "本地翻译服务未就绪";
                        SetStatus(status, readiness.Message, isError: true);
                        return;
                    }

                    translationService = _libreTranslateTranslationService;
                }
                else
                {
                    _cloudTranslationService.Configure(
                        new TranslationProviderOptions(endpoint!, model, apiKey));
                    translationService = _cloudTranslationService;
                }

                _textRecognizer ??= new WindowsOcrTextRecognizer();
            }
            catch (OperationCanceledException) when (_startupCancellation?.IsCancellationRequested == true)
            {
                return;
            }
            catch (Exception exception)
            {
                var title = selectedEngine == TranslationEngineKind.IntegratedQwen
                    ? "内置离线模型准备失败"
                    : "翻译引擎初始化失败";
                SetStatus(title, exception.Message, isError: true);
                return;
            }

            _lastTranslation = null;
            _runCancellation = new CancellationTokenSource();
            StartStopButton.Content = "停止实时翻译";
            SelectRegionButton.IsEnabled = false;
            TranslationEngineComboBox.IsEnabled = false;
            SetStatus("正在识别所选区域", "只在文字变化时调用翻译接口", isError: false);
            ShowActiveOutput("正在识别…", region);

            _runTask = RunTranslationLoopAsync(
                region,
                translationService,
                selectedEngine,
                _runCancellation.Token);
        }
        finally
        {
            _startupCancellation?.Dispose();
            _startupCancellation = null;
            _isStartingTranslation = false;
            StartStopButton.IsEnabled = true;

            if (_runCancellation is null)
            {
                SelectRegionButton.IsEnabled = true;
                TranslationEngineComboBox.IsEnabled = true;
            }
        }
    }

    private void SetStartupControlsEnabled(bool isEnabled)
    {
        StartStopButton.IsEnabled = isEnabled;
        SelectRegionButton.IsEnabled = isEnabled;
        TranslationEngineComboBox.IsEnabled = isEnabled;
    }

    private async Task RunTranslationLoopAsync(
        ScreenRegion region,
        ITranslationService translationService,
        TranslationEngineKind translationEngine,
        CancellationToken cancellationToken)
    {
        var interval = GetSelectedInterval();
        var languageDetection = translationEngine switch
        {
            TranslationEngineKind.IntegratedQwen => "源语言：由内置 Qwen 模型自动检测",
            TranslationEngineKind.ExternalLibreTranslate => "源语言：由外部离线服务自动检测",
            _ => "源语言：由模型根据正文自动检测"
        };

        var workQueue = Channel.CreateBounded<TranslationWork>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        using var coordinator = new LatestTranslationCoordinator();
        var recognitionTask = RunRecognitionLoopAsync(
            region,
            interval,
            languageDetection,
            coordinator,
            workQueue.Writer,
            cancellationToken);
        var translationTask = RunTranslationWorkerAsync(
            region,
            translationService,
            interval,
            languageDetection,
            coordinator,
            workQueue.Reader,
            cancellationToken);

        await Task.WhenAll(recognitionTask, translationTask);
    }

    private async Task RunRecognitionLoopAsync(
        ScreenRegion region,
        TimeSpan interval,
        string languageDetection,
        LatestTranslationCoordinator coordinator,
        ChannelWriter<TranslationWork> writer,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var frame = await _screenCaptureService.CaptureAsync(region, cancellationToken);
                    var recognized = await _textRecognizer!.RecognizeAsync(frame, cancellationToken);

                    if (string.IsNullOrWhiteSpace(recognized.Text))
                    {
                        if (_lastTranslation is null)
                        {
                            SetStatus("未识别到文字", "继续监视所选区域", isError: false);
                        }
                    }
                    else
                    {
                        var targetLanguage = GetTargetLanguage();

                        if (coordinator.TryObserve(recognized.Text, targetLanguage, out var key))
                        {
                            writer.TryWrite(new TranslationWork(
                                key,
                                recognized.Text,
                                targetLanguage));
                            SetStatus("发现文字变化，正在翻译", languageDetection, isError: false);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    SetStatus("本轮识别失败，将自动重试", exception.Message, isError: true);
                }

                if (!await timer.WaitForNextTickAsync(cancellationToken))
                {
                    break;
                }
            }
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task RunTranslationWorkerAsync(
        ScreenRegion region,
        ITranslationService translationService,
        TimeSpan interval,
        string languageDetection,
        LatestTranslationCoordinator coordinator,
        ChannelReader<TranslationWork> reader,
        CancellationToken cancellationToken)
    {
        await foreach (var work in reader.ReadAllAsync(cancellationToken))
        {
            var operation = coordinator.TryBegin(work.Key, cancellationToken);
            if (operation is null)
            {
                continue;
            }

            try
            {
                var translated = await translationService.TranslateAsync(
                    work.SourceText,
                    "auto",
                    work.TargetLanguage,
                    operation.Token);

                cancellationToken.ThrowIfCancellationRequested();

                if (!coordinator.CanPublish(work.Key))
                {
                    continue;
                }

                _lastTranslation = translated.TranslatedText;
                ShowActiveOutput(translated.TranslatedText, region);
                SetStatus(
                    "实时翻译运行中",
                    $"{languageDetection} · 更新：{DateTime.Now:HH:mm:ss}",
                    isError: false);
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested)
            {
                // A newer OCR result superseded this request, or the run was stopped.
            }
            catch (Exception exception)
            {
                coordinator.AllowRetry(work.Key);
                SetStatus("本轮翻译失败，将自动重试", exception.Message, isError: true);
                await Task.Delay(
                    TimeSpan.FromMilliseconds(Math.Max(3000, interval.TotalMilliseconds)),
                    cancellationToken);
            }
            finally
            {
                coordinator.End(operation);
            }
        }
    }

    private async Task StopTranslationAsync()
    {
        _startupCancellation?.Cancel();
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
        TranslationEngineComboBox.IsEnabled = true;

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

    private sealed record TranslationWork(
        TranslationRequestKey Key,
        string SourceText,
        string TargetLanguage);

    private sealed record FontPreviewOption(string DisplayName, WpfFontFamily FontFamily);

    private enum TranslationEngineKind
    {
        OpenAiCompatible,
        IntegratedQwen,
        ExternalLibreTranslate
    }

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
        _startupCancellation?.Cancel();
        _runCancellation?.Cancel();
        _overlayWindow.Close();
        _regionIndicatorWindow.Close();
        _panelWindow.AllowClose();
        _integratedModelRuntime.Dispose();
        _httpClient.Dispose();
        _modelHttpClient.Dispose();
        System.Windows.Application.Current.Shutdown();
    }
}
