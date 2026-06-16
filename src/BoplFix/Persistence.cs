using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BoplLanFix
{
    /// <summary>
    /// Tiny disk-persistence layer (uses Unity PlayerPrefs, like the game's own Settings).
    /// Stores keyboard/mouse keybindings (as InputControl.path strings) and the local
    /// player's default ability/powerup choices, so neither resets between launches.
    ///
    /// Keybinds are saved in the same index order the game uses (InputUpdater.swappableControls:
    /// [Jump, Ability0, Ability2, Ability1, W, A, S, D]) because we save the exact
    /// PlayerInit.keybindOverride array the game produced.
    /// </summary>
    internal static class Persistence
    {
        private const string KbmKey = "BoplPersist_kbm_binds";
        private const char Sep = '\n';
        private const int MaxBinds = 8; // == InputUpdater.swappableControls.Length

        // change-detection cache so per-frame ability saves don't spam disk writes
        private static readonly int[] AbilityCache = { int.MinValue, int.MinValue, int.MinValue };

        // ---------------- keybindings (keyboard/mouse) ----------------

        public static void SaveKbmBindings(InputControl[] binds)
        {
            try
            {
                if (binds == null) return;
                int n = Math.Min(binds.Length, MaxBinds);
                var parts = new string[n];
                for (int i = 0; i < n; i++)
                    parts[i] = binds[i] != null ? binds[i].path : "";
                PlayerPrefs.SetString(KbmKey, string.Join(Sep.ToString(), parts));
                PlayerPrefs.Save();
                Plugin.Log.LogInfo("Saved custom keyboard/mouse keybindings: " + string.Join(", ", parts));
            }
            catch (Exception e) { Plugin.Log.LogError("SaveKbmBindings: " + e); }
        }

        /// <summary>Reconstructs the saved keybinding as an InputControl[] (null if none/unresolvable).</summary>
        public static InputControl[] LoadKbmBindings()
        {
            try
            {
                string s = PlayerPrefs.GetString(KbmKey, "");
                if (string.IsNullOrEmpty(s)) return null;
                string[] parts = s.Split(Sep);
                int n = Math.Min(parts.Length, MaxBinds);
                var arr = new InputControl[n];
                bool any = false;
                for (int i = 0; i < n; i++)
                {
                    arr[i] = ResolveControl(parts[i]);
                    if (arr[i] != null) any = true;
                }
                return any ? arr : null;
            }
            catch (Exception e) { Plugin.Log.LogError("LoadKbmBindings: " + e); return null; }
        }

        public static bool HasKbmBindings()
        {
            return !string.IsNullOrEmpty(PlayerPrefs.GetString(KbmKey, ""));
        }

        private static InputControl ResolveControl(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var kb = Keyboard.current;
            if (kb != null)
                foreach (var k in kb.allKeys)
                    if (k.path == path) return k;
            var m = Mouse.current;
            if (m != null)
                foreach (var c in m.allControls)
                    if (c.path == path) return c;
            return null;
        }

        // ---------------- default abilities / powerups ----------------

        private static string AbilityKey(int slot) => "BoplPersist_ability_" + slot;

        public static void SaveAbility(int slot, int abilityIndex)
        {
            try
            {
                if (slot < 0 || slot > 2) return;
                if (AbilityCache[slot] == abilityIndex) return; // only write on change
                AbilityCache[slot] = abilityIndex;
                PlayerPrefs.SetInt(AbilityKey(slot), abilityIndex);
                PlayerPrefs.Save();
            }
            catch (Exception e) { Plugin.Log.LogError("SaveAbility: " + e); }
        }

        public static int LoadAbility(int slot, int defaultValue)
        {
            try { return PlayerPrefs.GetInt(AbilityKey(slot), defaultValue); }
            catch { return defaultValue; }
        }
    }
}
