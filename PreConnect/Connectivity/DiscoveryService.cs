using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PreConnect.Connectivity;

public sealed class DiscoveryService : IAsyncDisposable
{
    private const string BroadcastAddress = "255.255.255.255";
    private const string MulticastAddress = "224.0.0.251";
    private readonly ConnectivitySettingsStore _settingsStore;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _announceTask;
    private UdpClient? _udp;
    private volatile bool _broadcastEnabled = true;
    private string _localDeviceId = string.Empty;

    public DiscoveryService(ConnectivitySettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public async Task StartAsync()
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cts is not null)
            {
                return; // already running
            }

            var settings = await _settingsStore.LoadAsync().ConfigureAwait(false);
            if (!settings.DiscoveryEnabled)
            {
                return;
            }

            _localDeviceId = settings.DeviceId;

            _cts = new CancellationTokenSource();

            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, settings.DiscoveryPort));
            _udp.EnableBroadcast = true;
            _udp.MulticastLoopback = false;
            _udp.JoinMulticastGroup(IPAddress.Parse(MulticastAddress));

            _broadcastEnabled = true;
            var token = _cts.Token;
            _announceTask = Task.Run(() => AnnounceLoopAsync(settings, token), token);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task StopAsync()
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cts is null)
            {
                return;
            }

            _cts.Cancel();
            _udp?.Close();

            if (_announceTask is not null)
            {
                try { await _announceTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }

            _udp?.Dispose();
            _udp = null;
            _cts.Dispose();
            _cts = null;
            _announceTask = null;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task AnnounceLoopAsync(ConnectivitySettings settings, CancellationToken token)
    {
        if (_udp is null) return;
        var multicastEndpoint = new IPEndPoint(IPAddress.Parse(MulticastAddress), settings.DiscoveryPort);
        var broadcastEndpoint = new IPEndPoint(IPAddress.Parse(BroadcastAddress), settings.DiscoveryPort);

        while (!token.IsCancellationRequested)
        {
            if (_broadcastEnabled)
            {
                var localIp = GetLocalIPAddress()?.ToString() ?? string.Empty;
                var endpoint = $"http://{localIp}:{settings.HostPort}/";
                var payload = JsonSerializer.Serialize(new
                {
                    type = "preconnect/announce",
                    deviceId = _localDeviceId,
                    name = settings.DeviceName,
                    port = settings.HostPort,
                    service = settings.ServiceName,
                    endpoint
                });
                var bytes = Encoding.UTF8.GetBytes(payload);

                try { await _udp.SendAsync(bytes, bytes.Length, multicastEndpoint).ConfigureAwait(false); }
                catch { /* 忽略瞬时发送错误 */ }
                try { await _udp.SendAsync(bytes, bytes.Length, broadcastEndpoint).ConfigureAwait(false); }
                catch { /* 忽略瞬时发送错误 */ }
            }

            try { await Task.Delay(settings.BroadcastIntervalMs, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void PauseAnnouncements() => _broadcastEnabled = false;
    public void ResumeAnnouncements() => _broadcastEnabled = true;

    private static IPAddress? GetLocalIPAddress()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("1.1.1.1", 65530);
            return ((IPEndPoint)s.LocalEndPoint!).Address;
        }
        catch
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(StopAsync());
    }
}
