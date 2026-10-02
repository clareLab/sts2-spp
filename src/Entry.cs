using Godot;
using MegaCrit.Sts2.Core.Modding;

namespace spp;

[ModInitializer(nameof(Initialize))]
public static class Entry
{
    public static void Initialize()
    {
        GD.Print($"[spp] Loaded {typeof(Entry).Assembly.GetName().Version?.ToString(3)}");
    }
}
