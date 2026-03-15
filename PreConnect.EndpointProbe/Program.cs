using System.Net;
using System.Text;
using System.Text.Json;

try
{
	var options = CliOptions.Parse(args);
	if (options.ShowHelp)
	{
		CliOptions.PrintHelp();
		return 0;
	}

	Console.WriteLine("PreConnect Endpoint Probe");
	Console.WriteLine($"BaseUrl: {options.BaseUrl}");
	Console.WriteLine($"Timeout: {options.Timeout.TotalSeconds:F0}s");
	Console.WriteLine();

	using var http = new HttpClient { BaseAddress = options.BaseUrl, Timeout = options.Timeout };
	var results = new List<TestResult>();

	results.Add(await TestGetAsync(http, "/api/ping", "Ping"));
	results.Add(await TestGetAsync(http, "/api/status", "Status"));

	PairSuccess? pair = null;
	if (!string.IsNullOrWhiteSpace(options.Pin))
	{
		var pairResult = await TestPairAsync(http, options);
		results.Add(pairResult.Result);
		pair = pairResult.Pair;
	}
	else
	{
		results.Add(TestResult.Skip("Pair", "未提供 --pin，已跳过 /api/pair 测试。"));
		results.Add(TestResult.Skip("Telemetry", "未提供 --pin，已跳过配对后持续遥测。"));
	}

	if (pair is not null)
	{
		results.Add(await PollTelemetryAsync(http, pair, options));
	}

	Console.WriteLine("==================== 测试结果 ====================");
	foreach (var result in results)
	{
		var icon = result.Success ? "PASS" : result.Skipped ? "SKIP" : "FAIL";
		Console.WriteLine($"[{icon}] {result.Name}");
		if (!string.IsNullOrWhiteSpace(result.Message))
		{
			Console.WriteLine($"  {result.Message}");
		}
	}

	Console.WriteLine("=================================================");

	var hasFailure = results.Any(r => !r.Success && !r.Skipped);
	return hasFailure ? 1 : 0;
}
catch (Exception ex)
{
	Console.Error.WriteLine($"参数或运行错误: {ex.Message}");
	return 1;
}

static async Task<TestResult> TestGetAsync(HttpClient http, string path, string name)
{
	try
	{
		using var response = await http.GetAsync(path);
		var body = await response.Content.ReadAsStringAsync();
		var snippet = Shrink(body);

		if (!response.IsSuccessStatusCode)
		{
			return TestResult.Fail(name, $"HTTP {(int)response.StatusCode} {response.StatusCode}; 响应: {snippet}");
		}

		return TestResult.Ok(name, $"HTTP {(int)response.StatusCode}; 响应片段: {snippet}");
	}
	catch (Exception ex)
	{
		return TestResult.Fail(name, ex.Message);
	}
}

static async Task<PairAttemptResult> TestPairAsync(HttpClient http, CliOptions options)
{
	var payload = new
	{
		pin = options.Pin,
		deviceId = options.DeviceId,
		name = options.ClientName
	};

	try
	{
		var json = JsonSerializer.Serialize(payload);
		using var content = new StringContent(json, Encoding.UTF8, "application/json");
		using var response = await http.PostAsync("/api/pair", content);
		var body = await response.Content.ReadAsStringAsync();
		var snippet = Shrink(body);

		if (!response.IsSuccessStatusCode)
		{
			return new PairAttemptResult(TestResult.Fail("Pair", $"HTTP {(int)response.StatusCode} {response.StatusCode}; 响应: {snippet}"), null);
		}

		using var doc = JsonDocument.Parse(body);
		var root = doc.RootElement;
		var ok = root.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
		var endpoint = root.TryGetProperty("endpoint", out var ep) ? ep.GetString() : "";
		var token = root.TryGetProperty("sessionToken", out var tk) ? tk.GetString() : "";
		var expires = root.TryGetProperty("sessionTokenExpiresUtc", out var ex) ? ex.GetString() : "";

		if (!ok || string.IsNullOrWhiteSpace(token))
		{
			return new PairAttemptResult(TestResult.Fail("Pair", $"响应 ok=false 或缺少 token; 原始响应: {snippet}"), null);
		}

		var pair = new PairSuccess(endpoint ?? options.BaseUrl.ToString(), token!, expires ?? string.Empty);
		return new PairAttemptResult(TestResult.Ok("Pair", $"配对成功; endpoint={endpoint}; tokenLen={token?.Length ?? 0}; expires={expires}"), pair);
	}
	catch (Exception ex)
	{
		return new PairAttemptResult(TestResult.Fail("Pair", ex.Message), null);
	}
}

static async Task<TestResult> PollTelemetryAsync(HttpClient http, PairSuccess pair, CliOptions options)
{
	Console.WriteLine();
	Console.WriteLine("开始持续获取完整传感器数据...");

	for (var attempt = 1; attempt <= options.PollCount; attempt++)
	{
		try
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, "/api/telemetry");
			request.Headers.Add("X-Session-Token", pair.SessionToken);
			using var response = await http.SendAsync(request);
			var body = await response.Content.ReadAsStringAsync();

			if (!response.IsSuccessStatusCode)
			{
				return TestResult.Fail("Telemetry", $"第 {attempt} 次拉取失败: HTTP {(int)response.StatusCode} {response.StatusCode}; 响应: {Shrink(body)}");
			}

			PrintTelemetrySnapshot(body, attempt);
		}
		catch (Exception ex)
		{
			return TestResult.Fail("Telemetry", $"第 {attempt} 次拉取失败: {ex.Message}");
		}

		if (attempt < options.PollCount)
		{
			await Task.Delay(options.PollInterval);
		}
	}

	return TestResult.Ok("Telemetry", $"已成功连续获取 {options.PollCount} 次完整传感器数据，间隔 {options.PollInterval.TotalSeconds:F1}s。");
}

