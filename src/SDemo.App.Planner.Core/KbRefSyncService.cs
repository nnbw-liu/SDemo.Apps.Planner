using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SDemo.App.Planner.Storage;

namespace SDemo.App.Planner.Core;

/// <summary>
/// 把 Planner 的跨应用链接登记到 KB（`/v1/refs`），让「影响面」和「引用保护」在待办上真正成立。
/// 不引用 KB 工程，只走回环 HTTP；token 与端口从 KB 的运行期文件读取，环境变量可覆盖。
/// </summary>
public sealed class KbRefSyncService : IDisposable
{
    private const string StateKey = "kb.refs.state";
    private const string AppId = "sdemo.planner";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string RuntimeFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SDemo", "Hub", "apps", "sdemo.kb.json");
    private static readonly string TokenFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SDemo", "Hub", "apps", "sdemo.kb", "tokens", AppId + ".token");

    private readonly PlannerStore _store;
    private readonly Action<string>? _log;
    private readonly HttpClient _http;
    private bool _warnedMissingConfig;

    public KbRefSyncService(PlannerStore store, Action<string>? log = null, HttpClient? http = null)
    {
        _store = store;
        _log = log;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    public async Task<KbRefSyncResult> SyncOnceAsync(CancellationToken cancellationToken = default)
    {
        var config = ResolveConfig();
        if (config is null)
        {
            if (!_warnedMissingConfig)
            {
                _warnedMissingConfig = true;
                _log?.Invoke("KB 引用同步跳过：未找到 sdemo.kb 运行期文件或 planner token。");
            }

            return new KbRefSyncResult(true, 0, 0, 0, "KB 未就绪");
        }

        _warnedMissingConfig = false;

        var desired = BuildDesired(out var skipped);
        var previous = LoadState();
        var plan = BuildPlan(desired, previous);

        if (plan.Additions.Count == 0 && plan.Removals.Count == 0)
        {
            return new KbRefSyncResult(false, 0, 0, skipped, null);
        }

        var added = 0;
        var removed = 0;
        var failures = 0;

        foreach (var (from, target) in plan.Additions)
        {
            if (await PostAsync(config.RefsUrl, new
            {
                source = new { appId = AppId, entityUri = from, projectionVersion = 1 },
                to = target.To,
                rel = target.Rel,
            }, config.Token, cancellationToken).ConfigureAwait(false))
            {
                added++;
            }
            else
            {
                failures++;
            }
        }

        foreach (var (from, target) in plan.Removals)
        {
            if (await PostAsync(config.RefsUrl.TrimEnd('/') + "/retract", new
            {
                source = new { appId = AppId, entityUri = from, projectionVersion = 1 },
                to = target.To,
                rel = target.Rel,
            }, config.Token, cancellationToken).ConfigureAwait(false))
            {
                removed++;
            }
            else
            {
                failures++;
            }
        }

        // 只有全部成功才推进快照；失败的下一轮重试（登记/撤销都是幂等的）。
        if (failures == 0)
        {
            SaveState(desired);
            _log?.Invoke($"KB 引用同步：+{added} -{removed}（来源 {desired.Count} 个）");
        }
        else
        {
            _log?.Invoke($"KB 引用同步部分失败：+{added} -{removed}，失败 {failures} 条，下轮重试。");
        }

        return new KbRefSyncResult(false, added, removed, skipped, failures == 0 ? null : $"{failures} 条失败");
    }

    public async Task RunAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await SyncOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"KB 引用同步异常：{ex.Message}");
            }

            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>只保留指向 KB 的链接；实体 URI 用 <c>sdemo.planner/{ownerType}/{id}</c>。</summary>
    private Dictionary<string, HashSet<KbRefTarget>> BuildDesired(out int skipped)
    {
        skipped = 0;
        var desired = new Dictionary<string, HashSet<KbRefTarget>>(StringComparer.Ordinal);

        foreach (var link in _store.GetAllLinks())
        {
            var uri = (link.Uri ?? string.Empty).Trim();
            if (!uri.StartsWith("sdemo.kb/doc/", StringComparison.Ordinal))
            {
                continue;
            }

            if (!uri.Contains('@'))
            {
                // 没有版本的引用登记进去会让影响面漂；先跳过并计入报告。
                skipped++;
                continue;
            }

            if (link.OwnerType is not ("todo" or "event") || string.IsNullOrWhiteSpace(link.OwnerId))
            {
                skipped++;
                continue;
            }

            var from = $"sdemo.planner/{link.OwnerType}/{link.OwnerId}";
            var rel = link.OwnerType == "todo" ? "based_on" : "mentions";

            if (!desired.TryGetValue(from, out var targets))
            {
                targets = [];
                desired[from] = targets;
            }

            targets.Add(new KbRefTarget(uri, rel));
        }

        return desired;
    }

    private Dictionary<string, HashSet<KbRefTarget>> LoadState()
    {
        var json = _store.GetSetting(StateKey);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, HashSet<KbRefTarget>>(StringComparer.Ordinal);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, List<KbRefTarget>>>(json, JsonOptions)
                ?? [];
            return parsed.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToHashSet(),
                StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, HashSet<KbRefTarget>>(StringComparer.Ordinal);
        }
    }

    private void SaveState(Dictionary<string, HashSet<KbRefTarget>> desired)
    {
        var serializable = desired.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.OrderBy(item => item.To).ToList(),
            StringComparer.Ordinal);

        _store.SetSettings(
            new Dictionary<string, string> { [StateKey] = JsonSerializer.Serialize(serializable, JsonOptions) },
            DateTime.UtcNow);
    }

    internal static KbRefPlan BuildPlan(
        Dictionary<string, HashSet<KbRefTarget>> desired,
        Dictionary<string, HashSet<KbRefTarget>> previous)
    {
        var additions = new List<(string From, KbRefTarget Target)>();
        var removals = new List<(string From, KbRefTarget Target)>();

        foreach (var from in desired.Keys.Union(previous.Keys, StringComparer.Ordinal))
        {
            desired.TryGetValue(from, out var wanted);
            previous.TryGetValue(from, out var existed);
            wanted ??= [];
            existed ??= [];

            foreach (var item in wanted.Except(existed))
            {
                additions.Add((from, item));
            }

            foreach (var item in existed.Except(wanted))
            {
                removals.Add((from, item));
            }
        }

        return new KbRefPlan(additions, removals);
    }

    private KbConfig? ResolveConfig()
    {
        var token = Environment.GetEnvironmentVariable("SDEMO_KB_TOKEN");
        if (string.IsNullOrWhiteSpace(token) && File.Exists(TokenFile))
        {
            token = File.ReadAllText(TokenFile).Trim();
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var refsUrl = Environment.GetEnvironmentVariable("SDEMO_KB_URL");
        if (string.IsNullOrWhiteSpace(refsUrl) && File.Exists(RuntimeFile))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(RuntimeFile));
                if (document.RootElement.TryGetProperty("refsUrl", out var url))
                {
                    refsUrl = url.GetString();
                }
                else if (document.RootElement.TryGetProperty("port", out var port))
                {
                    refsUrl = $"http://127.0.0.1:{port.GetInt32()}/v1/refs";
                }
            }
            catch (Exception)
            {
                // 运行期文件损坏时按未就绪处理。
            }
        }

        if (string.IsNullOrWhiteSpace(refsUrl))
        {
            return null;
        }

        return new KbConfig(refsUrl.TrimEnd('/'), token.Trim());
    }

    private async Task<bool> PostAsync(string url, object payload, string token, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            _log?.Invoke($"KB 引用同步 HTTP {(int)response.StatusCode}：{url}");
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _log?.Invoke($"KB 引用同步连接失败：{ex.Message}");
            return false;
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed record KbConfig(string RefsUrl, string Token);
}

public sealed record KbRefTarget(string To, string Rel);

public sealed record KbRefPlan(
    IReadOnlyList<(string From, KbRefTarget Target)> Additions,
    IReadOnlyList<(string From, KbRefTarget Target)> Removals);

public sealed record KbRefSyncResult(
    bool Skipped,
    int Added,
    int Removed,
    int SkippedLinks,
    string? Message);

/// <summary>后台循环：启动即同步一次，之后每 30 秒一次；随宿主进程退出。</summary>
public sealed class KbRefSyncLoop : IDisposable
{
    private readonly KbRefSyncService _service;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _task;

    public KbRefSyncLoop(PlannerStore store, Action<string>? log = null, TimeSpan? interval = null)
    {
        _service = new KbRefSyncService(store, log);
        _task = Task.Run(() => _service.RunAsync(interval ?? TimeSpan.FromSeconds(30), _cts.Token));
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _task.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // 退出时的取消异常。
        }

        _service.Dispose();
        _cts.Dispose();
    }
}
