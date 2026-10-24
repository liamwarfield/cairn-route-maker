using System;
using HarmonyLib;
using Il2CppTheGameBakers.Cairn;
using MelonLoader;

namespace CairnRouteMaker.Game;

/// <summary>
/// In a story-free game, trigger volumes in the world put Ava's ice axes and crampons on and take
/// them off as she walks between rock and ice. Free climb keeps them on, so she has them wherever
/// a teleport or zone trip leaves her; the volumes that take them off are ignored.
/// </summary>
internal static class IceGearPatches
{
    /// <summary>While true, Ava keeps her ice gear.</summary>
    public static Func<bool> KeepIceGear { get; set; }

    [HarmonyPatch(typeof(IceClimbingTrigger), nameof(IceClimbingTrigger.OnTriggerEntered))]
    private static class TriggerPatch
    {
        private static bool Prefix(IceClimbingTrigger __instance)
        {
            try
            {
                if (__instance.equipIceClimbing || KeepIceGear == null || !KeepIceGear())
                {
                    return true;
                }

                MelonLogger.Msg($"Free climb: ignored {__instance.name}, which takes off Ava's ice gear.");
                return false;
            }
            catch (Exception e)
            {
                MelonLogger.Error($"Failed checking an ice gear trigger: {e}");
                return true;
            }
        }
    }
}
