# SIMSONN Pedals: plan and status

Living plan for the SimHub plugin that replaces Simsonn Manager for the SIMSONN Pro X Ultra throttle and brake.
Update it when a phase starts, finishes or changes. Last updated 2026-10-24 (v1.0.5).

## Where things are
- Repo: https://github.com/bobeZzz/simhub-plugin-simsonn-pro-x (public, `main`; git user bobeZzz)
- Project folder (dev PC): `D:\stasiks-rest\claude\SimsonnPedals`
- SimHub: dev PC `D:\stasiks`, rig `C:\Program Files (x86)\SimHub`
- Rig test data from Simsonn Manager: `D:\stasiks-rest\claude\rig-data-from-simsonn-manager` (copied into `research/`)
- Settings block map and all test rounds: [research/pedal-settings-layout.md](research/pedal-settings-layout.md)

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
- Elastomer and the firmware throttle-cut threshold are not stored in the pedals: not supported. Throttle cut is done
  by the plugin (phase 7).

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

## Next, in order
1. **Phase 3 – per-car presets.** Lookup car → car class → game → Default; "Save for this car / class"; show which
   level is active. Needs SimHub's car id and class from GameData.
2. **Brake not going to 0 when SimHub exits** (deferred by the user; vJoy keeps the last value, so a crash can leave
   the brake pressed in the sim). Zeroing in End/ProcessExit isn't enough; an earlier exit-watchdog attempt was abandoned.
3. **Phase 4 – clutch panel** (axis 2 / HID Z), full panel like throttle and brake.
4. **Phase 5 – iRacing binding check**: warn when the game's pedals aren't bound to vJoy.
5. **Phase 6 – exact curves**: match Simsonn Manager's curve shapes exactly.
6. **Phase 7 – throttle cut / blip from the shift paddles**, plus cutting throttle above a brake level.

## Maybe / parked
- **Calibrate from the plugin.** Calibration is the last thing that needs Simsonn Manager. Needs raw sensor values,
  probably in FreeJoy's params report (ID 2); another research round like F2.
- **F3 – import Simsonn Manager presets.** Only worth it if the user has Manager presets to keep.

## Known facts worth remembering
- USB VID_DDFD PID_6012. Interface MI_00 = joystick (report 1); MI_01 = settings (reports 2–6, 64 bytes).
- Firmware 0x2080 (FreeJoy-derived). Settings block 1116 bytes = 18 packets × 62. Axis i at 0x40 + 52·i;
  0 throttle, 1 brake, 2 clutch.
- Read: `[3, n]` → `[3, n, 62 bytes]`. Write: `[4, 0]` → `[4, 1]`, then `[4, n, 62 bytes]` → `[4, n+1]`; after the last
  packet the pedals save and restart (no answer). `[4, 0xFE]` = version mismatch, nothing saved.
- Right after Simsonn Manager saves, the pedals may not answer the first read; the reader retries 4 times.
- SimHub is 32-bit; the plugin is net48 AnyCPU. SimHub asks for approval whenever the plugin DLL changes.
- Windows PowerShell 5.1 adds a BOM to piped stdin: `release.ps1` feeds `git credential fill` from an ASCII file.
