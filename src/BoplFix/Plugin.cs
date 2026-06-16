using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BoplLanFix
{
    /// <summary>
    /// Bopl Battle ships its multiplayer on Steam's modern SteamNetworkingSockets P2P
    /// (SteamManager calls CreateRelaySocket / ConnectRelay) but never configures the
    /// P2P transport. On the Steam platform the default is "relay-only, don't share IPs"
    /// (anti-DoS), so EVERY connection -- including two PCs in the same room -- is forced
    /// through a Steam Datagram Relay server, giving constant high ping.
    ///
    /// This plugin sets the global Steam config value P2P_Transport_ICE_Enable = All right
    /// after Steam initializes, which lets peers share LAN/STUN candidates and connect
    /// directly (~1ms on a LAN). The Steam relay stays available as a fallback for real
    /// internet play, so nothing breaks. BOTH players must run the mod (ICE needs both
    /// sides to offer candidates).
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.boplfix";
        public const string Name = "Bopl Battle Fixes";
        public const string Version = "1.0.3";

        // Steam config value ids (from steamnetworkingtypes.h). Facepunch's bundled
        // NetConfig enum doesn't include these, so we use the raw ids via reflection.
        private const int CfgLogLevelP2PRendezvous  = 17;  // ICE/rendezvous log channel
        private const int CfgP2PStunServerList      = 103;
        private const int CfgP2PTransportIceEnable  = 104;
        private const int CfgP2PTransportIcePenalty = 105;
        private const int IceEnableAll = 0x7fffffff; // Relay|Private|Public + everything

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        // Read by the persistence Harmony patches.
        internal static bool SaveKeybindingsEnabled = true;
        internal static bool SaveDefaultPowerupsEnabled = true;

        private ConfigEntry<bool> _enableDirectP2P;
        private ConfigEntry<bool> _preferDirectOverRelay;
        private ConfigEntry<bool> _verboseLogging;
        private ConfigEntry<bool> _saveKeybindings;
        private ConfigEntry<bool> _saveDefaultPowerups;
        private ConfigEntry<string> _stunServers;
        private ConfigEntry<bool> _showNetOverlay;
        private ConfigEntry<bool> _showBadge;
        private ConfigEntry<int> _badgeSizePercent;
        private ConfigEntry<string> _badgeColorHex;

        private static bool _configApplied;
        private float _statusTimer;

        // Read by the badge Harmony patch.
        internal static bool ShowBadge = true;
        internal static int BadgeSizePercent = 24;
        internal static string BadgeColorHex = "1B5E20";

        // In-game net overlay state.
        internal static bool ShowNetOverlay;
        internal static bool VerboseLogging;
        private float _overlayTimer;
        private string[] _overlayLines = Array.Empty<string>();
        private GUIStyle _overlayStyle;
        private GUIStyle _overlayHeaderStyle;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            // THE LAN FIX, PART 0: make sure Steam's ICE/NAT-traversal engine is even loadable.
            EnsureWebRtcDll();

            _enableDirectP2P = Config.Bind("Netcode", "EnableDirectP2P", true,
                "THE FIX. Force Steam to allow direct ICE connections (LAN + NAT punch-through) "
                + "instead of forcing all P2P traffic through a Steam relay server. Both players must enable this.");

            _preferDirectOverRelay = Config.Bind("Netcode", "PreferDirectOverRelay", true,
                "Set the ICE transport penalty to 0 so the fast direct route is strongly preferred "
                + "over the Steam relay whenever both are available.");

            _verboseLogging = Config.Bind("Diagnostics", "VerboseLogging", false,
                "Troubleshooting only. When true, writes detailed Steam networking logs to "
                + "BepInEx\\LogOutput.log (the ICE/relay negotiation plus each peer's route and ping), so you "
                + "can see whether a connection went DIRECT or via RELAY and why. Leave false for normal play.");
            VerboseLogging = _verboseLogging.Value;

            _saveKeybindings = Config.Bind("QualityOfLife", "SaveKeybindings", true,
                "Remember custom KEYBOARD/MOUSE keybindings across launches so you don't have to re-bind every time.");
            _saveDefaultPowerups = Config.Bind("QualityOfLife", "SaveDefaultPowerups", true,
                "Remember your last chosen abilities/powerups and pre-select them as the default next launch.");
            SaveKeybindingsEnabled = _saveKeybindings.Value;
            SaveDefaultPowerupsEnabled = _saveDefaultPowerups.Value;

            _stunServers = Config.Bind("Netcode", "StunServerList",
                "stun.l.google.com:19302,stun1.l.google.com:19302",
                "STUN servers used to ACTIVATE Steam's ICE transport. Steam will not attempt direct/NAT-pierced "
                + "connections when this is empty (it is empty by default for this game, which is the second half "
                + "of the LAN bug). These servers are only used to discover candidate addresses; actual LAN game "
                + "traffic stays on the LAN. Leave as-is unless you have a reason to change it.");

            _showNetOverlay = Config.Bind("Diagnostics", "ShowNetOverlay", false,
                "Show a small in-game overlay with each peer's ping and whether the connection is DIRECT (LAN) "
                + "or via RELAY. Off by default; press F8 during an online match to toggle it on.");
            ShowNetOverlay = _showNetOverlay.Value;

            _showBadge = Config.Bind("Badge", "ShowBadge", true,
                "Show the '+ BoplFix' badge under the main-menu version number.");
            _badgeSizePercent = Config.Bind("Badge", "BadgeSizePercent", 24,
                "Badge text size as a percentage of the version number's size (smaller fits more text). 18-40 is sensible.");
            _badgeColorHex = Config.Bind("Badge", "BadgeColorHex", "1B5E20",
                "Badge text colour as a hex RGB string without '#'. Examples: 1B5E20 dark green, 0A4DA0 blue, B5530A orange, 222222 near-black.");
            ShowBadge = _showBadge.Value;
            BadgeSizePercent = Mathf.Clamp(_badgeSizePercent.Value, 5, 100);
            BadgeColorHex = SanitizeHex(_badgeColorHex.Value);

            var harmony = new Harmony(Guid);

            // CRITICAL: the LAN/direct-P2P fix. Patched first and on its own so nothing else can break it.
            try
            {
                harmony.CreateClassProcessor(typeof(SteamClient_Init_Patch)).Patch();
                Log.LogInfo("LAN fix patch applied (SteamClient.Init hook).");
            }
            catch (Exception e) { Log.LogError("CRITICAL: failed to apply the LAN fix patch: " + e); }

            // OPTIONAL: quality-of-life persistence. Each patch is isolated so a failure here can
            // never disable the LAN fix above.
            TryPatch(harmony, typeof(KeyBindButton_UpdateKeybind_Patch),      _saveKeybindings.Value);
            TryPatch(harmony, typeof(InputUpdater_Init_Patch),               _saveKeybindings.Value);
            TryPatch(harmony, typeof(SelectAbility_UpdatePlayerInit_Patch),  _saveDefaultPowerups.Value);
            TryPatch(harmony, typeof(CharacterSelectBox_OnEnterSelect_Patch), _saveDefaultPowerups.Value);

            // Cosmetic: "+ BoplFix" badge under the main-menu version number.
            TryPatch(harmony, typeof(PrintText_Version_Patch), enabled: true);

            // Drives our per-frame work (overlay/F8/route-log), since the plugin's own Update isn't ticked.
            TryPatch(harmony, typeof(SteamManager_Update_Patch), enabled: true);

            Log.LogInfo($"{Name} v{Version} loaded. Will patch Steam P2P transport after Steam init.");
        }

        private static void TryPatch(Harmony harmony, Type patchClass, bool enabled)
        {
            if (!enabled) { Log.LogInfo($"Skipping {patchClass.Name} (disabled in config)."); return; }
            try
            {
                harmony.CreateClassProcessor(patchClass).Patch();
                Log.LogInfo($"Applied {patchClass.Name}.");
            }
            catch (Exception e) { Log.LogError($"Failed to apply optional patch {patchClass.Name} (LAN fix unaffected): " + e); }
        }

        private static string SanitizeHex(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return "1B5E20";
            hex = hex.Trim().TrimStart('#');
            return Regex.IsMatch(hex, "^[0-9A-Fa-f]{6}$") ? hex : "1B5E20";
        }

        internal static Color BadgeColor()
        {
            return ColorUtility.TryParseHtmlString("#" + BadgeColorHex, out var c) ? c : new Color(0.106f, 0.369f, 0.125f);
        }

        // ---- THE LAN FIX, PART 0: ensure Steam's ICE engine (steamwebrtc64.dll) is loadable ----
        // Bopl never shipped steamwebrtc64.dll next to its steam_api64.dll, and Steam doesn't put its own
        // folder on the game's DLL search path. So Steam's networking can't load the ICE/NAT-traversal
        // engine ("Failed to load steamwebrtc64.dll" / "No ICE session factory") and EVERY P2P connection
        // falls back to the slow relay. The Steam client already has the DLL; copying it next to the game
        // exe puts it on the search path so ICE can run. Runs once (no-op once the file exists).
        private static void EnsureWebRtcDll()
        {
            try
            {
                string gameRoot = Paths.GameRootPath;
                if (string.IsNullOrEmpty(gameRoot)) return;
                string dest = Path.Combine(gameRoot, "steamwebrtc64.dll");
                if (File.Exists(dest)) return;
                foreach (string src in WebRtcSourceCandidates())
                {
                    if (!string.IsNullOrEmpty(src) && File.Exists(src))
                    {
                        File.Copy(src, dest, false);
                        Log.LogInfo("Enabled direct LAN play: copied steamwebrtc64.dll into the game folder (from " + src + ").");
                        return;
                    }
                }
                Log.LogWarning("steamwebrtc64.dll not found in the Steam client folder; direct (ICE) LAN connections "
                             + "may fall back to the relay. Repair the Steam client, or copy steamwebrtc64.dll next to BoplBattle.exe.");
            }
            catch (Exception e) { Log?.LogWarning("EnsureWebRtcDll: " + e.Message); }
        }

        private static IEnumerable<string> WebRtcSourceCandidates()
        {
            string reg = ReadSteamPathFromRegistry();
            if (!string.IsNullOrEmpty(reg)) yield return Path.Combine(reg, "steamwebrtc64.dll");
            foreach (string env in new[] { "ProgramFiles(x86)", "ProgramW6432", "ProgramFiles" })
            {
                string p = Environment.GetEnvironmentVariable(env);
                if (!string.IsNullOrEmpty(p)) yield return Path.Combine(p, "Steam", "steamwebrtc64.dll");
            }
        }

        // Read HKCU\Software\Valve\Steam\SteamPath via reflection so we don't need a Microsoft.Win32.Registry ref.
        private static string ReadSteamPathFromRegistry()
        {
            try
            {
                var t = Type.GetType("Microsoft.Win32.Registry, mscorlib") ?? Type.GetType("Microsoft.Win32.Registry");
                var m = t?.GetMethod("GetValue", new[] { typeof(string), typeof(string), typeof(object) });
                var v = m?.Invoke(null, new object[] { @"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null }) as string;
                if (!string.IsNullOrEmpty(v)) return v.Replace('/', '\\');
            }
            catch { }
            return null;
        }

        private static int ParsePingMs(string ds)
        {
            if (string.IsNullOrEmpty(ds)) return -1;
            var m = Regex.Match(ds, @"Ping:\s*(\d+)\s*ms");
            return m.Success && int.TryParse(m.Groups[1].Value, out int v) ? v : -1;
        }

        /// <summary>Called by the Harmony postfix on SteamClient.Init.</summary>
        internal static void OnSteamInitialized()
        {
            try { Instance?.ApplyNetworkConfig(); }
            catch (Exception e) { Log?.LogError("ApplyNetworkConfig (from Init postfix) failed: " + e); }
        }

        private OverlayDrawer _overlayDrawer;

        /// <summary>
        /// Driven every frame from a Harmony hook on SteamManager.Update. In this game the BepInEx
        /// plugin's own Update()/OnGUI() are never ticked (only Awake + the Harmony patches run), so
        /// we piggyback on the game's own per-frame loop, which definitely runs.
        /// </summary>
        internal void Tick()
        {
            EnsureOverlayDrawer();

            // Fallback in case the Init postfix didn't run (e.g. plugin loaded after Steam init).
            if (!_configApplied && SteamClient.IsValid)
            {
                try { ApplyNetworkConfig(); }
                catch (Exception e) { Log.LogError(e.ToString()); }
            }

            if (_configApplied && VerboseLogging)
            {
                _statusTimer += Time.unscaledDeltaTime;
                if (_statusTimer >= 3f)
                {
                    _statusTimer = 0f;
                    DumpConnectionStatus();
                }
            }

            // F8 toggles the net overlay (new Input System, so it works even if legacy input is off).
            try
            {
                var kb = Keyboard.current;
                if (kb != null && kb.f8Key.wasPressedThisFrame) ShowNetOverlay = !ShowNetOverlay;
            }
            catch { }

            // Refresh per-peer ping/route a few times a second (DetailedStatus allocates, so not every
            // frame). Done whenever connected - not only when the overlay is visible - so the ping-bug
            // fix also applies to the lobby/Play-Online ping readout.
            _overlayTimer += Time.unscaledDeltaTime;
            if (_overlayTimer >= 0.5f) { _overlayTimer = 0f; UpdatePeerStatus(); }
        }

        // The plugin's own OnGUI isn't ticked either, so we host it on our own GameObject (which is),
        // re-creating it if the game ever tears it down.
        private void EnsureOverlayDrawer()
        {
            if (_overlayDrawer != null) return;
            var go = new GameObject("BoplFixOverlay");
            DontDestroyOnLoad(go);
            _overlayDrawer = go.AddComponent<OverlayDrawer>();
        }

        // Refreshes each peer's ping + route from the connection's real Steam status, for the F8 overlay.
        // Runs whenever connected.
        private void UpdatePeerStatus()
        {
            try
            {
                var mgr = SteamManager.instance;
                var lines = new List<string>();
                if (mgr != null && mgr.connectedPlayers != null)
                {
                    foreach (var c in mgr.connectedPlayers)
                    {
                        if (c == null) continue;
                        if (!c.Connected) { lines.Add($"{Trim(c.steamName)}:  connecting..."); continue; }
                        string ds = null;
                        try { ds = c.Connection.DetailedStatus(); } catch { }
                        int pingMs = ParsePingMs(ds);   // real Steam-measured RTT
                        if (pingMs < 0)
                        {
                            float ap = 0f; try { ap = c.Ping; } catch { }
                            if (ap > 0f && ap < 5f) pingMs = (int)(ap * 1000f);
                        }
                        string route = "?"; try { route = ClassifyRoute(ds); } catch { }
                        string pingStr = pingMs >= 0 ? $"{pingMs,4} ms" : "  -- ms";
                        lines.Add($"{Trim(c.steamName)}:  {pingStr}   {route}");
                    }
                }
                _overlayLines = lines.ToArray();
            }
            catch (Exception e) { Log.LogDebug("UpdatePeerStatus: " + e.Message); }
        }

        private static string Trim(string s)
        {
            if (string.IsNullOrEmpty(s)) return "peer";
            return s.Length > 16 ? s.Substring(0, 16) : s;
        }

        // Best-effort classification of a connection's DetailedStatus() string into DIRECT vs RELAY.
        // The decisive signal for the ACTIVE route is Steam's "Remote host is in data center 'lax'"
        // line (relay); a private address means a genuine LAN route. (Backup candidates can mention
        // both SDR and ICE, so those keywords alone aren't reliable.)
        private static string ClassifyRoute(string ds)
        {
            if (string.IsNullOrEmpty(ds)) return "?";
            if (ds.IndexOf("data center", StringComparison.OrdinalIgnoreCase) >= 0) return "RELAY";
            if (Regex.IsMatch(ds, @"\b(192\.168\.|10\.|172\.(1[6-9]|2[0-9]|3[01])\.)\d")) return "DIRECT (LAN)";
            if (ds.IndexOf("ICE", StringComparison.OrdinalIgnoreCase) >= 0) return "DIRECT";
            return "direct?";
        }

        /// <summary>Called from OverlayDrawer.OnGUI (the plugin's own OnGUI isn't ticked in this game).</summary>
        internal void DrawOverlay()
        {
            if (!ShowNetOverlay) return;
            if (_overlayStyle == null)
            {
                _overlayStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, richText = true };
                _overlayHeaderStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold };
                _overlayHeaderStyle.normal.textColor = new Color(0.6f, 0.85f, 1f);
            }

            int n = _overlayLines.Length;
            float w = 320f, h = 26f + Mathf.Max(1, n) * 20f;
            var box = new Rect(10, 10, w, h);
            var prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = prev;

            GUI.Label(new Rect(box.x + 8, box.y + 4, w - 16, 18), "BoplFix net  (F8)", _overlayHeaderStyle);
            if (n == 0)
            {
                GUI.Label(new Rect(box.x + 8, box.y + 24, w - 16, 18), "no peers connected", _overlayStyle);
                return;
            }
            for (int i = 0; i < n; i++)
            {
                string line = _overlayLines[i];
                string colored = line.Contains("DIRECT")
                    ? $"<color=#7CFC00>{line}</color>"
                    : (line.Contains("RELAY") ? $"<color=#FFD23F>{line}</color>" : line);
                GUI.Label(new Rect(box.x + 8, box.y + 24 + i * 20, w - 16, 18), colored, _overlayStyle);
            }
        }

        private void ApplyNetworkConfig()
        {
            if (_configApplied) return;
            if (!SteamClient.IsValid) return;
            _configApplied = true;

            // Route Steam's own networking diagnostics into our log so the transport
            // (ICE/direct vs SDR/relay) is visible.
            try
            {
                if (VerboseLogging)
                {
                    SteamNetworkingUtils.OnDebugOutput -= OnSteamDebug;
                    SteamNetworkingUtils.OnDebugOutput += OnSteamDebug;
                    SteamNetworkingUtils.DebugLevel = (NetDebugOutput)6; // Verbose
                }
            }
            catch (Exception e) { Log.LogWarning("Could not enable Steam debug output: " + e.Message); }

            if (!_enableDirectP2P.Value)
            {
                Log.LogWarning("EnableDirectP2P=false; leaving Steam's relay-only default. LAN will stay slow.");
                return;
            }

            // Prime the relay/ICE subsystem early so candidate gathering starts ASAP.
            try { SteamNetworkingUtils.InitRelayNetworkAccess(); } catch { }

            bool ok = SetSteamConfigInt(CfgP2PTransportIceEnable, IceEnableAll);
            if (_preferDirectOverRelay.Value)
                SetSteamConfigInt(CfgP2PTransportIcePenalty, 0);

            // SECOND HALF OF THE FIX. Without a STUN server list, Steam never gathers ICE
            // candidates, so a direct/NAT-pierced route is never even attempted and the
            // connection goes straight to the SDR relay. These servers are only used to
            // DISCOVER candidate addresses; actual LAN game traffic stays on the LAN.
            if (!string.IsNullOrWhiteSpace(_stunServers.Value))
            {
                bool stunOk = SetSteamConfigString(CfgP2PStunServerList, _stunServers.Value.Trim());
                Log.LogInfo($"P2P_STUN_ServerList set to '{_stunServers.Value.Trim()}' => {stunOk}. (This activates ICE candidate gathering for direct connections.)");
            }

            // When troubleshooting, un-filter the P2P rendezvous/ICE channel so the log shows candidate
            // gathering and the direct-vs-relay negotiation (it's suppressed by default).
            if (VerboseLogging)
            {
                SetSteamConfigInt(CfgLogLevelP2PRendezvous, 6);
                Log.LogInfo("Verbose networking logging is ON (set [Diagnostics] VerboseLogging=false for normal play).");
            }

            int readback = GetSteamConfigInt(CfgP2PTransportIceEnable);
            if (ok)
                Log.LogInfo($"Direct P2P (ICE) ENABLED globally. P2P_Transport_ICE_Enable readback = 0x{readback:X8}. "
                          + "LAN peers should now connect directly. Make sure BOTH players run this mod.");
            else
                Log.LogError("FAILED to set P2P_Transport_ICE_Enable. The LAN fix did NOT apply (see earlier errors).");
        }

        // ---- reflection into Facepunch.Steamworks internals (SetConfigInt/GetConfigInt are internal) ----
        private static MethodInfo _setConfigInt;
        private static MethodInfo _getConfigInt;
        private static MethodInfo _setConfigString;
        private static Type _netConfigType;

        private static bool EnsureReflection()
        {
            if (_setConfigInt != null && _netConfigType != null) return true;
            var utils = typeof(SteamNetworkingUtils);
            _netConfigType = utils.Assembly.GetType("Steamworks.NetConfig");
            _setConfigInt = utils.GetMethod("SetConfigInt", BindingFlags.NonPublic | BindingFlags.Static);
            _getConfigInt = utils.GetMethod("GetConfigInt", BindingFlags.NonPublic | BindingFlags.Static);
            _setConfigString = utils.GetMethod("SetConfigString", BindingFlags.NonPublic | BindingFlags.Static);
            if (_setConfigInt == null || _netConfigType == null)
                Log.LogError("Reflection setup failed: Steamworks.NetConfig or SteamNetworkingUtils.SetConfigInt not found. "
                           + "Has the game's Facepunch.Steamworks build changed?");
            return _setConfigInt != null && _netConfigType != null;
        }

        private static bool SetSteamConfigString(int configId, string value)
        {
            try
            {
                if (!EnsureReflection() || _setConfigString == null) { Log.LogError("SetConfigString not found via reflection."); return false; }
                object cfg = Enum.ToObject(_netConfigType, configId);
                object res = _setConfigString.Invoke(null, new object[] { cfg, value });
                return res is bool b && b;
            }
            catch (Exception e) { Log.LogError($"SetSteamConfigString({configId}) threw: {e}"); return false; }
        }

        private static bool SetSteamConfigInt(int configId, int value)
        {
            try
            {
                if (!EnsureReflection()) return false;
                object cfg = Enum.ToObject(_netConfigType, configId);
                object res = _setConfigInt.Invoke(null, new object[] { cfg, value });
                bool ok = res is bool b && b;
                Log.LogInfo($"SetConfigInt(id={configId}, value=0x{value:X}) => {ok}");
                return ok;
            }
            catch (Exception e) { Log.LogError($"SetSteamConfigInt({configId}) threw: {e}"); return false; }
        }

        private static int GetSteamConfigInt(int configId)
        {
            try
            {
                if (!EnsureReflection() || _getConfigInt == null) return -1;
                object cfg = Enum.ToObject(_netConfigType, configId);
                object res = _getConfigInt.Invoke(null, new object[] { cfg });
                return res is int i ? i : -1;
            }
            catch { return -1; }
        }

        private static void OnSteamDebug(NetDebugOutput type, string msg)
        {
            Log.LogInfo($"[Steam:{type}] {msg}");
        }

        private void DumpConnectionStatus()
        {
            try
            {
                var mgr = SteamManager.instance;
                if (mgr == null || mgr.connectedPlayers == null || mgr.connectedPlayers.Count == 0) return;

                foreach (var c in mgr.connectedPlayers)
                {
                    if (c == null || !c.Connected) continue;
                    string ds = null;
                    try { ds = c.Connection.DetailedStatus(); } catch { }
                    if (!string.IsNullOrEmpty(ds))
                        Log.LogInfo($"[Route] peer '{c.steamName}':\n{ds}");
                }
            }
            catch (Exception e) { Log.LogDebug("DumpConnectionStatus: " + e.Message); }
        }
    }
}
