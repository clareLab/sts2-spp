using System.Net;
using System.Text.Json;
using spp;

int passed = 0;
string directory = Path.Combine(Path.GetTempPath(), "spp-test-" + Guid.NewGuid());
var now = DateTimeOffset.UtcNow;
const string scope = "solo:a10:v0.111.0";
string payload = JsonSerializer.Serialize(new
{
    entity_type = "cards",
    bracket = scope,
    total_runs = 1000,
    rows = new[] { new { id = "BASH", upgraded = false, picks = 200, wins = 50, offered = 1000, picked = 100, pick_rate = 99, pick_rate_by_act = new double?[] { 12, null, 0 } } }
});
string population = JsonSerializer.Serialize(new { total_runs = 1000, total_wins = 200, by_ascension = new[] { new { ascension = 10, runs = 1000, wins = 200 } } });

try
{
    var data = Statistics.Parse(payload, "cards", scope).WithPopulation(population);
    Check(data.Items["BASH"].PickRate == 10 && data.Items["BASH"].WinRate == 25, "derive rates from the correct denominators");
    Check(data.BaselineWinRate == 20, "derive the baseline from overall runs rather than entity-weighted rates");
    Reject(() => data.WithPopulation(population.Replace("1000", "1001")), "reject mismatched baseline sample counts");
    Reject(() => data.WithPopulation(population.Replace("\"ascension\":10", "\"ascension\":0")), "reject a baseline from another ascension");
    Reject(() => Statistics.Parse(payload, "cards", "solo:a10"), "reject a mismatched data bracket");
    Reject(() => Statistics.Parse(payload, "cards", "solo:a5:v0.111.0"), "never show A10 statistics for another ascension");
    Reject(() => Statistics.Parse(payload, "cards", "2p:a10:v0.111.0"), "never mix solo and multiplayer statistics");
    Reject(() => Statistics.Parse(payload.Replace("solo:a10:", "solo:"), "cards", scope), "reject all-ascension data for an A10 request");
    Reject(() => Statistics.Parse(payload, "relics", scope), "reject mismatched entity data");
    Reject(() => Statistics.Parse("<html>error</html>", "cards", scope), "reject non-JSON responses");
    Reject(() => Statistics.Parse(payload.Replace("\"picks\":200", "\"picks\":-1"), "cards", scope), "reject negative counts");
    var zero = Statistics.Parse(payload.Replace("\"offered\":1000", "\"offered\":0").Replace("\"picked\":100", "\"picked\":0"), "cards", scope);
    Check(zero.Items["BASH"].PickRate == null, "missing offers do not become zero percent");
    string? text = StatsText.Render(new(data, now, false, false), "BASH", false, 2, scope);
    Check(text != null && text.Contains("Act 3 pick") && text.Contains("0.0") && text.Contains("\u00a0%"), "keep measured zero percentages");
    Check(text != null && text.Contains("A10") && text.Contains("Solo") && !text.Contains(" / "), "keep ascension and party size in separate cells");
    Check(text != null && text.Contains("Win rate Δ") && text.Contains("+5.0") && text.Contains("\u00a0pp") && !text.Contains("25.0"), "show percentage-point delta instead of absolute win rate");
    var equal = data with { BaselineWinRate = 25 };
    Check(StatsText.Render(new(equal, now, false, false), "BASH", false, 0, scope)?.Contains("]0.0[") == true, "keep neutral deltas unsigned");
    var lower = data with { BaselineWinRate = 27.34 };
    Check(StatsText.Render(new(lower, now, false, false), "BASH", false, 0, scope)?.Contains("−2.3") == true, "show a negative delta with aligned decimal precision");
    Check(StatsText.Render(new(data with { BaselineWinRate = null }, now, false, false), "BASH", false, 0, scope)?.Contains("Win rate") == false, "omit win delta without a valid baseline");
    Check(StatsText.Render(new(data, now, false, false), "BASH", false, 1, scope)?.Contains("Act 2 pick") == false, "omit unknown act rates");
    Check(StatsText.Render(new(data, now, false, true), "BASH", true, 0, scope)?.Contains("Cached") == true, "label offline snapshots");
    Check(StatsText.Render(new(data, now, false, false), "UNKNOWN", false, 0, scope) == null, "hide items without samples");
    Check(StatsText.Render(new(null, null, false, true), "BASH", false, 0, scope) == null, "hide unavailable statistics without cache");
    Check(StatsText.Render(new(null, null, true, false), "BASH", false, 0, scope) == null, "hide loading placeholders without samples");

    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var handler = new Stub(async request => { await gate.Task; return Json(request.RequestUri!.AbsolutePath.Contains("community-stats") ? population : payload); });
    using (var cache = new StatsCache(directory, new HttpClient(handler), () => now))
    {
        for (int i = 0; i < 100; i++) cache.Get("cards", scope);
        gate.SetResult();
        var view = await Wait(cache);
        Check(handler.Calls == 2 && view.Data?.BaselineWinRate == 20, "coalesce concurrent hover requests and cache the matching baseline");
        Check(handler.LastUri?.Query.Contains("solo%3Aa10%3Av0.111.0", StringComparison.OrdinalIgnoreCase) == true, "send the exact A10 and version bracket");
        await Until(() => Directory.Exists(directory) && Directory.GetFiles(directory, "*.json").Length == 1);
        for (int i = 0; i < 20; i++) cache.Get("cards", scope);
        Check(handler.Calls == 2, "reuse fresh statistics without network traffic");
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
    using (var cache = new StatsCache(directory, new HttpClient(new Stub(request => Task.FromResult(Json(request.RequestUri!.AbsolutePath.Contains("community-stats") ? population : payload)))), () => now))
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
    var mismatch = new Stub(request => Task.FromResult(Json(request.RequestUri!.AbsolutePath.Contains("community-stats") ? population.Replace("1000", "1001") : payload)));
    using (var cache = new StatsCache(directory + "-mismatch", new HttpClient(mismatch), () => now))
    {
        var view = await Wait(cache);
        Check(view.Offline && view.Data?.Items["BASH"].PickRate == 10 && view.Data.BaselineWinRate == null,
            "baseline failure preserves valid pick rates without inventing a win delta");
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
