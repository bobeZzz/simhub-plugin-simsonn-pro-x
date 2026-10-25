# SIMSONN Pedals: plan and status

Living plan for the SimHub plugin that replaces Simsonn Manager for the SIMSONN Pro X Ultra throttle and brake.
Update it when a phase starts, finishes or changes. Last updated 2026-10-25 (v1.0.24).

## Where we stopped (2026-10-25)
- **Direct mode (no vJoy) is complete**: D1–D3 checked on the rig (1.0.20–1.0.22); D4–D6 built in 1.0.23, rig
  check pending. Firmware 2.1.1 on the rig.
- **Next: rig checks** in [TESTING.md](TESTING.md): 1.0.23 (direct mode D4–D6) first, then the vJoy phases
  (1.0.15–1.0.19 and older). Fixes from those come next; nothing else is planned.
- **1.0.24 reorganised the page** into a status + active preset header and tabs (Pedals, Shift assist, Presets,
  Output and game, Pedal setup, Updates); rig look-over pending in TESTING.md.

## Where things are
- Repo: https://github.com/bobeZzz/simhub-plugin-simsonn-pro-x (public, `main`; git user bobeZzz)
- Project folder (dev PC): `D:\stasiks-rest\claude\SimsonnPedals`
- SimHub: dev PC `D:\stasiks`, rig `C:\Program Files (x86)\SimHub`; plugin dumps and reports go to
  `<SimHub>\PluginsData\SimsonnPedals` (**Open folder** on the page)
- Rig test data from Simsonn Manager: `D:\stasiks-rest\claude\rig-data-from-simsonn-manager` (copied into `research/`)
- Settings block map and all test rounds: [research/pedal-settings-layout.md](research/pedal-settings-layout.md)
- How Simsonn Manager talks to the pedals (from its binaries: report 6 commands, params report, calibration):
  [research/manager-protocol.md](research/manager-protocol.md). Manager install copy: `D:\stasiks-rest\claude\simsonn-manager`
- Direct mode research, rig results and the firmware's pause timer: [research/direct-mode.md](research/direct-mode.md);
  rig files in `research/d1-rig`; offline export check [research/export-test.ps1](research/export-test.ps1)
- Firmware images (decrypted 2.0.8 / 2.1.1) and Capstone scripts: the session scratchpad (`fw/`, `fw211/`,
  `fwrefs.py`, `fwscan*.py`); regenerate from the Manager's `.sfu` files if gone (research/firmware-update.md)

## How we work
- The user tests on the rig. They update from the plugin page (**Plugin updates**), so every rig test needs a release.
- Release: commit and push `main`, then `.\release.ps1 -Version x.y.z` (sets the version, builds, tags, publishes the DLL).
- Rig checks waiting for the user live in [TESTING.md](TESTING.md): every release adds its checks there; passed ones
  move to its Passed list.
- Research on pedal settings: one change in Simsonn Manager per **Read pedal settings** read, labelled with the change;
  diff each read against the previous one.

