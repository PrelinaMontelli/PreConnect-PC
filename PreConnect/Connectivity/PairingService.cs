using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace PreConnect.Connectivity;

public sealed class PairingOffer
{
    public string Pin { get; set; } = string.Empty;
    public string PairEndpoint { get; set; } = string.Empty;
    public string RotatingKey { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public string QrPayload { get; set; } = string.Empty;
}

public sealed class PairingResult
{
    public string ClientName { get; set; } = string.Empty;
    public string ClientDeviceId { get; set; } = string.Empty;
    public string SessionToken { get; set; } = string.Empty;
    public DateTimeOffset SessionTokenExpiresAtUtc { get; set; }
}

public sealed class PairingService : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ActiveSession> _sessions = new(StringComparer.Ordinal);
    private PairingSession? _current;
    private readonly string _sessionsFilePath;

    private static readonly JsonSerializerOptions _persistJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public PairingService()
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PreConnect");
        Directory.CreateDirectory(dataDir);
        _sessionsFilePath = Path.Combine(dataDir, "sessions.json");
        LoadPersistedSessions();
    }

    public PairingOffer GenerateOffer(string deviceName, string pairEndpoint, TimeSpan? lifetime = null)
    {
        lifetime ??= TimeSpan.FromMinutes(5);
        _current?.Dispose();

        var pin = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var rotatingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var expires = DateTimeOffset.UtcNow.Add(lifetime.Value);

        var offer = new PairingOffer
        {
            Pin = pin,
            PairEndpoint = pairEndpoint,
            RotatingKey = rotatingKey,
            ExpiresAtUtc = expires,
            QrPayload = JsonSerializer.Serialize(new
            {
                type = "preconnect/pair",
                server = deviceName,
                endpoint = pairEndpoint,
                pin,
                rotatingKey,
                expires = expires
            })
        };

        _current = new PairingSession(offer);
        return offer;
    }

    public PairingOffer EnsureOffer(string deviceName, string pairEndpoint)
    {
        if (_current is null || _current.IsExpired)
        {
            return GenerateOffer(deviceName, pairEndpoint);
        }

        return _current.Offer;
    }

    public Task<PairingResult?> CompleteHandshakeAsync(string providedPin, string providedRotatingKey, string remoteName, string remoteDeviceId)
    {
        if (_current is null || _current.IsExpired)
        {
            return Task.FromResult<PairingResult?>(null);
        }

        if (!string.Equals(_current.Offer.Pin, providedPin, StringComparison.Ordinal))
        {
            return Task.FromResult<PairingResult?>(null);
        }

        if (!string.Equals(_current.Offer.RotatingKey, providedRotatingKey, StringComparison.Ordinal))
        {
            return Task.FromResult<PairingResult?>(null);
        }

        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(tokenBytes);
        var tokenExpires = DateTimeOffset.UtcNow.AddYears(100);

        _current.Dispose();
        _current = null;

        PairingResult result = new()
        {
            ClientName = string.IsNullOrWhiteSpace(remoteName) ? "Unknown" : remoteName,
            ClientDeviceId = string.IsNullOrWhiteSpace(remoteDeviceId) ? Guid.NewGuid().ToString() : remoteDeviceId,
            SessionToken = token,
            SessionTokenExpiresAtUtc = tokenExpires
        };

        _sessions[token] = new ActiveSession(result.ClientDeviceId, result.ClientName, tokenExpires);
        SavePersistedSessions();

        return Task.FromResult<PairingResult?>(result);
    }

    public bool TryValidateSession(string? sessionToken, out PairingSessionInfo session)
    {
        session = default!;
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return false;
        }

        if (!_sessions.TryGetValue(sessionToken, out var activeSession))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow > activeSession.ExpiresAtUtc)
        {
            _sessions.TryRemove(sessionToken, out _);
            return false;
        }

        session = new PairingSessionInfo(activeSession.DeviceId, activeSession.DeviceName, activeSession.ExpiresAtUtc);
        return true;
    }

    public void PruneExpiredSessions()
    {
        var now = DateTimeOffset.UtcNow;
        var anyRemoved = false;
        foreach (var entry in _sessions)
        {
            if (now > entry.Value.ExpiresAtUtc)
            {
                if (_sessions.TryRemove(entry.Key, out _))
                    anyRemoved = true;
            }
        }
        if (anyRemoved)
            SavePersistedSessions();
    }

    public ValueTask DisposeAsync()
    {
        _current?.Dispose();
        _current = null;
        _sessions.Clear();
        return ValueTask.CompletedTask;
    }

    private void LoadPersistedSessions()
    {
        try
        {
            if (!File.Exists(_sessionsFilePath))
                return;

            var json = File.ReadAllText(_sessionsFilePath);
            var entries = JsonSerializer.Deserialize<List<PersistedSessionEntry>>(json, _persistJsonOptions);
            if (entries is null)
                return;

            var now = DateTimeOffset.UtcNow;
            foreach (var e in entries)
            {
                if (!string.IsNullOrWhiteSpace(e.Token) && now <= e.ExpiresAtUtc)
                    _sessions[e.Token] = new ActiveSession(e.DeviceId, e.DeviceName, e.ExpiresAtUtc);
            }
        }
        catch
        {
            // 文件缺失或损坏时静默忽略，会话将在下次配对后重建
        }
    }

    private void SavePersistedSessions()
    {
        try
        {
            var entries = _sessions
                .Select(kv => new PersistedSessionEntry
                {
                    Token = kv.Key,
                    DeviceId = kv.Value.DeviceId,
                    DeviceName = kv.Value.DeviceName,
                    ExpiresAtUtc = kv.Value.ExpiresAtUtc
                })
                .ToList();

            var json = JsonSerializer.Serialize(entries, _persistJsonOptions);
            File.WriteAllText(_sessionsFilePath, json);
        }
        catch
        {
            // 持久化失败不影响主流程
        }
    }

    private sealed class PersistedSessionEntry
    {
        public string Token { get; init; } = string.Empty;
        public string DeviceId { get; init; } = string.Empty;
        public string DeviceName { get; init; } = string.Empty;
        public DateTimeOffset ExpiresAtUtc { get; init; }
    }

    private sealed record ActiveSession(string DeviceId, string DeviceName, DateTimeOffset ExpiresAtUtc);

    private sealed class PairingSession : IDisposable
    {
        public PairingSession(PairingOffer offer)
        {
            Offer = offer;
        }

        public PairingOffer Offer { get; }

        public bool IsExpired => DateTimeOffset.UtcNow > Offer.ExpiresAtUtc;

        public void Dispose()
        {
            // no-op for MVP
        }
    }
}

public readonly record struct PairingSessionInfo(string DeviceId, string DeviceName, DateTimeOffset ExpiresAtUtc);
