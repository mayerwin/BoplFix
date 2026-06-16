# Bopl Battle Fixes (BoplFix)

An unofficial [BepInEx](https://github.com/BepInEx/BepInEx) mod for **Bopl Battle** that restores
fast **direct LAN / peer-to-peer** play and adds a few quality-of-life fixes.

> **TL;DR:** Online matches - even two PCs in the same room - get forced through a distant Steam relay,
> causing a constant ~250 ms ping and rubber-banding. This mod makes the game connect **directly**, so a
> LAN game drops back to ~1 ms. It also remembers your **keybindings** and **default powerups** between
> launches.

---

## Features

| Fix | What it does |
| --- | --- |
| **Direct LAN / P2P** | Restores direct (ICE) connections instead of relay-only, so peers on the same network connect directly (~1 ms instead of ~250 ms). The Steam relay stays as a fallback for normal internet play. |
| **Keybinding memory** | Remembers your custom **keyboard/mouse** keybindings across launches (the game normally forgets them every time). |
| **Default powerup memory** | Remembers your last chosen abilities/powerups and pre-selects them next launch. |
| **In-game net overlay** | Press **F8** in an online match to show each peer's live ping and whether the connection is **DIRECT (LAN)** or via **RELAY** - so you can see the fix working. |
| **"Mod loaded" badge** | A small `+ BoplFix` badge above the main-menu version number, so it's obvious the mod is active. |

Everything is toggleable in the config file, and all logging is **off by default**.

---

## Why LAN was broken (root cause)

Bopl Battle uses Steam's modern `SteamNetworkingSockets` P2P API (`CreateRelaySocket` / `ConnectRelay`).
For two peers to connect **directly**, Steam uses **ICE** (the same NAT-traversal tech as WebRTC), and ICE
is implemented in a separate native library, **`steamwebrtc64.dll`**. Three things were wrong:

1. **The ICE engine couldn't even load.** Bopl never shipped `steamwebrtc64.dll` next to its own
   `steam_api64.dll`, and Steam doesn't add its own install folder to the game's DLL search path. So
   Steam's networking failed to load the engine (`ICE failed: Failed to load steamwebrtc64.dll` /
   `No ICE session factory`) and **every** connection - including same-room LAN - silently fell back to a
   Steam Datagram Relay server (`Remote host is in data center 'lax'`, ~228 ms). **No amount of config can
   fix this**, which is why it looks unfixable: there is no engine to do direct connections with.
