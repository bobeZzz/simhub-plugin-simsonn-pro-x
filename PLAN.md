# SIMSONN Pedals: plan and status

Living plan for the SimHub plugin that replaces Simsonn Manager for the SIMSONN Pro X Ultra throttle and brake.
Update it when a phase starts, finishes or changes. Last updated 2026-10-24 (v1.0.16).

## Where things are
- Repo: https://github.com/bobeZzz/simhub-plugin-simsonn-pro-x (public, `main`; git user bobeZzz)
- Project folder (dev PC): `D:\stasiks-rest\claude\SimsonnPedals`
- SimHub: dev PC `D:\stasiks`, rig `C:\Program Files (x86)\SimHub`
- Rig test data from Simsonn Manager: `D:\stasiks-rest\claude\rig-data-from-simsonn-manager` (copied into `research/`)
- Settings block map and all test rounds: [research/pedal-settings-layout.md](research/pedal-settings-layout.md)
- How Simsonn Manager talks to the pedals (from its binaries: report 6 commands, params report, calibration):
  [research/manager-protocol.md](research/manager-protocol.md). Manager install copy: `D:\stasiks-rest\claude\simsonn-manager`

## How we work
- The user tests on the rig. They update from the plugin page (**Plugin updates**), so every rig test needs a release.
- Release: commit and push `main`, then `.\release.ps1 -Version x.y.z` (sets the version, builds, tags, publishes the DLL).
- Research on pedal settings: one change in Simsonn Manager per **Read pedal settings** read, labelled with the change;
  diff each read against the previous one.

## Decisions (keep unless the user changes them)
- Output goes through **vJoy**; the game binds to vJoy, not to the pedals.
- Shaping (dead zone, curve, max force scaling) happens **live in the plugin**. The pedals are set up once to neutral
  (linear, 0–100, filter off, high max force) with **Set up pedals**. Writes to the pedals restart them (USB drops),
  so they are never done per game or per car.
- **No smoothing filter** in the plugin (removed in 1.0.2).
- Presets: per game, auto-applied; later per car (lookup car → class → game → Default).
- Edits apply immediately and are autosaved, but stay "unsaved" until **Save**; **Revert** returns to the last Save.
- Brake: "Pedal max" = max force stored in the pedals; "100% at" = the user's real 100%.
- GIF at the top right of the page. Ignore interface language, the dashboard and housekeeping features.
- Throttle cut is done by the plugin (phase 7). The firmware has its own (report 6 `brakeCutoff`, outside the
  settings block); the plugin only switches it off, so the pedals never cut throttle behind the plugin's back.
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
| – | **Brake stuck after SimHub exits**: exit guard, a small exe embedded in the DLL and started when vJoy is acquired; it waits for SimHub to exit (any way, incl. crash and Task Manager), then acquires the vJoy device and sets every axis to 0. Log: `%LocalAppData%\SimsonnPedals\exit-guard.log`. Tested on the dev PC without vJoy (one guard per SimHub, sees the exit); rig check pending | 1.0.16 |
| F5 | Manager protocol read from its binaries (static only): report 6 commands, params report, calibration steps, kg scale per firmware, field names. See [research/manager-protocol.md](research/manager-protocol.md) | – |

## Next, in order
1. **Phase 5 – iRacing binding check**: warn when the game's pedals aren't bound to vJoy.
2. **Phase 6 – exact curves**: match Simsonn Manager's curve shapes exactly.
3. **Phase 7 – throttle cut / blip from the shift paddles**, plus cutting throttle above a brake level.

## Maybe / parked
- **Live brake max force** (report 6 `damping_max`, sent by the Manager while the spinner moves). If it applies without
  a restart, max force could change per car in the pedals. Not needed while scaling is done in the plugin; unknown
  whether it writes flash every time.
- **F3 – import Simsonn Manager presets.** Only worth it if the user has Manager presets to keep. Format is known now:
  INI with FreeJoy key names (`env\Default_*`, user presets in `<Manager>\etc\device\<name>\pedal.cfg`).

## Known facts worth remembering
- USB VID_DDFD PID_6012. Interface MI_00 = joystick (report 1); MI_01 = settings (reports 2–6, 64 bytes).
- Firmware 0x2080 (FreeJoy-derived). Settings block 1116 bytes = 18 packets × 62. Axis i at 0x40 + 52·i;
  0 throttle, 1 brake, 2 clutch.
- Read: `[3, n]` → `[3, n, 62 bytes]`. Write: `[4, 0]` → `[4, 1]`, then `[4, n, 62 bytes]` → `[4, n+1]`; after the last
  packet the pedals save and restart (no answer). `[4, 0xFE]` = version mismatch, nothing saved.
- Right after Simsonn Manager saves, the pedals may not answer the first read; the reader retries 4 times.
- Report 6 = command channel: `[6, "name", payload]`, replies `[6, "OK"…]` / `[6, "BCOK"…]`. Report 2 = params: host
  sends `[2, 0]` (repeat < 5 s), device sends raw sensor values (channel i int16 at packet 0 byte 6 + 2i; to verify).
- Axis fields are FreeJoy's: +0/+2/+4 CalibMin/Center/Max (our output ends), +6/+8 old_calib_min/max (our raw range).
  Axes 3–5 are each pedal's second sensor (disabled on this rig). Brake +43 is lighting, not throttle cut.
- Max force: raw_max = raw_min + kg × units/kg ÷ (1 − start dead zone); units/kg = 300 (fw ≤ 2.0.9) or 148 (fw ≥ 2.1.0).
- SimHub is 32-bit; the plugin is net48 AnyCPU. SimHub asks for approval whenever the plugin DLL changes.
- Windows PowerShell 5.1 adds a BOM to piped stdin: `release.ps1` feeds `git credential fill` from an ASCII file.
