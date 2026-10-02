using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace spp;

internal static class SelfTests
{
    private static readonly List<string> Passed = [];

    internal static void Initialize()
    {
        if (!OS.GetCmdlineArgs().Contains("--spp-selftest")) return;
        ((SceneTree)Engine.GetMainLoop()).ProcessFrame += Start;
    }

    private static void Start()
    {
        if (NGame.Instance?.MainMenu == null || !SaveManager.Instance.IsProfileInitialized) return;
        ((SceneTree)Engine.GetMainLoop()).ProcessFrame -= Start;
        _ = Run();
    }

    private static async Task Run()
    {
        string? error = null;
        try
        {
            if (!File.Exists(ProjectSettings.GlobalizePath("user://.spp-test-sandbox")))
                throw new InvalidOperationException("An isolated test sandbox is required.");
            await Frames(3);
            SaveManager.Instance.SetFtuesEnabled(false);
            ValidateNumbers();
            Check(StatsHover.Scope().Split(':').Contains("a10"), "use A10 statistics before a run starts");
            var run = await NGame.Instance!.StartNewSingleplayerRun(ModelDb.Character<Ironclad>(), true,
                ActModel.GetDefaultList(), [], "SPP-PREVIEW-001", GameMode.Standard);
            Check(run.AscensionLevel != 10 && StatsHover.Scope().Split(':').Contains("a10"), "use A10 statistics during a lower-ascension run");
            string bracket = StatsHover.Scope();
            Check(bracket.StartsWith("solo:a10:", StringComparison.Ordinal), "scope keeps the current party size and game version");
            for (int i = 0; i < 1200 && (StatsHover.Cache.Get("cards", bracket).Loading || StatsHover.Cache.Get("relics", bracket).Loading); i++) await Frames(1);
            Check(StatsHover.Cache.Get("cards", bracket).Data?.Items.Count > 0, "live card statistics loaded");
            Check(StatsHover.Cache.Get("relics", bracket).Data?.Items.Count > 0, "live relic statistics loaded");
            await RunManager.Instance.EnterMapCoord(run.Map!.GetAllMapPoints().Where(p => p.PointType == MapPointType.Monster).OrderBy(p => p.coord.row).First().coord);
            await Frames(3);
            var cards = new CardModel[] { ModelDb.Card<PommelStrike>().ToMutable(), ModelDb.Card<ShrugItOff>().ToMutable(), ModelDb.Card<DefendIronclad>().ToMutable() };
            foreach (var card in cards) card.Owner = run.Players[0];
            var screen = NCardRewardSelectionScreen.ShowScreen(cards.Select(c => new CardCreationResult(c)).ToArray(), [])!;
            await Delay(1);
            var holder = screen.GetCardHolder(cards[0]);
            await ValidateLoading(holder, bracket);
            AccessTools.Method(typeof(NCardHolder), "CreateHoverTips").Invoke(holder, null);
            await Frames(3);
            await Screenshot("card");
            Validate("card");
            Check(Panels().Single().GetNode<RichTextLabel>("%Description").Text.Contains("Pick rate"), "native card tooltip contains pick rate");
            NHoverTipSet.Clear();
            AccessTools.Method(typeof(NCardHolder), "CreateHoverTips").Invoke(screen.GetCardHolder(cards[2]), null);
            await Frames(3);
            Check(!Panels().Any(), "starter cards without samples have no statistics panel");
            Check(Descendants(NGame.Instance.HoverTipsContainer!).OfType<RichTextLabel>().Any(), "native keyword tooltips remain without statistics");
            NHoverTipSet.Clear();
            NOverlayStack.Instance!.Remove(screen);
            await Frames(5);
            var inspect = NGame.Instance.GetInspectCardScreen();
            inspect.Open([cards[0]], 0);
            await Delay(0.5);
            Validate("card inspection");
            inspect.Close();
            NHoverTipSet.Clear();
            await Frames(3);
            var relicScreen = NChooseARelicSelection.ShowScreen([ModelDb.Relic<BagOfPreparation>().ToMutable(), ModelDb.Relic<Anchor>().ToMutable(), ModelDb.Relic<Shovel>().ToMutable()])!;
            await Delay(1);
            var relicHolder = Descendants(relicScreen).OfType<NRelicBasicHolder>().First();
            AccessTools.Method(typeof(NRelicBasicHolder), "OnFocus").Invoke(relicHolder, null);
            await Frames(3);
            await Screenshot("relic");
            Validate("relic");
            Check(!Panels().Single().GetNode<RichTextLabel>("%Description").Text.Contains("Pick rate"), "relic tooltip omits unavailable pick rate");
            NHoverTipSet.Clear();
            await Frames(3);
            Check(!Panels().Any(), "statistics disappear with native tooltips");
            NOverlayStack.Instance!.Remove(relicScreen);
            await RunManager.Instance.EnterMapCoord(run.Map.GetAllMapPoints().First(p => p.PointType == MapPointType.Shop).coord);
            var merchant = NRun.Instance!.MerchantRoom!;
            merchant.OpenInventory();
            await Delay(1);
            var shopCard = Descendants(merchant.Inventory).OfType<NMerchantCard>().First(node =>
                node.Entry is MerchantCardEntry { CreationResult.Card: { } card } &&
                StatsText.Render(StatsHover.Cache.Get("cards", bracket), card.Id.Entry, card.IsUpgraded, run.CurrentActIndex, bracket) != null);
            AccessTools.Method(typeof(NMerchantCard), "CreateHoverTip").Invoke(shopCard, null);
            await Frames(3);
            await Screenshot("shop-card");
            Validate("shop card");
            NHoverTipSet.Clear();
            var shopRelic = Descendants(merchant.Inventory).OfType<NMerchantRelic>().First(node =>
                node.Entry is MerchantRelicEntry { Model: { } relic } &&
                StatsText.Render(StatsHover.Cache.Get("relics", bracket), relic.Id.Entry, false, run.CurrentActIndex, bracket) != null);
            AccessTools.Method(typeof(NMerchantRelic), "CreateHoverTip").Invoke(shopRelic, null);
            await Frames(3);
            Validate("shop relic");
            NHoverTipSet.Clear();
            GD.Print("[spp] SELFTEST_OK");
        }
        catch (Exception exception)
        {
            error = exception.ToString();
            GD.PrintErr("[spp] SELFTEST_FAILED " + error);
        }
        finally
        {
            File.WriteAllText(ProjectSettings.GlobalizePath("user://spp-selftest.json"), JsonSerializer.Serialize(new { success = error == null, passed = Passed, error }));
            ((SceneTree)Engine.GetMainLoop()).Quit(error == null ? 0 : 1);
        }
    }

