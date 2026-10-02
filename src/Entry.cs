using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace spp;

[ModInitializer(nameof(Initialize))]
public static class Entry
{
    public static void Initialize()
    {
        var harmony = new Harmony("clareLab.spp");
        try
        {
            StatsHover.Initialize();
            harmony.PatchAll(typeof(Entry).Assembly);
#if SPP_SELFTEST
            SelfTests.Initialize();
#endif
        }
        catch (Exception error)
        {
            harmony.UnpatchAll(harmony.Id);
            GD.PrintErr("[spp] Statistics disabled: " + error.Message);
        }
        GD.Print($"[spp] Loaded {typeof(Entry).Assembly.GetName().Version?.ToString(3)}");
    }
}
