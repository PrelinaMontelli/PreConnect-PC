using System;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using PreConnect.BackEnd.LibreHardwareMonitor;
using PreConnect.Connectivity;

namespace PreConnect.BackEnd.Networking;

/// <summary>
/// Lightweight HTTP host to expose telemetry and pairing APIs.
/// Uses Kestrel to avoid HttpListener URL ACL permission issues on Windows.
/// </summary>
public sealed class DataProviderHost : IAsyncDisposable
{
    private readonly HardwareMonitorService _monitor;
    private readonly PairingService _pairingService;
    private readonly ConnectivitySettings _settings;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private WebApplication? _app;
    private int _activeConnections;

    public DataProviderHost(HardwareMonitorService monitor, PairingService pairingService, ConnectivitySettings settings, string prefix = "http://+:5005/")
    {
        _monitor = monitor;
        _pairingService = pairingService;
        _settings = settings;
        Prefix = prefix.EndsWith('/') ? prefix : prefix + "/";
        LocalEndpoint = ResolveLocalEndpoint(Prefix, settings.HostPort);
    }

    public string Prefix { get; }

    public string LocalEndpoint { get; }

    public bool IsRunning => _app is not null;

    public DateTimeOffset? LastRequestUtc { get; private set; }

    public int ActiveConnections => _activeConnections;

    public async Task StartAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_app is not null)
            {
                return;
            }

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseKestrel();
            builder.WebHost.UseUrls($"http://0.0.0.0:{_settings.HostPort}");

            var app = builder.Build();
            ConfigurePipeline(app);
            ConfigureRoutes(app);

            await app.StartAsync().ConfigureAwait(false);
            _app = app;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync()
    {
        WebApplication? appToStop;

        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            appToStop = _app;
            _app = null;
        }
        finally
        {
            _lifecycleLock.Release();
        }

        if (appToStop is null)
        {
            return;
        }

        try
        {
            await appToStop.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            await appToStop.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void ConfigurePipeline(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            _pairingService.PruneExpiredSessions();
            Interlocked.Increment(ref _activeConnections);
            LastRequestUtc = DateTimeOffset.UtcNow;

            try
            {
                await next().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _activeConnections);
            }
        });
    }

    private void ConfigureRoutes(WebApplication app)
    {
        app.MapGet("/api/ping", () =>
            WriteJsonResult(new { ok = true, name = "PreConnect", time = DateTimeOffset.UtcNow }));

        app.MapGet("/api/status", () =>
        {
            var payload = new
            {
                name = "PreConnect Data Provider",
                isRunning = IsRunning,
                endpoint = LocalEndpoint,
                activeConnections = _activeConnections,
                lastRequestUtc = LastRequestUtc,
                machineName = Environment.MachineName,
                os = Environment.OSVersion.ToString(),
                version = typeof(DataProviderHost).Assembly.GetName().Version?.ToString() ?? "1.0"
            };
            return WriteJsonResult(payload);
        });

        app.MapGet("/api/telemetry", (HttpContext context) =>
        {
            try
            {
                if (!TryGetAuthorizedSession(context, out var session, out var authError))
                {
                    return Results.Json(new { ok = false, error = authError }, statusCode: StatusCodes.Status401Unauthorized);
                }

                var snapshot = _monitor.GetSnapshot();
                return WriteJsonResult(new
                {
                    ok = true,
                    deviceId = session.DeviceId,
                    deviceName = session.DeviceName,
                    expiresAtUtc = session.ExpiresAtUtc,
                    snapshot
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message, type = ex.GetType().FullName }, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapPost("/api/pair", async (HttpContext context) =>
        {
            try
            {
                var request = await context.Request.ReadFromJsonAsync<PairRequest>(cancellationToken: context.RequestAborted).ConfigureAwait(false);
                if (request is null || string.IsNullOrWhiteSpace(request.Pin) || string.IsNullOrWhiteSpace(request.RotatingKey))
                {
                    return Results.BadRequest(new { ok = false, error = "Invalid request" });
                }

                var result = await _pairingService.CompleteHandshakeAsync(
                    request.Pin,
                    request.RotatingKey,
                    request.Name ?? "Unknown",
                    request.DeviceId ?? string.Empty).ConfigureAwait(false);

                if (result is null)
                {
                    return Results.BadRequest(new { ok = false, error = "PIN invalid or expired" });
                }

                return Results.Json(new PairResponse
                {
                    Ok = true,
                    SessionToken = result.SessionToken,
                    SessionTokenExpiresUtc = result.SessionTokenExpiresAtUtc,
                    ServerName = Environment.MachineName,
                    Endpoint = LocalEndpoint,
                    DeviceId = result.ClientDeviceId,
                    DeviceName = result.ClientName
                }, _jsonOptions);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
            }
        });
    }

    private IResult WriteJsonResult<T>(T payload)
    {
        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        return Results.Text(json, "application/json");
    }

    private bool TryGetAuthorizedSession(HttpContext context, out PairingSessionInfo session, out string error)
    {
        session = default;
        error = string.Empty;

        var token = ExtractSessionToken(context);
        if (!_pairingService.TryValidateSession(token, out session))
        {
            error = "Missing or invalid session token";
            return false;
        }

        return true;
    }

    private static string? ExtractSessionToken(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Session-Token", out var headerToken) && !string.IsNullOrWhiteSpace(headerToken))
        {
            return headerToken.ToString();
        }

        if (context.Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            var value = authHeader.ToString();
            const string bearerPrefix = "Bearer ";
            if (value.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return value[bearerPrefix.Length..].Trim();
            }
        }

        if (context.Request.Query.TryGetValue("sessionToken", out var queryToken) && !string.IsNullOrWhiteSpace(queryToken))
        {
            return queryToken.ToString();
        }

        return null;
    }

    private static string ResolveLocalEndpoint(string prefix, int fallbackPort)
    {
        if (!prefix.Contains('+') && !prefix.Contains('*'))
        {
            return prefix;
        }

        var port = fallbackPort;
        try
        {
            var testUri = new Uri(prefix.Replace("+", "localhost").Replace("*", "localhost"));
            port = testUri.Port;
        }
        catch
        {
            // ignore parse failure and use fallback port
        }

        var ip = GetLocalIPAddress();
        return $"http://{ip}:{port}/";
    }

    private static string GetLocalIPAddress()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("1.1.1.1", 65530);
            return ((IPEndPoint)s.LocalEndPoint!).Address.ToString();
        }
        catch
        {
            return "localhost";
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycleLock.Dispose();
    }
}

public sealed class PairRequest
{
    public string? Pin { get; set; }
    public string? RotatingKey { get; set; }
    public string? DeviceId { get; set; }
    public string? Name { get; set; }
}

public sealed class PairResponse
{
    public bool Ok { get; set; }
    public string SessionToken { get; set; } = string.Empty;
    public DateTimeOffset SessionTokenExpiresUtc { get; set; }
    public string ServerName { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
}