## Decisions (keep unless the user changes them)
- Two output modes, chosen on the page (**Output and game** tab): **vJoy** (the plugin shapes, the game binds to vJoy) and
  **direct** (the plugin writes the preset into the pedals' own settings, the game binds to the pedals). vJoy mode
  stays as it was; direct mode is not a replacement.
- vJoy mode: shaping (dead zone, curve, max force scaling) happens **live in the plugin**; the pedals are set up once
  to neutral (linear, 0–100, filter off, high max force) with **Set up pedals**.
- Direct mode: writes erase the pedals' settings flash and freeze their input for 2 s, so they happen only when the
  preset switches, on Save and on Write to pedals, never per edit, and are skipped when nothing changed. Leaving
  direct mode writes the pedals back to neutral.
- **No smoothing filter** in the plugin (removed in 1.0.2).
- Presets: automatic lookup car → class → game → Default.
- Edits apply immediately and are autosaved, but stay "unsaved" until **Save**; **Revert** returns to the last Save.
- Brake: "Pedal max" = max force stored in the pedals; "100% at" = the user's real 100%.
- GIF at the top left of the page, in the status header. Ignore interface language, the dashboard and housekeeping features.
- Throttle cut in vJoy mode is done by the plugin (phase 7) and the firmware's own (`brakeCutoff`) is kept off, so the
  pedals never cut throttle behind the plugin's back. In direct mode the firmware's is the one to use (D5).
- Elastomer (report 6 `damping_type`) is not supported: in the Manager it only caps the max force (60 / 100 / 140 kg)
  and its effect in the firmware is unknown.

## Done
| Phase | What | Version |
|---|---|---|
| 1 | Plugin core: DirectInput read, calibration, dead zone, curve editor, brake max force scaling, vJoy output, per-game presets, bind-in-game sweep, autosave, header GIF | 1.0.0 |
| 1 | USB replug reconnect (fresh DirectInput per attempt, attached check, settle delay) + Reconnect button | 1.0.0 |
| – | GitHub releases + in-plugin updater (sha256 + version check, swap while loaded, elevated fallback, Restart SimHub) | 1.0.0–1.0.1 |
| – | Filter removed | 1.0.2 |
| F2 | Settings block mapped: dead zones, output ends, calibration (raw min), max force (raw max = raw min + 300 × kg), curve points, filter bits | 1.0.2–1.0.3 |
| F4 | Write to pedals (FreeJoy CONFIG_OUT), rewrite test verified on the rig | 1.0.3 |
| F4 | **Set up pedals** (linear, 0–100, filter off, brake max force; keeps calibration), verified on the rig | 1.0.4 |
| 2 | Save / Revert with unsaved marker; Save as moves edits to the new preset | 1.0.5 (rig check pending) |
| – | Max force units/kg from the firmware version (300 for 2.0.x, 148 for 2.1.x, refuse others) | 1.0.6 |
| F5 | **Read pedal settings** also reads throttle cut, elastomer, raw sensors (report 6) and 3 s of params reports; rig-checked: report 6 and params layout confirmed, firmware throttle cut found on at 10 % | 1.0.6 |
| F5 | **Set up pedals** switches the firmware throttle cut off (report 6 `brakeCutoff`); verified on the rig: no restart, both pedals work together, stays off after SimHub restart and replug | 1.0.7 |
| 8 | **Calibrate pedals**: live raw values from the params report (`PedalRawMonitor`), Start at rest → press → Finish; one block write (throttle raw range, brake rest, then Set up pedals); plugin calibration of all presets reset to full range. Rig run 20:46: written and verified; throttle raw −32767…−8522, brake rest −30341 → −30239, 85 kg kept, only the expected bytes changed | 1.0.8 |
| 9 | **Firmware update** from the plugin with the Manager's own files and key (CRC-checked): main chip via SIMSONN Flasher report 5, second chip via report 7, resume from update mode, rollback file choice. Rig: 2.0.8 → 2.1.1 / 2.0.6. 2.1.x support: pedal axes and raw channels found from the block (3–5 on 2.1), presets switched to Rx/Ry; calibration on 2.1.1 verified, bars move. See [research/firmware-update.md](research/firmware-update.md) | 1.0.9–1.0.12 |
| 3 | **Per-car and per-class presets**: automatic lookup car → class → game → Default; Save for <car> / <class>; a line shows the automatic choice and why. iRacing class read from the raw session info (`CarClassID`), since SimHub's CarClass is blank in single-class sessions. Rig-checked | 1.0.13–1.0.14 |
| 4 | **Clutch panel**: "Clutch pedal connected" toggle (off by default); full panel (calibration, dead zone, curve, bind sweep) on vJoy Z; reads Z on 2.0.x, Rz on 2.1.x. Shaped in the plugin only: the clutch axis in the pedals is not touched by Set up / Calibrate pedals. Rig check pending | 1.0.15 |
| – | **Brake stuck after SimHub exits**: exit guard, a small exe embedded in the DLL and started when vJoy is acquired; it waits for SimHub to exit (any way, incl. crash and Task Manager), then acquires the vJoy device and sets every axis to 0. Log: `%LocalAppData%\SimsonnPedals\exit-guard.log`. Tested on the dev PC without vJoy; rig check pending | 1.0.16 |
| 5 | **iRacing binding check**: reads `Documents\iRacing\controls.cfg` and `joyCalib.yaml`; warns when throttle / brake / clutch are unbound, bound to the pedals directly, to another device, or to a different vJoy axis than the preset sends. Rechecked when iRacing saves its controls. Rig check pending | 1.0.17 |
| 6 | **Exact curves**: the firmware maps raw linearly between the output ends (which hold the dead zones), then joins the 11 curve points with straight lines, the same as the plugin. **Import Manager preset… / Import from pedals** convert the Manager's settings into the active preset exactly (also covers F3). See [research/curves.md](research/curves.md). Rig check pending | 1.0.18 |
| 7 | **Throttle cut and blip**, per preset: SimHub actions `SimsonnPedalsPlugin.Upshift` / `Downshift` cut the throttle for a set time or blip it to a set level; optional cut while brake output ≥ a level. vJoy mode only. Logic checked offline; rig check pending | 1.0.19 |
| F5 | Manager protocol read from its binaries (static only). See [research/manager-protocol.md](research/manager-protocol.md) | – |
| D1 | **Direct mode, risky parts.** Firmware (2.0.8, 2.1.1): block writes save to flash and reload live, no reset; `damping_max` changes the brake's raw max / high end live and saves; `brakeCutoff` live, capped at 50 %. Rig tests (Live write, Live max force, Firmware brake cut): all live, pedals stay connected, settings put back exactly | 1.0.20 |
| D2 | **Preset → settings block** (`PedalExport`): calibration, invert, brake force and dead zone folded into the output ends; curve as whole-number points; invert = mirrored curve + firmware invert bit; only shaping bytes change. Offline on 2.1.1 and 2.0.8 dumps: within 0.09 % of vJoy mode, no stacking, default preset needs no write | – |
| D3 | **Output mode and writing**: Output vJoy / directly; direct mode releases vJoy, bars show the pedals' output, panels' Calibrate off, shift cut / blip off. `DirectWriter` reads once, then writes from that block (`WriteKnown`) on mode on, preset switch, Save, Write to pedals (reads first); "Edits not in the pedals yet" marker; holds during other pedal operations; re-reads after replug; retries; back to vJoy writes neutral; write counter. Rig: works end to end, same feel as vJoy mode. **Write pause test**: a read freezes joystick reports 2074 ms, a write 2050 ms (firmware timer, see Known facts) | 1.0.21–1.0.22 |
| D4 | **Safe writes**: while a game runs, reads and writes wait until the car is stopped (< 1 km/h) and throttle and brake are released; amber "Waiting to send … (the car is moving / release throttle and brake)"; sent from SimHub's data loop once safe. Rig check pending | 1.0.23 |
| D5 | **Live brake and brake cut**: brake "100% at" edits go out half a second after the last change with `damping_max` (0.2 s pause) when only the brake's high end changes; the preset's "Cut throttle when braking" is the firmware's `brakeCutoff` in direct mode (capped at 50 %), sent with each pass; leaving direct mode switches it off. Offline: only the brake's centre/high end bytes change; display bytes (+34/+35) ignored when comparing. Rig check pending | 1.0.23 |
| D6 | **Page and binding check**: direct mode hides the vJoy status, the panels' "Bind in game" row and the shift cut / blip rows (brake cut row stays, with a note); the binding check expects each pedal on the SIMSONN pedals' own axis (Rx/Ry/Rz on 2.1.x) and names vJoy or wrong-axis bindings. Offline on synthetic controls.cfg. Rig check pending | 1.0.23 |
| UI | **Page layout**: fixed header with pedal / vJoy / direct / binding status and the active preset (Save, Revert, unsaved and automatic-choice lines); tabs Pedals, Shift assist (Brake cut in direct mode), Presets, Output and game, Pedal setup (pedal settings, firmware, tests folded away), Updates ("Update available" in the tab name). Pedal panels follow the signal chain: live bars, force, dead zone, curve; calibration, bind and axes in a "Calibration and binding" expander. Rendered offline with SimHub's styles; rig look-over pending | 1.0.24 |

## Next, in order
1. **Rig check 1.0.23** (direct mode D4–D6), see TESTING.md.
2. **Rig checks still open from the vJoy phases**, below.
3. Fixes from both. Nothing else planned; ideas under Maybe / parked.
4. **TODO: remove the old releases (1.0.0–1.0.24).** Their DLLs still carry the author's real name (the
   `PluginAuthor` attribute; git history was rewritten to bobeZzz on 2026-10-25, tags force-pushed). Do it once a
   newer release (1.0.25+) is out so the updater always has a latest release; deleting a release keeps its tag.

