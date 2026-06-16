using HarmonyLib;
using UnityEngine;

namespace BoplLanFix
{
    /// <summary>
    /// In Bopl Battle the BepInEx manager object that hosts our plugin never receives Unity's
    /// Update()/OnGUI() calls (only Awake + the static Harmony patches run). So we drive our
    /// per-frame logic from the game's OWN SteamManager.Update, which definitely ticks every frame
    /// (it's where the game pumps Steam callbacks and reads the socket).
    /// </summary>
    [HarmonyPatch(typeof(SteamManager), "Update")]
    internal static class SteamManager_Update_Patch
    {
        private static void Postfix()
        {
            Plugin.Instance?.Tick();
        }
    }

    /// <summary>
    /// A dedicated GameObject (created by the plugin) whose OnGUI actually runs, used to draw the
    /// net overlay. It just forwards to the plugin so all the logic lives in one place.
    /// </summary>
    internal class OverlayDrawer : MonoBehaviour
    {
        private void OnGUI()
        {
            Plugin.Instance?.DrawOverlay();
        }
    }
}
