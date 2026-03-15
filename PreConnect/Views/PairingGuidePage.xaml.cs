using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using PreConnect.Connectivity;
using QRCoder;
using Windows.Storage.Streams;

namespace PreConnect.Views;

public sealed partial class PairingGuidePage : Page
{
    private PairingGuideViewModel ViewModel { get; } = new();
    private PairingService? _pairingService;
    private string _pairEndpoint = string.Empty;
    private bool _initialized;

    public PairingGuidePage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is StartPageNavigationContext ctx)
        {
            _pairingService = ctx.PairingService;
            _pairEndpoint = ctx.Host.LocalEndpoint + "api/pair";

            if (!_initialized)
            {
                _initialized = true;
                ViewModel.SetStatus("准备就绪，请在 5 分钟内使用下方 PIN 完成配对。");
                if (_pairingService is not null)
                    await ViewModel.EnsureOfferAsync(_pairingService, _pairEndpoint);
            }
            ViewModel.SetDiscovering(true);
        }
    }

    private async void RefreshPin_Click(object sender, RoutedEventArgs e)
    {
        if (_pairingService is null) return;
        await ViewModel.EnsureOfferAsync(_pairingService, _pairEndpoint, forceNew: true);
    }
}

public sealed class PairingGuideViewModel : INotifyPropertyChanged
{
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private string _pinCode = "------";
    private string _qrPayload = string.Empty;
    private string _statusMessage = string.Empty;
    private string _pinExpiryText = string.Empty;
    private bool _isDiscovering = true;
    private BitmapImage? _qrCodeImage;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<PermissionItem> PermissionItems { get; } = new()
    {
        new PermissionItem("网络权限", "用于局域网广播、TLS 通信与同步", "\uE71B"),
        new PermissionItem("通知权限", "展示配对状态，帮助识别 PC 设备", "\uE77B"),
        new PermissionItem("硬件遥测", "仅在本地采集并通过已授权会话传出", "\uE9CA"),
    };

    public ObservableCollection<TroubleshootingItem> TroubleshootingSteps { get; } = new()
    {
        new TroubleshootingItem("同网段 / 时钟同步", "确保手机与 PC 在同一局域网，时钟偏差 < 2 分钟", "\uE8C7"),
        new TroubleshootingItem("PIN 错误", "确认 6 位 PIN 未过期且输入正确", "\uE730"),
        new TroubleshootingItem("防火墙 / 端口访问", "在 PC 防火墙中放行端口，必要时临时关闭", "\uE72C"),
    };

    public string PinCode
    {
        get => _pinCode;
        set => SetProperty(ref _pinCode, value);
    }

    public string QrPayload
    {
        get => _qrPayload;
        set => SetProperty(ref _qrPayload, value);
    }

    public BitmapImage? QrCodeImage
    {
        get => _qrCodeImage;
        set => SetProperty(ref _qrCodeImage, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string PinExpiryText
    {
        get => _pinExpiryText;
        set => SetProperty(ref _pinExpiryText, value);
    }

    public bool IsDiscovering
    {
        get => _isDiscovering;
        set => SetProperty(ref _isDiscovering, value);
    }

    public Visibility DiscoveringTextVisibility => IsDiscovering ? Visibility.Visible : Visibility.Collapsed;

    public async Task EnsureOfferAsync(PairingService pairingService, string pairEndpoint, bool forceNew = false)
    {
        PairingOffer offer;

        if (forceNew)
        {
            offer = pairingService.GenerateOffer("PreConnect", pairEndpoint);
        }
        else
        {
            offer = pairingService.EnsureOffer("PreConnect", pairEndpoint);
        }

        await UpdateOfferAsync(offer);
    }

    public async Task UpdateOfferAsync(PairingOffer offer)
    {
        var qr = await CreateQrCodeImageAsync(offer.QrPayload);
        await RunOnUiAsync(() =>
        {
            PinCode = offer.Pin;
            QrPayload = offer.QrPayload;
            PinExpiryText = $"有效至 {offer.ExpiresAtUtc.ToLocalTime():HH:mm:ss}";
            QrCodeImage = qr;
        });
    }

    public void SetDiscovering(bool value)
    {
        _ = RunOnUiAsync(() =>
        {
            IsDiscovering = value;
            OnPropertyChanged(nameof(DiscoveringTextVisibility));
        });
    }

    public void SetStatus(string message)
    {
        _ = RunOnUiAsync(() => StatusMessage = message);
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

    private static async Task<BitmapImage> CreateQrCodeImageAsync(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        var qr = new PngByteQRCode(data);
        var png = qr.GetGraphic(10);

        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);

        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        return image;
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

public sealed class PermissionItem
{
    public PermissionItem(string title, string description, string iconGlyph)
    {
        Title = title;
        Description = description;
        IconGlyph = iconGlyph;
    }

    public string Title { get; }
    public string Description { get; }
    public string IconGlyph { get; }
}

public sealed class TroubleshootingItem
{
    public TroubleshootingItem(string title, string description, string iconGlyph)
    {
        Title = title;
        Description = description;
        IconGlyph = iconGlyph;
    }

    public string Title { get; }
    public string Description { get; }
    public string IconGlyph { get; }
}