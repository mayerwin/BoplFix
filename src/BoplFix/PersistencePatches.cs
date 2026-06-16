using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine.InputSystem;

namespace BoplLanFix
{
    // ============================ KEYBINDINGS (keyboard/mouse) ============================

    /// <summary>
    /// SAVE: KeyBindButton.UpdateKeybind() runs both when loading a player's choices AND right
    /// after a rebind completes. We only want to persist a genuine, just-completed rebind, which
    /// is exactly when the private 'rebindingKeys' flag is still true. (At load time it is false.)
    /// </summary>
    [HarmonyPatch(typeof(KeyBindButton), "UpdateKeybind")]
    internal static class KeyBindButton_UpdateKeybind_Patch
    {
        private static readonly FieldInfo RebindingKeysField = AccessTools.Field(typeof(KeyBindButton), "rebindingKeys");

        private static void Postfix(KeyBindButton __instance)
        {
            if (!Plugin.SaveKeybindingsEnabled) return;
            try
            {
                bool rebinding = RebindingKeysField != null && (bool)RebindingKeysField.GetValue(__instance);
                if (!rebinding) return; // not an actual rebind completion -> don't overwrite saved data

                var csb = __instance.charSelectBox;
                if (csb == null || !csb.UseskeyboardMouse) return; // keyboard/mouse only (gamepad ids are unstable)

                Persistence.SaveKbmBindings(csb.playerInit.keybindOverride);
            }
            catch (Exception e) { Plugin.Log.LogError("KeyBindButton.UpdateKeybind patch: " + e); }
        }
    }

    /// <summary>
    /// APPLY: InputUpdater.Init applies a player's CustomKeyBinding to the live InputSystem action
    /// map. For a keyboard player who hasn't rebound this session (CustomKeyBinding == null), we
    /// inject the saved binding before the method runs, so the game applies it exactly as if the
    /// player had rebound manually. This is purely local input mapping (never networked) so it
    /// cannot affect the deterministic lockstep simulation.
    /// </summary>
    [HarmonyPatch(typeof(InputUpdater), nameof(InputUpdater.Init))]
    internal static class InputUpdater_Init_Patch
    {
        private static void Prefix(int playerId)
        {
            if (!Plugin.SaveKeybindingsEnabled) return;
            try
            {
                if (GameLobby.isPlayingAReplay) return; // replays drive input from recorded packets
                var handler = PlayerHandler.Get();
                if (handler == null) return;
                Player player = handler.GetPlayer(playerId);
                if (player == null || !player.UsesKeyboardAndMouse) return;
                if (player.CustomKeyBinding != null) return; // a rebind happened this session -> respect it

                InputControl[] saved = Persistence.LoadKbmBindings();
                if (saved != null)
                {
                    player.CustomKeyBinding = saved;
                    Plugin.Log.LogInfo("Applied saved keyboard/mouse keybindings for player " + playerId + ".");
                }
            }
            catch (Exception e) { Plugin.Log.LogError("InputUpdater.Init patch: " + e); }
        }
    }

    // ============================ DEFAULT ABILITIES / POWERUPS ============================

    /// <summary>
    /// SAVE: SelectAbility.UpdatePlayerInit(ref PlayerInit, index) writes the chosen ability index
    /// into the PlayerInit. It is called for the LOCAL player's ability selectables (slots 0..2).
    /// We persist the chosen index (change-detected, so no per-frame disk spam).
    /// </summary>
    [HarmonyPatch(typeof(SelectAbility), nameof(SelectAbility.UpdatePlayerInit))]
    internal static class SelectAbility_UpdatePlayerInit_Patch
    {
        private static void Postfix(SelectAbility __instance, int index)
        {
            if (!Plugin.SaveDefaultPowerupsEnabled) return;
            try
            {
                if (index < 0 || index > 2) return; // only the three ability slots
                Persistence.SaveAbility(index, __instance.SelectedIndex);
            }
            catch (Exception e) { Plugin.Log.LogError("SelectAbility.UpdatePlayerInit patch: " + e); }
        }
    }

    /// <summary>
    /// APPLY: when a player freshly joins character-select (transition FROM the 'join' state), pre-select
    /// their saved default abilities. This runs in the PREFIX, BEFORE OnEnterSelect's body calls
    /// UpdatePlayerInit. That ordering matters: the body persists each ability element's current index, and
    /// a freshly re-created element holds the game's DEFAULT ability. If we loaded in the postfix, the
    /// body would already have saved those defaults over the player's stored picks (the bug where powerup
    /// choices never survived a return to the menu). Loading first means the body persists the loaded
    /// values instead. We act only on a genuine join (not when backing out of 'ready'). SelectAbility
    /// selectables live at list indices 2, 3, 4.
    /// </summary>
    [HarmonyPatch(typeof(CharacterSelectBox), nameof(CharacterSelectBox.OnEnterSelect))]
    internal static class CharacterSelectBox_OnEnterSelect_Patch
    {
        private static void Prefix(CharacterSelectBox __instance)
        {
            if (!Plugin.SaveDefaultPowerupsEnabled) return;
            if (__instance.menuState != CharSelectMenu.join) return; // only a genuine fresh join
            try
            {
                var sel = __instance.selectables;
                if (sel == null) return;
                for (int listIdx = 2; listIdx <= 4 && listIdx < sel.Count; listIdx++)
                {
                    var sa = sel[listIdx] as SelectAbility;
                    if (sa == null || sa.Skip) continue;
                    int saved = Persistence.LoadAbility(listIdx - 2, -1);
                    if (saved < 0) continue;
                    int fallback = sa.SelectedIndex; // the slot's valid default, in case 'saved' is now out of range
                    try
                    {
                        sa.Select(saved);
                    }
                    catch (Exception inner)
                    {
                        // A stale/removed ability index (e.g. a future game update shortened the ability list).
                        // Select() assigns selectedIndex before it throws, so restore the slot to its valid
                        // default rather than leaving an out-of-range value that could crash the game later.
                        Plugin.Log.LogWarning($"Saved powerup index {saved} for slot {listIdx - 2} is no longer valid; using the default. ({inner.Message})");
                        try { sa.Select(fallback); } catch { }
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogError("CharacterSelectBox.OnEnterSelect patch (load powerups): " + e); }
        }
    }
}
