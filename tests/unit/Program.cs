using System.Net;
using System.Text.Json;
using spp;

int passed = 0;
string directory = Path.Combine(Path.GetTempPath(), "spp-test-" + Guid.NewGuid());
var now = DateTimeOffset.UtcNow;
const string scope = "solo:v0.111.0";
string payload = JsonSerializer.Serialize(new
{
    entity_type = "cards",
    bracket = scope,
    rows = new[] { new { id = "BASH", upgraded = false, picks = 200, wins = 50, offered = 1000, picked = 100, pick_rate = 99, pick_rate_by_act = new double?[] { 12, null, 0 } } }
});

try
{
    var data = Statistics.Parse(payload, "cards", scope);
    Check(data.Items["BASH"].PickRate == 10 && data.Items["BASH"].WinRate == 25, "derive rates from the correct denominators");
    Reject(() => Statistics.Parse(payload, "cards", "solo:a10"), "reject a mismatched data bracket");
    Reject(() => Statistics.Parse(payload, "relics", scope), "reject mismatched entity data");
    Reject(() => Statistics.Parse("<html>error</html>", "cards", scope), "reject non-JSON responses");
    Reject(() => Statistics.Parse(payload.Replace("\"picks\":200", "\"picks\":-1"), "cards", scope), "reject negative counts");
    var zero = Statistics.Parse(payload.Replace("\"offered\":1000", "\"offered\":0").Replace("\"picked\":100", "\"picked\":0"), "cards", scope);
    Check(zero.Items["BASH"].PickRate == null, "missing offers do not become zero percent");
    string text = StatsText.Render(new(data, now, false, false), "BASH", false, 2, scope);
    Check(text.Contains("Act 3 pick") && text.Contains("0.0%"), "keep measured zero percentages");
    Check(!StatsText.Render(new(data, now, false, false), "BASH", false, 1, scope).Contains("Act 2 pick"), "omit unknown act rates");
    Check(StatsText.Render(new(data, now, false, true), "BASH", true, 0, scope).Contains("Cached"), "label offline snapshots");
    Check(StatsText.Render(new(data, now, false, false), "UNKNOWN", false, 0, scope).Contains("No samples"), "unknown items do not receive fabricated statistics");
    Check(!StatsText.Render(new(null, null, false, true), "BASH", false, 0, scope).Contains("0.0%"), "offline without cache is unavailable");

    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var handler = new Stub(async _ => { await gate.Task; return Json(payload); });
    using (var cache = new StatsCache(directory, new HttpClient(handler), () => now))
    {
        for (int i = 0; i < 100; i++) cache.Get("cards", scope);
        gate.SetResult();
        var view = await Wait(cache);
        Check(handler.Calls == 1 && view.Data != null, "coalesce concurrent hover requests");
        Check(handler.LastUri?.Query.Contains("solo%3Av0.111.0", StringComparison.OrdinalIgnoreCase) == true, "send the exact version bracket");
        await Until(() => Directory.Exists(directory) && Directory.GetFiles(directory, "*.json").Length == 1);
        for (int i = 0; i < 20; i++) cache.Get("cards", scope);
        Check(handler.Calls == 1, "reuse fresh statistics without network traffic");
        handler.Response = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        now += TimeSpan.FromHours(7);
        view = await Wait(cache);
        Check(view.Offline && view.Data?.Items["BASH"].WinRate == 25, "failed refresh preserves the last good snapshot");
        int calls = handler.Calls;
        cache.Get("cards", scope);
        Check(handler.Calls == calls, "back off after a network failure");
    }
    now -= TimeSpan.FromHours(7);
    var offline = new Stub(_ => throw new HttpRequestException("offline"));
    using (var cache = new StatsCache(directory, new HttpClient(offline), () => now))
    {
        var view = await Wait(cache);
        Check(view.Data != null && offline.Calls == 0, "load a fresh disk cache without contacting the service");
    }
    foreach (string file in Directory.GetFiles(directory, "*.json")) File.WriteAllText(file, "corrupt");
    using (var cache = new StatsCache(directory, new HttpClient(new Stub(_ => Task.FromResult(Json(payload)))), () => now))
        Check((await Wait(cache)).Data != null, "recover from a corrupt cache");
    var limited = new Stub(_ =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(1));
        return Task.FromResult(response);
    });
    using (var cache = new StatsCache(directory + "-limited", new HttpClient(limited), () => now))
    {
        Check((await Wait(cache)).Offline, "handle rate limiting without throwing");
        now += TimeSpan.FromMinutes(30);
        cache.Get("cards", scope);
        Check(limited.Calls == 1, "honor Retry-After");
    }
    Console.WriteLine($"PASS {passed} statistics and cache checks");
}
finally
{
    if (Directory.Exists(directory)) Directory.Delete(directory, true);
}

void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    passed++;
}

void Reject(Action action, string name)
{
    try { action(); }
    catch (JsonException) { Check(true, name); return; }
    throw new InvalidOperationException(name);
}

static HttpResponseMessage Json(string payload) => new(HttpStatusCode.OK) { Content = new StringContent(payload) };

static async Task Until(Func<bool> condition)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (!condition()) await Task.Delay(10, timeout.Token);
}

static async Task<CacheView> Wait(StatsCache cache)
{
    CacheView view = cache.Get("cards", scope);
    await Until(() => { view = cache.Get("cards", scope); return !view.Loading; });
    return view;
}

sealed class Stub(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
{
    internal Func<HttpRequestMessage, Task<HttpResponseMessage>> Response = response;
    internal int Calls;
    internal Uri? LastUri;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        LastUri = request.RequestUri;
        return Response(request);
    }
}