### Rig checks still open (vJoy phases)
Listed in [TESTING.md](TESTING.md): clutch panel (1.0.15), exit guard (1.0.16), binding check (1.0.17), Manager import
(1.0.18), throttle cut and blip (1.0.19), and older ones (Save/Revert, brake max force on 2.1.1, rebinding after 2.1.1).

Flash wear: the STM32 page is good for about 10 000 erases; writes are skipped when nothing changed and counted in the
settings (`DirectWriteCount`, shown on the page).

## Maybe / parked
- Direct mode: the panels' bars show the pedals' output only; pedal travel could come from the params report (raw
  sensors, no freeze) if wanted.
- Direct mode: brake "100% at" goes live only with no brake start dead zone (`damping_max` sets the high end, not the
  low end); with one it waits for Save. Could send the low end too through a full write when stopped.
- Cache the pedals' block on disk so direct mode needn't read (2 s freeze) at every SimHub start; re-read only if
  the cached one doesn't match the firmware version or after a replug.
- Avoid the re-read after the direct-mode tests that put everything back themselves.

## Known facts worth remembering
- USB VID_DDFD PID_6012. Interface MI_00 = joystick (report 1, 500/s); MI_01 = settings (reports 2–6, 64 bytes).
- FreeJoy-derived firmware. Settings block 1116 bytes = 18 packets × 62. Axis i at 0x40 + 52·i. Pedals on axes 0–2
  (throttle, brake, clutch) on 2.0.x, axes 3–5 on 2.1.x.
