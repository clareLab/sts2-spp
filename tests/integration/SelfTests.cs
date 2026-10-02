using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
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
            string bracket = StatsHover.Scope();
            for (int i = 0; i < 1200 && (StatsHover.Cache.Get("cards", bracket).Loading || StatsHover.Cache.Get("relics", bracket).Loading); i++) await Frames(1);
            Check(StatsHover.Cache.Get("cards", bracket).Data?.Items.Count > 0, "live card statistics loaded");
            Check(StatsHover.Cache.Get("relics", bracket).Data?.Items.Count > 0, "live relic statistics loaded");
            var run = await NGame.Instance!.StartNewSingleplayerRun(ModelDb.Character<Ironclad>(), true,
                ActModel.GetDefaultList(), [], "SPP-PREVIEW-001", GameMode.Standard);
            await RunManager.Instance.EnterMapCoord(run.Map!.GetAllMapPoints().Where(p => p.PointType == MapPointType.Monster).OrderBy(p => p.coord.row).First().coord);
            await Frames(3);
            var cards = new CardModel[] { ModelDb.Card<PommelStrike>().ToMutable(), ModelDb.Card<ShrugItOff>().ToMutable(), ModelDb.Card<BattleTrance>().ToMutable() };
            foreach (var card in cards) card.Owner = run.Players[0];
            var screen = NCardRewardSelectionScreen.ShowScreen(cards.Select(c => new CardCreationResult(c)).ToArray(), [])!;
            await Delay(1);
            var holder = screen.GetCardHolder(cards[0]);
            AccessTools.Method(typeof(NCardHolder), "CreateHoverTips").Invoke(holder, null);
            await Frames(3);
            Validate("card");
            Check(Panels().Single().GetNode<RichTextLabel>("%Description").Text.Contains("Pick rate"), "native card tooltip contains pick rate");
            await Screenshot("card");
            NHoverTipSet.Clear();
            NOverlayStack.Instance!.Remove(screen);
            await Frames(5);
            var relicScreen = NChooseARelicSelection.ShowScreen([ModelDb.Relic<BagOfPreparation>().ToMutable(), ModelDb.Relic<Anchor>().ToMutable(), ModelDb.Relic<Shovel>().ToMutable()])!;
            await Delay(1);
            var relicHolder = Descendants(relicScreen).OfType<NRelicBasicHolder>().First();
            AccessTools.Method(typeof(NRelicBasicHolder), "OnFocus").Invoke(relicHolder, null);
            await Frames(3);
            Validate("relic");
            Check(!Panels().Single().GetNode<RichTextLabel>("%Description").Text.Contains("Pick rate"), "relic tooltip omits unavailable pick rate");
            await Screenshot("relic");
            NHoverTipSet.Clear();
            await Frames(3);
            Check(!Panels().Any(), "statistics disappear with native tooltips");
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
        Check(label.Text.Contains("Spire Codex") && label.Text.Contains("v0.111.0"), name + " tooltip shows source and version");
        Check(label.GetContentHeight() <= label.Size.Y + 2, name + " tooltip text is not clipped");
        Check(label.GetContentWidth() >= label.Size.X - 15 && label.GetContentWidth() <= label.Size.X + 2, name + " statistics use the available width");
        var rectangle = panel.GetGlobalRect();
        Check(rectangle.Position.X >= -1 && rectangle.Position.Y >= -1 && rectangle.End.X <= panel.GetViewportRect().Size.X + 1 && rectangle.End.Y <= panel.GetViewportRect().Size.Y + 1,
            name + " tooltip stays inside the viewport");
        GD.Print($"[spp] {name} panel {rectangle}, text {label.Size}, content {label.GetContentHeight()}");
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
