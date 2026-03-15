using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using PreConnect.BackEnd.LibreHardwareMonitor;
using PreConnect.BackEnd.Networking;
using PreConnect.Connectivity;

namespace PreConnect.Views;

public sealed partial class StartPage : Page
{
    private StartPageViewModel ViewModel { get; } = new();
    private DataProviderHost? _host;
    private DiscoveryService? _discoveryService;
    private bool _initialized;

    public StartPage()
    {
        InitializeComponent();
        // 初始化自启开关状态（不触发 Toggled 事件）
        AutoStartToggle.IsOn = AutoStartService.IsEnabled();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is StartPageNavigationContext ctx)
        {
            _host = ctx.Host;
            _discoveryService = ctx.DiscoveryService;

            if (!_initialized)
            {
                _initialized = true;
                ViewModel.Endpoint = ctx.Host.LocalEndpoint + "api/telemetry";

                await _discoveryService.StartAsync();

                if (!ctx.Host.IsRunning)
                {
                    try
                    {
                        await ctx.Host.StartAsync();
                        ViewModel.SetMessage("数据服务已启动，等待移动端连接...");
                    }
                    catch (Exception ex)
                    {
                        ViewModel.SetMessage($"启动失败: {ex.Message}");
                    }
                }
            }

            await ViewModel.RefreshStatusAsync(ctx.Host);
        }
    }

    private async void StartService_Click(object sender, RoutedEventArgs e)
    {
        if (_host is null) return;
        if (_host.IsRunning) { ViewModel.SetMessage("服务已在运行中"); return; }
        try
        {
            await _host.StartAsync();
            await ViewModel.RefreshStatusAsync(_host);
            ViewModel.SetMessage("数据服务已启动");
        }
        catch (Exception ex) { ViewModel.SetMessage($"启动失败: {ex.Message}"); }
    }

    private async void StopService_Click(object sender, RoutedEventArgs e)
    {
        if (_host is null) return;
        await _host.StopAsync();
        await ViewModel.RefreshStatusAsync(_host);
        ViewModel.SetMessage("服务已停止");
    }

    private async void RefreshStatus_Click(object sender, RoutedEventArgs e)
    {
        if (_host is null) return;
        await ViewModel.RefreshStatusAsync(_host);
    }

    private void AutoStartToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (AutoStartToggle.IsOn)
        {
            var replaced = AutoStartService.Enable();
            ViewModel.IsAutoStartEnabled = true;
            ViewModel.AutoStartHint = replaced > 0
                ? $"已替换 {replaced} 个旧版本的自启条目"
                : string.Empty;
        }
        else
        {
            AutoStartService.Disable();
            ViewModel.IsAutoStartEnabled = false;
            ViewModel.AutoStartHint = string.Empty;
        }
    }
}

public sealed class StartPageNavigationContext
{
    public StartPageNavigationContext(
        HardwareMonitorService monitor,
        DataProviderHost host,
        PairingService pairingService,
        DiscoveryService discoveryService)
    {
        Monitor = monitor;
        Host = host;
        PairingService = pairingService;
        DiscoveryService = discoveryService;
    }

    public HardwareMonitorService Monitor { get; }
    public DataProviderHost Host { get; }
    public PairingService PairingService { get; }
    public DiscoveryService DiscoveryService { get; }
}

public sealed class StartPageViewModel : INotifyPropertyChanged
{
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly SolidColorBrush _runningBrush;
    private readonly SolidColorBrush _stoppedBrush;
    private bool _isRunning;
    private string _serviceStatus = "未运行";
    private string _endpoint = "http://localhost:5005/api/telemetry";
    private string _lastMessage = "准备就绪";
    private string _lastRequestTime = "—";
    private bool _isAutoStartEnabled;
    private string _autoStartHint = string.Empty;

    public StartPageViewModel()
    {
        _runningBrush = new SolidColorBrush(Colors.LightGreen);
        _stoppedBrush = new SolidColorBrush(Colors.Gray);
        _isAutoStartEnabled = AutoStartService.IsEnabled();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SolidColorBrush StatusDotBrush => _isRunning ? _runningBrush : _stoppedBrush;

    public string ServiceStatus
    {
        get => _serviceStatus;
        set => SetProperty(ref _serviceStatus, value);
    }

    public string Endpoint
    {
        get => _endpoint;
        set => SetProperty(ref _endpoint, value);
    }

    public string LastMessage
    {
        get => _lastMessage;
        set => SetProperty(ref _lastMessage, value);
    }

    public string LastRequestTime
    {
        get => _lastRequestTime;
        set => SetProperty(ref _lastRequestTime, value);
    }

    public bool IsAutoStartEnabled
    {
        get => _isAutoStartEnabled;
        set => SetProperty(ref _isAutoStartEnabled, value);
    }

    public string AutoStartHint
    {
        get => _autoStartHint;
        set => SetProperty(ref _autoStartHint, value);
    }

    public async Task RefreshStatusAsync(DataProviderHost host)
    {
        await RunOnUiAsync(() =>
        {
            var wasRunning = _isRunning;
            _isRunning = host.IsRunning;
            ServiceStatus = _isRunning ? "运行中" : "已停止";
            if (wasRunning != _isRunning)
                OnPropertyChanged(nameof(StatusDotBrush));
            Endpoint = host.LocalEndpoint + "api/telemetry";
            LastRequestTime = host.LastRequestUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "—";
        });
    }

    public void SetMessage(string message)
    {
        _ = RunOnUiAsync(() => LastMessage = message);
    }

    private Task RunOnUiAsync(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource();
        _dispatcher.TryEnqueue(() =>
        {
            try { action(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    private void SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(storage, value)) return;
        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