- Read: `[3, n]` → `[3, n, 62 bytes]`. Write: `[4, 0]` → `[4, 1]`, then `[4, n, 62 bytes]` → `[4, n+1]`; after the last
  packet the pedals save to flash and reload live, with no answer (no restart). `[4, 0xFE]` = version mismatch.
- **Every config packet (reads 3, writes 4, flash 5) freezes the joystick reports for 2 s after the last packet;
  report 6 commands for 200 ms; params requests (2) not at all** (firmware 2.1.1 `0x08012ac0` sets the report timer
  to `millis() + 2000` / `+ 200`). The game sees the last values held meanwhile.
- Right after Simsonn Manager saves, the pedals may not answer the first read; the reader retries 4 times.
- Report 6 = command channel: `[6, "name", payload]`, replies `[6, "OK"…]` / `[6, "BCOK"…]` / `DMOK`. Report 2 =
  params: host sends `[2, 0]` (repeat < 5 s), device sends raw sensor values (channel i int16 at packet 0 byte 6 + 2i).
- Axis fields are FreeJoy's: +0/+2/+4 CalibMin/Center/Max (our output ends), +6/+8 old_calib_min/max (our raw range),
  +10 bit 1 invert, +11…+21 curve, +34/+35 the Manager's dead zone percentages (display only).
- Max force: raw_max = raw_min + kg × units/kg ÷ (1 − start dead zone); units/kg = 300 (fw ≤ 2.0.9) or 148 (fw ≥ 2.1.0).
- SimHub is 32-bit; the plugin is net48 AnyCPU. SimHub asks for approval whenever the plugin DLL changes.
- Windows PowerShell 5.1 adds a BOM to piped stdin: `release.ps1` feeds `git credential fill` from an ASCII file.
- PowerShell test scripts: seed maxima with `0.0`, not `0` (`[Math]::Max` picks the int overload).
