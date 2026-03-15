using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PreConnect.Connectivity;

public sealed class ConnectivitySettings
{
    public string ServiceName { get; set; } = "PreConnect";
    public string DeviceName { get; set; } = Environment.MachineName;
    public int HostPort { get; set; } = 5005;
    public int DiscoveryPort { get; set; } = 53530;
    public int BroadcastIntervalMs { get; set; } = 2000;
    public bool DiscoveryEnabled { get; set; } = true;
    public string DeviceId { get; set; } = Guid.NewGuid().ToString();
}

public sealed class ConnectivitySettingsStore
{
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _settingsFile;

    public ConnectivitySettingsStore(string? basePath = null)
    {
        var root = basePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PreConnect");
        Directory.CreateDirectory(root);
        _settingsFile = Path.Combine(root, "connectivity-settings.json");
    }

    public async Task<ConnectivitySettings> LoadAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!File.Exists(_settingsFile))
            {
                var defaults = new ConnectivitySettings();
                await SaveInternalAsync(defaults).ConfigureAwait(false);
                return defaults;
            }

            await using var stream = File.OpenRead(_settingsFile);
            var settings = await JsonSerializer.DeserializeAsync<ConnectivitySettings>(stream, _options).ConfigureAwait(false)
                           ?? new ConnectivitySettings();

            // 兼容旧版本配置文件：若 DeviceId 为空则补全并回写，确保 ID 持久稳定
            if (string.IsNullOrWhiteSpace(settings.DeviceId))
            {
                settings.DeviceId = Guid.NewGuid().ToString();
                await SaveInternalAsync(settings).ConfigureAwait(false);
            }

            return settings;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(ConnectivitySettings settings)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            await SaveInternalAsync(settings).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SaveInternalAsync(ConnectivitySettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
        await using var stream = File.Create(_settingsFile);
        await JsonSerializer.SerializeAsync(stream, settings, _options).ConfigureAwait(false);
    }
}
