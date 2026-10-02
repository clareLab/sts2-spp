using System.Globalization;
using System.Text.Json;

namespace spp;

internal sealed record ItemStats(long Runs, long Wins, long Offered, long Picked, double?[] ActPickRates)
{
    internal double? WinRate => Runs > 0 ? 100d * Wins / Runs : null;
    internal double? PickRate => Offered > 0 ? 100d * Picked / Offered : null;
}

internal sealed record Statistics(string Kind, string Bracket, Dictionary<string, ItemStats> Items, long TotalRuns, double? BaselineWinRate = null)
{
    internal Statistics WithPopulation(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        long total = Count(root, "total_runs"), wins = Count(root, "total_wins");
        var ascensions = root.GetProperty("by_ascension");
        if (total == 0 || total != TotalRuns || wins > total ||
            ascensions.GetArrayLength() != 1 || ascensions[0].GetProperty("ascension").GetInt32() != 10 ||
            Count(ascensions[0], "runs") != total || Count(ascensions[0], "wins") != wins)
            throw new JsonException("Unexpected baseline population.");
        return this with { BaselineWinRate = 100d * wins / total };
    }

    internal static Statistics Parse(string payload, string kind, string bracket)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.GetProperty("entity_type").GetString() != kind || root.GetProperty("bracket").GetString() != bracket)
            throw new JsonException("Unexpected statistics scope.");
        if (root.TryGetProperty("character", out var character) && character.ValueKind != JsonValueKind.Null)
            throw new JsonException("Unexpected character filter.");
        var rows = root.GetProperty("rows");
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 10000)
            throw new JsonException("Invalid statistics table.");
        var items = new Dictionary<string, ItemStats>(StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            string? id = row.GetProperty("id").GetString();
            if (string.IsNullOrEmpty(id) || id.Length > 160) continue;
            bool upgraded = row.TryGetProperty("upgraded", out var upgrade) && upgrade.ValueKind == JsonValueKind.True;
            long runs = Count(row, "picks"), wins = Count(row, "wins");
            long offered = Count(row, "offered"), picked = Count(row, "picked");
            if (wins > runs || picked > offered) continue;
            var acts = new double?[3];
            if (row.TryGetProperty("pick_rate_by_act", out var actRates) && actRates.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (var rate in actRates.EnumerateArray().Take(3))
                    acts[index++] = Percentage(rate);
            }
            items[id + (upgraded ? "+" : "")] = new ItemStats(runs, wins, offered, picked, acts);
        }
        return new Statistics(kind, bracket, items, Count(root, "total_runs"));
    }

    private static long Count(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return 0;
        if (!value.TryGetInt64(out long number) || number < 0 || number > 1_000_000_000_000)
            throw new JsonException("Invalid sample count.");
        return number;
    }

    private static double? Percentage(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number) && number >= 0 && number <= 100 ? number : null;
}

internal static class StatsText
{
    internal const string NumberFont = "res://fonts/spectral_bold.ttf";

    internal static string? Render(CacheView view, string id, bool upgraded, int act, string bracket)
    {
        var lines = new List<string>();
        ItemStats? item = null;
        if (upgraded) view.Data?.Items.TryGetValue(id + "+", out item);
        if (item == null) view.Data?.Items.TryGetValue(id, out item);
        if (item != null && (item.Runs > 0 || item.Offered > 0))
        {
            Add("Win rate Δ", item.WinRate - view.Data?.BaselineWinRate, item.Runs, "Runs", true);
            Add("Pick rate", item.PickRate, item.Offered, "Offers");
            if (act is >= 0 and < 3) Add($"Act {act + 1} pick", item.ActPickRates[act]);
        }
        if (lines.Count == 0) return null;
        string body = "[table=2]" + string.Join("", lines) + "[/table]";
        string[] scope = bracket.Split(':');
        string players = scope[0] == "solo" ? "Solo" : scope[0].EndsWith('p') ? scope[0][..^1] + " players" : "All players";
        string difficulty = scope.FirstOrDefault(s => s.Length > 1 && s[0] == 'a' && int.TryParse(s.AsSpan(1), out _))?.ToUpperInvariant() ?? "All ascensions";
        string cache = view.Offline && view.SavedAt.HasValue ? Secondary("Cached statistics") : "";
        string population = $"[font_size=16][color=#b3c0c2][table=2][cell expand=1 shrink=false]{difficulty}[/cell][cell expand=1 shrink=false][right]{players}[/right][/cell][/table][/color][/font_size]";
        return population + "\n" + body + cache;

        void Add(string label, double? value, long samples = 0, string unit = "", bool delta = false)
        {
            if (!value.HasValue) return;
            label = label.Replace(' ', '\u00a0');
            string sampleLabel = samples > 0 ? Secondary(unit) : "";
            string sampleCount = samples > 0 ? Secondary(samples.ToString("N0", CultureInfo.InvariantCulture)) : "";
            double rounded = Math.Round(value.Value, 1, MidpointRounding.AwayFromZero);
            string percentage = delta ? rounded.ToString("+0.0;−0.0;0.0", CultureInfo.InvariantCulture) : rounded.ToString("0.0", CultureInfo.InvariantCulture);
            string color = PercentageColor(delta ? 50 + rounded * 2.5 : rounded);
            string suffix = delta ? "pp" : "%";
            string number = Numeric($"[font_size=22][color={color}]{percentage}\u00a0{suffix}[/color][/font_size]{sampleCount}");
            lines.Add($"[cell expand=5 shrink=false][font_size=22]{label}[/font_size]{sampleLabel}[/cell][cell expand=4 shrink=false][right]{number}[/right][/cell]");
        }

        static string Secondary(string text) => $"\n[font_size=16][color=#b3c0c2]{text}[/color][/font_size]";
    }

    internal static string Numeric(string text) => $"[font={NumberFont}][otf=tnum,lnum,kern=0]{text}[/otf][/font]";

    private static string PercentageColor(double value)
    {
        double position = Math.Clamp(value / 50, 0, 2);
        int from = position <= 1 ? 0xFF5555 : 0xEFC851;
        int to = position <= 1 ? 0xEFC851 : 0x36C78A;
        double weight = position <= 1 ? position : position - 1;
        return "#" + string.Concat(new[] { 16, 8, 0 }.Select(shift =>
        {
            double channel = ((from >> shift) & 255) * (1 - weight) + ((to >> shift) & 255) * weight;
            return ((int)Math.Round(channel * .8 + ((0xFFF6E2 >> shift) & 255) * .2)).ToString("X2", CultureInfo.InvariantCulture);
        }));
    }
}
