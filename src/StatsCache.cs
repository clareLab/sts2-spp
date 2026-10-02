using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace spp;

internal sealed record CacheView(Statistics? Data, DateTimeOffset? SavedAt, bool Loading, bool Offline);

internal sealed class StatsCache : IDisposable
{
    private sealed class Slot
    {
        internal CacheView View = new(null, null, false, false);
        internal DateTimeOffset NextAttempt;
        internal Task? Work;
        internal bool DiskRead;
    }

    private sealed record Stored(DateTimeOffset SavedAt, string Payload);
    private readonly ConcurrentDictionary<string, Slot> _slots = new();
    private readonly HttpClient _http;
    private readonly string _directory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly CancellationTokenSource _shutdown = new();
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);
    private const int MaxBytes = 4 * 1024 * 1024;

    internal StatsCache(string directory, HttpClient? client = null, Func<DateTimeOffset>? clock = null)
    {
        _directory = directory;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _http = client ?? new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All });
        _http.Timeout = TimeSpan.FromSeconds(12);
        _http.MaxResponseContentBufferSize = MaxBytes;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("StatsPlusPlus/0.1.3 (+https://github.com/clareLab/sts2-spp)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    internal CacheView Get(string kind, string bracket)
    {
        if (kind is not ("cards" or "relics")) throw new ArgumentOutOfRangeException(nameof(kind));
        var slot = _slots.GetOrAdd(kind + ":" + bracket, _ => new Slot());
        lock (slot)
        {
            if (!_shutdown.IsCancellationRequested && (slot.Work == null || slot.Work.IsCompleted) && _clock() >= slot.NextAttempt)
            {
                slot.View = slot.View with { Loading = true };
                slot.Work = Task.Run(() => Load(slot, kind, bracket));
            }
            return slot.View;
        }
    }

    private async Task Load(Slot slot, string kind, string bracket)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + ":" + bracket)));
        string file = Path.Combine(_directory, key + ".json");
        try
        {
            if (!slot.DiskRead)
            {
                slot.DiskRead = true;
                try
                {
                    if (File.Exists(file) && new FileInfo(file).Length <= MaxBytes * 2)
                    {
                        var stored = JsonSerializer.Deserialize<Stored>(await File.ReadAllTextAsync(file, _shutdown.Token).ConfigureAwait(false));
                        if (stored != null && stored.SavedAt <= _clock().AddMinutes(5))
                        {
                            var data = Statistics.Parse(stored.Payload, kind, bracket);
                            bool fresh = _clock() - stored.SavedAt < Lifetime;
                            lock (slot)
                            {
                                slot.View = new(data, stored.SavedAt, !fresh, false);
                                if (fresh) slot.NextAttempt = stored.SavedAt + Lifetime;
                            }
                            if (fresh) return;
                        }
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException) { }
            }
            string url = $"https://spire-codex.com/api/runs/metrics/{kind}?bracket={Uri.EscapeDataString(bracket)}";
            using var response = await _http.GetAsync(url, _shutdown.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = response.Headers.RetryAfter?.Date ?? _clock() + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(15));
                Fail(slot, retry > _clock().AddMinutes(1) ? retry : _clock().AddMinutes(1));
                return;
            }
            response.EnsureSuccessStatusCode();
            string payload = await response.Content.ReadAsStringAsync(_shutdown.Token).ConfigureAwait(false);
            var snapshot = Statistics.Parse(payload, kind, bracket);
            var now = _clock();
            lock (slot)
            {
                slot.View = new(snapshot, now, false, false);
                slot.NextAttempt = now + Lifetime;
            }
            try
            {
                Directory.CreateDirectory(_directory);
                await File.WriteAllTextAsync(file + ".tmp", JsonSerializer.Serialize(new Stored(now, payload)), _shutdown.Token).ConfigureAwait(false);
                File.Move(file + ".tmp", file, true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception) { Fail(slot, _clock().AddMinutes(15)); }
    }

    private static void Fail(Slot slot, DateTimeOffset retry)
    {
        lock (slot)
        {
            slot.View = slot.View with { Loading = false, Offline = true };
            slot.NextAttempt = retry;
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _http.Dispose();
    }
}
