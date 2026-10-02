using System.Globalization;
using System.Text.Json;

namespace spp;

internal sealed record ItemStats(long Runs, long Wins, long Offered, long Picked, double?[] ActPickRates)
{
    internal double? WinRate => Runs > 0 ? 100d * Wins / Runs : null;
    internal double? PickRate => Offered > 0 ? 100d * Picked / Offered : null;
}

internal sealed record Statistics(string Kind, string Bracket, Dictionary<string, ItemStats> Items)
{
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
        return new Statistics(kind, bracket, items);
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
    internal static string Render(CacheView view, string id, bool upgraded, int act, string bracket)
    {
        var lines = new List<string>();
        ItemStats? item = null;
        if (upgraded) view.Data?.Items.TryGetValue(id + "+", out item);
        if (item == null) view.Data?.Items.TryGetValue(id, out item);
        if (item != null && (item.Runs > 0 || item.Offered > 0))
        {
            Add("Win rate", item.WinRate);
            Add("Pick rate", item.PickRate);
            if (act is >= 0 and < 3) Add($"Act {act + 1} pick", item.ActPickRates[act]);
            if (item.Runs > 0) Row("Runs", item.Runs.ToString("N0", CultureInfo.InvariantCulture));
            if (item.Offered > 0) Row("Offers", item.Offered.ToString("N0", CultureInfo.InvariantCulture));
        }
        string body = lines.Count > 0 ? "[table=2]" + string.Join("", lines) + "[/table]" :
            view.Loading ? "Loading statistics…" : view.Data != null ? "No samples for this item." : "Statistics unavailable.";
        string[] scope = bracket.Split(':');
        string players = scope[0] == "solo" ? "Solo" : scope[0].EndsWith('p') ? scope[0][..^1] + " players" : "All players";
        string difficulty = scope.Contains("a10") ? "A10" : "All ascensions";
        string version = scope.LastOrDefault(x => x.StartsWith('v')) ?? "All patches";
        string age = view.SavedAt is { } saved ? saved.ToString("MMM d", CultureInfo.InvariantCulture) : "";
        string cache = view.Offline && view.Data != null ? "  Cached" : "";
        return body + $"\n[font_size=16][color=#b3c0c2]{players}  {difficulty}\n{version}  {age}{cache}\nSpire Codex[/color][/font_size]";

        void Add(string label, double? value)
        {
            if (value.HasValue) Row(label, value.Value.ToString("0.0", CultureInfo.InvariantCulture) + "%");
        }

        void Row(string label, string value) => lines.Add($"[cell expand=1 shrink=false]{label}[/cell][cell expand=1 shrink=false][right][color=#f2d68d]{value}[/color][/right][/cell]");
    }
}
