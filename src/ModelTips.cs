using System.Collections;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;

namespace spp;

internal sealed record ModelTips(AbstractModel Model, IEnumerable<IHoverTip> Tips) : IEnumerable<IHoverTip>
{
    public IEnumerator<IHoverTip> GetEnumerator() => Tips.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

[HarmonyPatch]
internal static class ModelTipsPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.HoverTips));
        yield return AccessTools.PropertyGetter(typeof(RelicModel), nameof(RelicModel.HoverTips));
        yield return AccessTools.PropertyGetter(typeof(RelicModel), nameof(RelicModel.HoverTipsExcludingRelic));
    }

    private static void Postfix(AbstractModel __instance, ref IEnumerable<IHoverTip> __result) =>
        __result = new ModelTips(__instance, __result);
}