2. **ICE wasn't enabled.** Even with the engine present, the game never sets `P2P_Transport_ICE_Enable`,
   and on Steam the default is relay-only (IP addresses aren't shared between peers - an anti-DoS measure).
3. **No STUN server list.** Valve's docs note *"if you set this to an empty string, NAT piercing will not
   be attempted"*, and it's empty by default, so ICE never gathers candidate addresses.

The mod fixes all three: it **copies `steamwebrtc64.dll` from your Steam client into the game folder** (so
the engine loads), then sets `P2P_Transport_ICE_Enable = All` and a `P2P_STUN_ServerList`. The game's last
build is from mid-2025, so the regression was triggered by a Steam-side change, not a game patch.

This is purely a *transport* change - packet contents are identical, so it **cannot** cause desyncs in the
game's lockstep netcode. The STUN servers are only used to *discover* addresses; actual LAN traffic stays
on the LAN.

---

## Requirements

- Bopl Battle (Steam) on Windows (64-bit), launched through Steam at least once (so `steamwebrtc64.dll`
  exists in your Steam client folder - the mod copies it from there).
- For the **LAN fix**, *both* players must install the mod - direct (ICE) connections require both peers
  to have a working ICE engine and to offer their addresses. (The other fixes are local and don't require
  the other player to have the mod.)
- Two different Steam accounts (you can't run one account on two PCs at once).

---

## Install

Do this on **BOTH PCs** for the LAN fix. Pick whichever option you like - all three install the same thing.

### Option 1 - installer (easiest)
Download and run **`BoplFix-Installer.exe`** (~0.8 MB). It finds your Bopl Battle install automatically,
plus the free **Bopl Battle Demo** if you have it, and installs into each, asks you to confirm first. If a
copy sits in a protected folder (e.g. *Program Files*, where the Demo usually lives) it automatically
requests administrator rights (UAC). Close the game first if it's open.

> It uses Windows' built-in PowerShell, so there's nothing else to install. Being unsigned, Windows
> SmartScreen may show *"Windows protected your PC"* - click **More info -> Run anyway**. If your
> antivirus is strict about self-extracting installers, just use Option 2 instead (same mod).

### Option 2 - one-click bundle (no installer)
1. Close Bopl Battle.
2. Open the game folder: in Steam, right-click **Bopl Battle -> Manage -> Browse local files**
   (the folder containing `BoplBattle.exe`).
3. Extract everything from **`BoplFix-v1.0.0.zip`** into that folder. When done, `winhttp.dll` and a
   `BepInEx` folder sit next to `BoplBattle.exe`.
4. Launch the game once. Done.

### Option 3 - just the plugin (if you already have BepInEx 5 x64)
Drop **`BoplFix.dll`** into `BepInEx\plugins\`.

> The three downloads are the same mod in different wrappers: the **installer** does it for you, the
> **zip** is the mod plus the BepInEx loader to extract manually, and the **dll** is only the mod (for
> people who already run BepInEx).

The mod copies `steamwebrtc64.dll` into the game folder automatically on first launch - you don't need to
find or copy any DLL yourself.

---

## Uninstall

**Easiest:** run **`BoplFix-Installer.exe`** again. When it detects the mod is already installed it asks
*Reinstall / **Uninstall** / Cancel* - choose **Uninstall**. It removes the mod, and if BoplFix was your
only BepInEx mod it removes the BepInEx loader too, returning the game to vanilla. If you have **other
BepInEx mods** (even ones tucked in their own subfolder), it leaves BepInEx and those mods alone, and only
does a full vanilla reset if you explicitly confirm. (It auto-elevates if the folder needs admin rights.)

**Manually:** delete `BepInEx\plugins\BoplFix.dll` (and `BepInEx\config\com.boplfix.cfg`). To
remove the loader entirely as well, also delete the `BepInEx` folder, `winhttp.dll`, `doorstop_config.ini`
and `.doorstop_version` from the game folder - but only if you have no other BepInEx mods. (Leaving the
copied `steamwebrtc64.dll` behind is harmless - it just lets Steam do direct connections.)

---

## Configuration

After the first launch, settings are at `BepInEx\config\com.boplfix.cfg`:

```ini
[Netcode]
EnableDirectP2P = true        # the LAN fix (copy ICE engine + enable ICE)
PreferDirectOverRelay = true  # bias toward the fast direct route
StunServerList = stun.l.google.com:19302,stun1.l.google.com:19302  # activates ICE candidate gathering

[QualityOfLife]
SaveKeybindings = true        # remember keyboard/mouse binds
SaveDefaultPowerups = true    # remember chosen abilities

[Badge]
ShowBadge = true              # "+ BoplFix" badge above the main-menu version number
BadgeSizePercent = 24         # badge size as a % of the version number
BadgeColorHex = 1B5E20        # badge colour (hex RGB, no '#'); e.g. 0A4DA0 blue, B5530A orange

[Diagnostics]
ShowNetOverlay = false        # in-game ping + DIRECT/RELAY overlay; press F8 in a match to toggle
VerboseLogging = false        # troubleshooting only - see below
```

### Enabling verbose logging (troubleshooting)

Logging is **off by default** for clean, quiet play. If a direct connection isn't forming and you want to
see why, set:

```ini
[Diagnostics]
VerboseLogging = true
```

Relaunch, reproduce the match, then read `BepInEx\LogOutput.log`. With it on, the mod writes the full
Steam ICE/relay negotiation plus a per-peer `[Route]` block (ping + route) every few seconds, which shows
exactly whether the connection went DIRECT or via RELAY and why. Set it back to `false` for normal play -
**the LAN fix itself is always on regardless of this setting.**

---

## Verifying the LAN fix

See [TESTING.md](TESTING.md) for the full 2-PC procedure and which log lines confirm a direct connection.
The quickest check: press **F8** during an online match and look for **DIRECT (LAN)** with a low ping.

---

## Build from source

Requires the .NET SDK. References resolve against your local game install.

```powershell
# default game path is D:\SteamLibrary\steamapps\common\Bopl Battle
dotnet build src/BoplFix/BoplFix.csproj -c Release

# different install location:
dotnet build src/BoplFix/BoplFix.csproj -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Bopl Battle"
```

The output `BoplFix.dll` lands in `src/BoplFix/bin/Release/`. On Windows, **`build.ps1`** produces all
three release artifacts in `dist/`: the plugin DLL, the one-click bundle zip, and the installer `.exe`
(built with the built-in IExpress, wrapping `installer/install.ps1` + the bundle - ~0.8 MB).

The mod targets `netstandard2.1` and references BepInEx, 0Harmony, and the game's
`Assembly-CSharp` / `Facepunch.Steamworks.Win64` / `UnityEngine` / `Unity.InputSystem` assemblies.

---

## How it works (technical)

- **LAN fix** - In `Awake`, `EnsureWebRtcDll()` copies `steamwebrtc64.dll` from the Steam client
  (located via `HKCU\Software\Valve\Steam\SteamPath`, with `%ProgramFiles%` fallbacks) into the game
  folder if it isn't already there, so Steam's networking can load the ICE engine. Then a Harmony postfix
  on `Steamworks.SteamClient.Init` sets the global config values `P2P_Transport_ICE_Enable = 0x7fffffff`,
  `P2P_Transport_ICE_Penalty = 0`, and `P2P_STUN_ServerList` via the Facepunch wrapper.
- **Keybindings** - Saved as `InputControl.path` strings (PlayerPrefs) when a rebind completes
  (`KeyBindButton.UpdateKeybind`), and re-applied for keyboard players in `InputUpdater.Init` if they
  haven't rebound this session.
- **Powerups** - The chosen ability index is saved (`SelectAbility.UpdatePlayerInit`) and pre-selected
  when a player freshly joins character-select (`CharacterSelectBox.OnEnterSelect`).
- **Overlay / badge** - The overlay reads each peer's `Connection.DetailedStatus()` to classify the route
  (a "data center" line = RELAY; a private IP = DIRECT/LAN). The badge is a standalone text element placed
  relative to the rendered version number in a postfix on `printText.Awake` (it never modifies the version
  label, and runs *after* `Constants.version` is captured, so lobby version-matching is untouched).

Per-frame work (overlay, F8, route logging) is driven from a Harmony hook on `SteamManager.Update`,
because this game never ticks a BepInEx plugin's own `Update()`/`OnGUI()`. The LAN-fix patch is applied
first and in isolation, so a failure in any other patch can never disable it.

---

## Compatibility & safety

- Does not modify any game files; it's a standard BepInEx plugin. (It does copy Valve's own
  `steamwebrtc64.dll` into the game folder to enable direct connections.)
- The LAN fix only changes how packets are *routed*, not their contents - safe for the deterministic
  lockstep netcode (no desyncs).
- Keybindings and powerup defaults are local UI state and are never sent over the network.

---

## Disclaimer

Unofficial and not affiliated with or endorsed by the developer of Bopl Battle. Provided as-is under
the MIT License (see [LICENSE](LICENSE)). It only contains original mod code - no game assets.
