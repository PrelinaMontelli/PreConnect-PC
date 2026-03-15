using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PreConnect.BackEnd.LibreHardwareMonitor;
using PreConnect.BackEnd.Networking;
using PreConnect.Connectivity;
using PreConnect.Views;
using Microsoft.UI.Windowing;
using WinRT.Interop;
using Windows.Graphics;

namespace PreConnect;

public sealed partial class MainWindow : Window
{
    private readonly HardwareMonitorService _monitor;
    private readonly DataProviderHost _dataHost;
    private readonly ConnectivitySettingsStore _settingsStore;
    private readonly ConnectivitySettings _settings;
    private readonly PairingService _pairingService;
    private readonly DiscoveryService _discoveryService;
    private StartPageNavigationContext? _navContext;

    public MainWindow()
    {
        this.InitializeComponent();

        _monitor = new HardwareMonitorService();
        _settingsStore = new ConnectivitySettingsStore();
        _settings = _settingsStore.LoadAsync().GetAwaiter().GetResult();
        _pairingService = new PairingService();
        _discoveryService = new DiscoveryService(_settingsStore);
        _dataHost = new DataProviderHost(_monitor, _pairingService, _settings, $"http://+:{_settings.HostPort}/");

        AppWindow.Resize(new SizeInt32(960, 660));
        TrySetWindowIcon();

        _ = _discoveryService.StartAsync();

        Closed += OnClosed;

        if (RootNav.MenuItems is { Count: > 0 })
        {
            RootNav.SelectedItem = RootNav.MenuItems[0];
            NavigateTo("start");
        }
    }

    private void RootNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is null)
            return;

        NavigateTo(item.Tag.ToString());
    }

    private void NavigateTo(string? tag)
    {
        // 复用同一个 context 实例，避免每次导航重建
        _navContext ??= new StartPageNavigationContext(_monitor, _dataHost, _pairingService, _discoveryService);
        switch (tag)
        {
            case "start":
                ContentFrame.Navigate(typeof(StartPage), _navContext);
                break;
            case "pairing":
                ContentFrame.Navigate(typeof(PairingGuidePage), _navContext);
                break;
        }
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        await _dataHost.DisposeAsync();
        _monitor.Dispose();
        await _pairingService.DisposeAsync();
        await _discoveryService.DisposeAsync();
    }

    private void TrySetWindowIcon()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            var baseDir = System.AppContext.BaseDirectory;
            var iconCandidates = new[]
            {
                System.IO.Path.Combine(baseDir, "PreConnectApp.ico"),
                System.IO.Path.Combine(baseDir, "Assets", "PreConnectApp.ico")
            };

            foreach (var iconPath in iconCandidates)
            {
                if (System.IO.File.Exists(iconPath))
                {
                    appWindow.SetIcon(iconPath);
                    break;
                }
            }
        }
        catch
        {
            // Ignore icon setup failures to avoid blocking app startup.
        }
    }
}