static void PrintTelemetrySnapshot(string body, int attempt)
{
	Console.WriteLine($"---- Telemetry #{attempt} ----");
	Console.WriteLine(body);

	try
	{
		using var doc = JsonDocument.Parse(body);
		var root = doc.RootElement;
		if (root.TryGetProperty("snapshot", out var snapshot) &&
			snapshot.TryGetProperty("components", out var components) &&
			components.ValueKind == JsonValueKind.Array)
		{
			var componentCount = components.GetArrayLength();
			var sensorCount = CountSensors(components);
			Console.WriteLine($"摘要: components={componentCount}, sensors={sensorCount}");
		}
	}
	catch
	{
		// raw JSON already printed
	}

	Console.WriteLine();
}

static int CountSensors(JsonElement components)
{
	var total = 0;
	foreach (var component in components.EnumerateArray())
	{
		if (component.TryGetProperty("sensors", out var sensors) && sensors.ValueKind == JsonValueKind.Array)
		{
			total += sensors.GetArrayLength();
		}

		if (component.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
		{
			total += CountSensors(children);
		}
	}

	return total;
}

static string Shrink(string value, int maxLength = 180)
{
	if (string.IsNullOrWhiteSpace(value))
	{
		return "<empty>";
	}

	var oneLine = value.Replace("\r", " ").Replace("\n", " ").Trim();
	if (oneLine.Length <= maxLength)
	{
		return oneLine;
	}

	return oneLine[..maxLength] + "...";
}

sealed record TestResult(string Name, bool Success, bool Skipped, string Message)
{
	public static TestResult Ok(string name, string message) => new(name, true, false, message);
	public static TestResult Fail(string name, string message) => new(name, false, false, message);
	public static TestResult Skip(string name, string message) => new(name, false, true, message);
}

sealed record PairAttemptResult(TestResult Result, PairSuccess? Pair);
sealed record PairSuccess(string Endpoint, string SessionToken, string ExpiresUtc);

sealed class CliOptions
{
	public Uri BaseUrl { get; private set; } = new("http://127.0.0.1:5005/");
	public string? Pin { get; private set; }
	public string ClientName { get; private set; } = "EndpointProbe";
	public string DeviceId { get; private set; } = Guid.NewGuid().ToString();
	public TimeSpan Timeout { get; private set; } = TimeSpan.FromSeconds(5);
	public int PollCount { get; private set; } = 5;
	public TimeSpan PollInterval { get; private set; } = TimeSpan.FromSeconds(2);
	public bool ShowHelp { get; private set; }

	public static CliOptions Parse(string[] args)
	{
		var options = new CliOptions();

		for (var i = 0; i < args.Length; i++)
		{
			var arg = args[i];
			switch (arg)
			{
				case "-h":
				case "--help":
					options.ShowHelp = true;
					return options;
				case "--base-url":
					options.BaseUrl = new Uri(ReadValue(args, ref i, arg));
					break;
				case "--pin":
					options.Pin = ReadValue(args, ref i, arg);
					break;
				case "--name":
					options.ClientName = ReadValue(args, ref i, arg);
					break;
				case "--device-id":
					options.DeviceId = ReadValue(args, ref i, arg);
					break;
				case "--timeout-seconds":
					if (!int.TryParse(ReadValue(args, ref i, arg), out var seconds) || seconds <= 0)
					{
						throw new ArgumentException("--timeout-seconds 必须是正整数");
					}

					options.Timeout = TimeSpan.FromSeconds(seconds);
					break;
				case "--poll-count":
					if (!int.TryParse(ReadValue(args, ref i, arg), out var pollCount) || pollCount <= 0)
					{
						throw new ArgumentException("--poll-count 必须是正整数");
					}

					options.PollCount = pollCount;
					break;
				case "--poll-interval-seconds":
					if (!double.TryParse(ReadValue(args, ref i, arg), out var intervalSeconds) || intervalSeconds <= 0)
					{
						throw new ArgumentException("--poll-interval-seconds 必须是正数");
					}

					options.PollInterval = TimeSpan.FromSeconds(intervalSeconds);
					break;
				default:
					throw new ArgumentException($"未知参数: {arg}");
			}
		}

		return options;
	}

	private static string ReadValue(string[] args, ref int index, string flag)
	{
		if (index + 1 >= args.Length)
		{
			throw new ArgumentException($"参数 {flag} 缺少值");
		}

		index++;
		return args[index];
	}

	public static void PrintHelp()
	{
		Console.WriteLine("用法:");
		Console.WriteLine("  dotnet run --project PreConnect.EndpointProbe -- --base-url http://127.0.0.1:5005/ [--pin 123456]");
		Console.WriteLine();
		Console.WriteLine("参数:");
		Console.WriteLine("  --base-url         主机服务根地址，默认 http://127.0.0.1:5005/");
		Console.WriteLine("  --pin              可选。提供后会测试 /api/pair");
		Console.WriteLine("  --name             可选。配对请求中的客户端名称，默认 EndpointProbe");
		Console.WriteLine("  --device-id        可选。配对请求中的设备 ID，默认随机 GUID");
		Console.WriteLine("  --timeout-seconds  可选。请求超时秒数，默认 5");
		Console.WriteLine("  --poll-count       可选。配对成功后遥测拉取次数，默认 5");
		Console.WriteLine("  --poll-interval-seconds  可选。遥测拉取间隔秒数，默认 2");
		Console.WriteLine("  -h, --help         查看帮助");
	}
}