    private static void Validate(string name)
    {
        var panel = Panels().Single();
        var label = panel.GetNode<RichTextLabel>("%Description");
        GD.Print($"[spp] {name} panel {panel.GetGlobalRect()}, text {label.Size}, content {label.GetContentWidth()} x {label.GetContentHeight()}");
        Check(!label.Text.Contains("Spire Codex") && label.Text.Contains("Solo") && label.Text.Contains("A10") && !label.Text.Contains(" / "), name + " tooltip keeps separate scope labels without a footer");
        Check(label.GetContentHeight() <= label.Size.Y + 2, name + " tooltip text is not clipped");
        Check(label.GetContentWidth() >= label.Size.X - 15 && label.GetContentWidth() <= label.Size.X + 2, name + " statistics use the available width");
        var rectangle = panel.GetGlobalRect();
        Check(rectangle.Position.X >= -1 && rectangle.Position.Y >= -1 && rectangle.End.X <= panel.GetViewportRect().Size.X + 1 && rectangle.End.Y <= panel.GetViewportRect().Size.Y + 1,
            name + " tooltip stays inside the viewport");
    }

    private static void ValidateNumbers()
    {
        using var scene = ResourceLoader.Load<PackedScene>("res://scenes/ui/hover_tip.tscn").Instantiate<Control>();
        using var probe = new RichTextLabel { BbcodeEnabled = true, AutowrapMode = TextServer.AutowrapMode.Off, Size = new Vector2(500, 100), Visible = false };
        probe.AddThemeFontOverride("normal_font", scene.GetNode<RichTextLabel>("%Description").GetThemeFont("normal_font"));
        ((SceneTree)Engine.GetMainLoop()).Root.AddChild(probe);
        foreach (int size in new[] { 16, 22 })
        {
            var widths = Enumerable.Range(0, 10).Select(digit =>
            {
                probe.Text = $"[font_size={size}]{StatsText.Numeric(new string((char)('0' + digit), 6))}[/font_size]";
                return probe.GetContentWidth();
            }).ToArray();
            Check(widths.Min() > 0 && widths.Max() - widths.Min() <= 1, $"equal digit advances at {size}px");
        }
    }

    private static async Task ValidateLoading(NCardHolder holder, string bracket)
    {
        var original = StatsHover.Cache;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new LoadingHandler(gate.Task, JsonSerializer.Serialize(new
        {
            entity_type = "cards",
            bracket,
            rows = new[] { new { id = holder.CardModel!.Id.Entry, picks = 200, wins = 50, offered = 1000, picked = 100 } }
        }));
        string path = ProjectSettings.GlobalizePath("user://spp-loading-" + Guid.NewGuid());
        using var cache = new StatsCache(path, new System.Net.Http.HttpClient(handler));
        try
        {
            AccessTools.Property(typeof(StatsHover), nameof(StatsHover.Cache)).SetValue(null, cache);
            AccessTools.Method(typeof(NCardHolder), "CreateHoverTips").Invoke(holder, null);
            await Frames(5);
            var panel = Panels().Single();
            var spinner = Descendants(panel).OfType<Control>().Single(n => n.Name == "SppLoading");
            Check(spinner.Visible && spinner.MouseFilter == Control.MouseFilterEnum.Ignore && panel.GetNode<RichTextLabel>("%Description").Text == " ",
                "pending statistics show only a nonblocking spinner in the native tip");
            await Screenshot("loading");
            gate.SetResult();
            for (int i = 0; i < 300 && spinner.Visible; i++) await Frames(1);
            Check(!spinner.Visible && panel.GetNode<RichTextLabel>("%Description").Text.Contains("25.0%"),
                "completed statistics replace the spinner without another hover");
        }
        finally
        {
            gate.TrySetResult();
            NHoverTipSet.Clear();
            AccessTools.Property(typeof(StatsHover), nameof(StatsHover.Cache)).SetValue(null, original);
        }
    }

    private sealed class LoadingHandler(Task ready, string payload) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await ready.WaitAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(payload) };
        }
    }

    private static IEnumerable<Control> Panels() => Descendants(NGame.Instance!.HoverTipsContainer!).OfType<Control>().Where(n => n.Name == "SppStatistics");

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException(name);
        Passed.Add(name);
        GD.Print("[spp] PASS " + name);
    }

    private static async Task Frames(int count)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        for (int i = 0; i < count; i++) await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
    }

    private static async Task Delay(double seconds)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        await tree.ToSignal(tree.CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
    }

    private static async Task Screenshot(string name)
    {
        await Frames(5);
        Check(NGame.Instance!.GetViewport().GetTexture().GetImage().SavePng(ProjectSettings.GlobalizePath($"user://spp-{name}.png")) == Error.Ok, name + " screenshot");
    }
}
