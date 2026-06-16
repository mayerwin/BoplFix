# Testing BoplFix

## A) LAN / direct-P2P fix (needs 2 PCs)

Confirms two PCs on the same network connect **directly** (~1-5 ms) instead of via a distant Steam
relay (~250 ms).

> **Both players must install the mod.** Direct (ICE) connections require *both* peers to offer
> their LAN address; with only one side modded, Steam still falls back to the relay.
>
> The two PCs must use **different Steam accounts**, both owning Bopl Battle (the free demo also works
> for a quick test).

### Install (BOTH PCs)
- **Easiest:** extract `BoplFix-v1.0.0.zip` into the game folder (the one with `BoplBattle.exe`).
  Right-click *Bopl Battle -> Manage -> Browse local files* to find it. Launch once.
- **If you already have BepInEx:** drop `BoplFix.dll` into `BepInEx\plugins\`.

### Run
1. Both players launch Bopl Battle.
2. One hosts a private lobby; invite the other (Steam friends) and join.
3. Start an online match and play a round.

### Confirm it worked

**Easiest - the in-game overlay:** press **F8** during the lobby or a match. The top-left overlay shows
each peer and **DIRECT (LAN)** in green (with ~1-5 ms) or **RELAY** in yellow (with ~100-250 ms). Green =
fixed. (Note: a connection often starts on RELAY and flips to DIRECT a few seconds after ICE finishes
negotiating, so give it a moment at the start of a match.)

**Or via the log** (more detail) - first set `VerboseLogging = true` in
`BepInEx\config\com.boplfix.cfg` (logging is off by default), then open
`...\Bopl Battle\BepInEx\LogOutput.log` on either PC (updates live).

1. **ICE engine installed + fix loaded** - near the top:
   ```
   Enabled direct LAN play: copied steamwebrtc64.dll into the game folder ...
   Direct P2P (ICE) ENABLED globally. P2P_Transport_ICE_Enable readback = 0x7FFFFFFF
   ```
   (If `steamwebrtc64.dll` was already in the game folder, the first line is skipped - that's fine.)
2. **No ICE failure** - during a match you should **not** see `Failed to load steamwebrtc64.dll` or
   `No ICE session factory`. Those were the original bug: without Steam's ICE engine, every connection
   falls back to the relay.
3. **Connection is direct** - once in a match, the `[Route]` blocks (every few seconds) should show:
   - ping **~1-5 ms** (was ~250 ms)
   - **no** `Remote host is in data center '...'` line - that line means the route is relayed; a direct
     route instead shows the peer's address (ideally a **local IP**, `192.168.x.x` / `10.x.x.x`).
4. **The feel** - no more rubber-banding/stutter; movement is crisp.

If it still relays: confirm **both** PCs show the "ENABLED globally" line and **no** `Failed to load
steamwebrtc64.dll`, then send both the `[Route]` and `[Steam:...]` lines.

When you're done, set `VerboseLogging = false` again for quiet normal play. (The fix stays on.)

---

## B) Keybinding persistence (1 PC, keyboard)

1. Launch, go to local/online play and join character-select with **keyboard/mouse**.
2. Use the **rebind keys** option and set your controls to something non-default (e.g. swap jump).
3. Fully **quit** the game and relaunch.
4. Join character-select again and start a match - your custom keys should be in effect immediately,
   without re-binding.

The log shows `Saved custom keyboard/mouse keybindings: ...` when you finish a rebind, and
`Applied saved keyboard/mouse keybindings for player N.` at the start of the next match.

> Scope: keyboard/mouse only. Gamepad binds aren't persisted (controller device IDs are unstable
> across reconnects). Set `SaveKeybindings = false` in the config to disable.

---

## C) Default powerup persistence (1 PC)

1. In character-select, choose abilities/powerups different from the defaults, then ready up / play.
2. Quit and relaunch.
3. Join character-select again - your previously chosen abilities should already be selected.

Set `SaveDefaultPowerups = false` in the config to disable.

> If the game's ability list/order ever changes in a future update, a saved index could map to a
> different ability; just re-pick once and it re-saves.
