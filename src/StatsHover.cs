using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Runs;

namespace spp;

[HarmonyPatch(typeof(NHoverTipSet), nameof(NHoverTipSet.CreateAndShow), [typeof(Control), typeof(IEnumerable<IHoverTip>), typeof(HoverTipAlignment)])]
internal static class StatsHover
{
    internal sealed record Request(string Kind, string Id, bool Upgraded, int Act, string Bracket);
    private sealed record LiveTip(NHoverTipSet Set, Control Owner, Control Panel, RichTextLabel Label, Request Request, HoverTipAlignment Alignment);
    private static readonly List<LiveTip> Active = [];
    internal static StatsCache Cache { get; private set; } = null!;
    private static ulong _nextTick;
    private static bool _failed;
    private const string TitleKey = "spp.stats.title";

    internal static void Initialize()
    {
        Cache = new StatsCache(ProjectSettings.GlobalizePath("user://spp/cache"));
        ((SceneTree)Engine.GetMainLoop()).ProcessFrame += Tick;
        ((SceneTree)Engine.GetMainLoop()).Root.TreeExiting += () => Cache.Dispose();
        string scope = Scope();
        Cache.Get("cards", scope);
        Cache.Get("relics", scope);
    }

    internal static string Scope()
    {
        var run = RunManager.Instance.DebugOnlyGetState();
        string players = run?.Players.Count is > 1 ? run.Players.Count + "p" : "solo";
        string difficulty = run?.AscensionLevel == 10 ? ":a10" : "";
        string? version = ReleaseInfoManager.Instance.ReleaseInfo?.Version;
        return players + difficulty + (version != null && System.Text.RegularExpressions.Regex.IsMatch(version, @"^v?\d+\.\d+\.\d+$") ? ":v" + version.TrimStart('v') : "");
    }

    private static void Prefix(Control owner, ref IEnumerable<IHoverTip> hoverTips, out Request? __state)
    {
        __state = null;
        if (_failed || NHoverTipSet.shouldBlockHoverTips) return;
        try
        {
            var tips = hoverTips.ToArray();
            if (tips.Any(t => t.Id.StartsWith("spp:", StringComparison.Ordinal))) return;
            AbstractModel? model = owner is NCardHolder holder ? holder.CardModel :
                tips.Select(t => t.CanonicalModel).FirstOrDefault(m => m is RelicModel);
            if (model is not (CardModel or RelicModel)) return;
            var request = new Request(model is CardModel ? "cards" : "relics", model.Id.Entry,
                model is CardModel card && card.IsUpgraded, RunManager.Instance.DebugOnlyGetState()?.CurrentActIndex ?? -1, Scope());
            var table = LocManager.Instance.GetTable("static_hover_tips");
            if (!table.HasEntry(TitleKey)) table.MergeWith(new Dictionary<string, string> { [TitleKey] = "Stats ++" });
            var tip = new HoverTip(new LocString("static_hover_tips", TitleKey), Text(request)) { Id = "spp:" + request.Kind + ":" + request.Id };
            hoverTips = tips.Append(tip).ToArray();
            __state = request;
        }
        catch (Exception error) { Disable(error); }
    }

    private static void Postfix(Control owner, HoverTipAlignment alignment, NHoverTipSet? __result, Request? __state)
    {
        if (__result == null || __state == null) return;
        try
        {
            var panel = __result.GetNode<Control>("textHoverTipContainer").GetChildren().OfType<Control>().Last();
            var label = panel.GetNode<RichTextLabel>("%Description");
            panel.Name = "SppStatistics";
            label.AutowrapMode = TextServer.AutowrapMode.Word;
            Active.Add(new LiveTip(__result, owner, panel, label, __state, alignment));
        }
        catch (Exception error) { Disable(error); }
    }

    private static string Text(Request request) => StatsText.Render(Cache.Get(request.Kind, request.Bracket), request.Id, request.Upgraded, request.Act, request.Bracket);

    private static void Tick()
    {
        if (_failed || Time.GetTicksMsec() < _nextTick) return;
        _nextTick = Time.GetTicksMsec() + 200;
        try
        {
            Active.RemoveAll(t => !GodotObject.IsInstanceValid(t.Set) || t.Set.IsQueuedForDeletion() || !GodotObject.IsInstanceValid(t.Owner));
            foreach (var tip in Active)
            {
                string text = Text(tip.Request);
                if (tip.Label.Text == text) continue;
                tip.Label.Text = text;
                tip.Panel.ResetSize();
                var flow = tip.Set.GetNode<Control>("textHoverTipContainer");
                float height = flow.GetChildren().OfType<Control>().Sum(p => p.GetCombinedMinimumSize().Y + 5);
                flow.Size = new Vector2(360, Math.Min(height, tip.Set.GetViewportRect().Size.Y - 50));
                if (tip.Owner is NCardHolder holder) tip.Set.SetAlignmentForCardHolder(holder);
                else if (tip.Owner is NRelicBasicHolder relic) tip.Set.SetAlignmentForRelic(relic.Relic);
                else tip.Set.SetAlignment(tip.Owner, tip.Alignment);
            }
        }
        catch (Exception error) { Disable(error); }
    }

    private static void Disable(Exception error)
    {
        _failed = true;
        Active.Clear();
        GD.PrintErr("[spp] Tooltips disabled: " + error.Message);
    }
}
