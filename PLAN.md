# SIMSONN Pedals: plan and status

Living plan for the SimHub plugin that replaces Simsonn Manager for the SIMSONN Pro X Ultra throttle and brake.
Update it when a phase starts, finishes or changes. Last updated 2026-10-24 (v1.0.21).

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
- Rig checks waiting for the user live in [TESTING.md](TESTING.md): every release adds its checks there; passed ones
  move to its Passed list.
- Research on pedal settings: one change in Simsonn Manager per **Read pedal settings** read, labelled with the change;
  diff each read against the previous one.

## Decisions (keep unless the user changes them)
- Output goes through **vJoy**; the game binds to vJoy, not to the pedals. Direct mode (planned, D1–D6) is a second
  output mode beside it, not a replacement.
- Shaping (dead zone, curve, max force scaling) happens **live in the plugin**. The pedals are set up once to neutral
  (linear, 0–100, filter off, high max force) with **Set up pedals**. Writes to the pedals write flash (and were thought to restart them; the firmware code says they reload live, D1),
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
| 5 | **iRacing binding check**: reads `Documents\iRacing\controls.cfg` (binary: name, int32 0x1F, bound, axis index, int32, DirectInput instance GUID) and `joyCalib.yaml` (device and axis names); warns when throttle / brake / clutch are unbound, bound to the pedals directly, to another device, or to a different vJoy axis than the preset sends. Rechecked when iRacing saves its controls. Format checked on the dev PC's 2024 iRacing files; rig check pending (vJoy axis names assumed "X Axis", "Y Rotation"…, only compared when they match) | 1.0.17 |
| 6 | **Exact curves**: the firmware (2.1.1, disassembled) maps raw linearly between the output ends (which hold the dead zones), then joins the 11 curve points with straight lines, the same as the plugin; the Manager only *draws* it as a spline. **Import Manager preset… / Import from pedals** convert the Manager's dead zones, curve points and brake 100 % force into the active preset exactly (also covers F3). Offline: within 0.004 % of the simulated firmware on a rig dump. See [research/curves.md](research/curves.md). Rig check pending | 1.0.18 |
| 7 | **Throttle cut and blip**, per preset: SimHub actions `SimsonnPedalsPlugin.Upshift` / `Downshift` (mapped to the paddles in Controls and events) cut the throttle to 0 for a set time or blip it to a set level (the pedal wins if pressed further); optional cut while brake output ≥ a level (2 % hysteresis; a blip still gets through). Applied after shaping, not during a throttle bind sweep. Properties `Throttle.Sent`, `Throttle.Override`. Logic checked offline; rig check pending | 1.0.19 |
| F5 | Manager protocol read from its binaries (static only): report 6 commands, params report, calibration steps, kg scale per firmware, field names. See [research/manager-protocol.md](research/manager-protocol.md) | – |

## Next, in order
All vJoy phases are released; their rig checks are in [TESTING.md](TESTING.md). Next is the direct mode below.

### Direct mode: no vJoy (phases D1–D6)
The plugin writes the active preset into the pedals' own settings, and the game binds to the pedals themselves. Phase 6
showed the firmware applies dead zones and curves exactly as the plugin does (output ends, then 11 straight segments),
so a preset can be written without changing how it feels. vJoy mode stays as it is; the user picks the mode.

**Gains:** no vJoy driver, exit guard or vJoy binding; the pedals work with SimHub closed; nothing sits between the
pedals and the game. **Costs:** every change erases and writes flash, so writes must be skipped when nothing changed
and debounced while editing; shift cut and blip need vJoy and aren't available; the firmware's brake throttle cut tops
out at 50 % brake. **D1 firmware finding:** a block write does *not* restart the pedals: 2.0.8 and 2.1.1 save to flash
and reload the settings live (see [research/direct-mode.md](research/direct-mode.md)). The rig tests confirm it.

| Plugin feature | In direct mode |
|---|---|
| Calibration, dead zones, curve, invert | Written into the block (output ends +0/+4, curve points, invert bit) |
| Brake "100% at" | Output high end = raw min + kg × units/kg (as Set up pedals does for max force) |
| Clutch | Its own axis in the block, same as throttle |
| Cut throttle when braking | Firmware `brakeCutoff` (0.1 % steps, brake axis), sent live |
| Shift cut / blip, bind sweep, exit guard | Not available / not needed |
| iRacing binding check | Checks the bindings point at the pedals instead of vJoy |

1. **D1 – Check the risky parts. Done (1.0.20, rig 2026-10-24 on 2.1.1).**
   - Firmware (2.0.8 and 2.1.1): block writes save to flash and reload live with no reset; `damping_max` changes the
     brake's raw max and high end live and saves them; `brakeCutoff` is live, capped at 50 %. No RAM-only apply exists:
     every change writes flash.
   - Rig: a block write applied live with the pedals staying connected; `damping_max` applied in about 0.2 s and was
     saved; the firmware brake cut works (cut at about 21 % for 20 %). Every test put the settings back exactly.
   - Open for D3: time the joystick pause of a write on its own. Reads pause the pedals' reports for about 2 s, so
     direct mode writes from the block it holds instead of re-reading before every write.
2. **D2 – Preset → settings block (`PedalExport`). Done (code only, no release).** Folds calibration, invert, brake
   force and dead zone into the output ends, curve as whole-number points, invert as the mirrored curve plus the
   firmware's invert bit; only shaping bytes change. Offline on the 2.1.1 and 2.0.8 dumps: firmware output within
   0.09 % of vJoy mode, no other bytes touched, no stacking, the default preset needs no write, export → import within
   0.35 %. See [research/direct-mode.md](research/direct-mode.md).
3. **D3 – Output mode and writing. Built (1.0.21), rig check pending.** Output setting vJoy / directly; direct mode
   releases vJoy, skips shift cut and blip, bars show the pedals' output, the panels' Calibrate is off. `DirectWriter`
   reads the pedals once, then writes from that block (`WriteKnown`: no reads, no report file) when direct mode turns
   on, the preset switches, on Save, and on Write to pedals (which reads first); edits alone show "Edits not in the
   pedals yet". Other pedal operations hold it and it writes again after them; it re-reads after a replug and retries
   failures every 5 s. Leaving direct mode writes the pedals back to neutral. Writes are counted in the settings.
   **Write pause test** times the joystick gap during a read and a write (HID reports on MI_00).
   Original plan, kept for reference: Setting **Output: vJoy / Direct to pedals**. Direct mode doesn't acquire vJoy
   or start the exit guard; the engine still reads the pedals for the bars. A write reads the block first and skips it
   when nothing changes (no needless restart or flash write). Writes happen on **Write to pedals**, on Save, and on an
   automatic preset switch while no game is running. Status line: which preset the pedals hold, and since when.
   Leaving direct mode offers Set up pedals, so vJoy mode gets neutral pedals again.
4. **D4 – Per car while a game runs**, depending on D1. If writes apply live without a drop (as the firmware code
   says): write on car change, preferably while stopped (pits or garage) so the feel doesn't change mid-corner. If
   the pedals drop off and iRacing doesn't cope: switch per game only, and say per-car presets need vJoy mode.
5. **D5 – Live edits.** Dragging a curve point in direct mode would mean one restart per change: edits stay in the
   plugin (bars show the result) and go to the pedals on Save / Write to pedals, with an "Edits not in the pedals
   yet" marker. Brake cut (and max force, if D1 allows) go live.
6. **D6 – UI and checks.** Hide vJoy-only parts in direct mode (vJoy status, bind sweep, shift cut and blip); the
   binding check looks for the pedals; TESTING.md section for the mode switch, writes, per-car switching and fallback
   to vJoy mode.

Flash wear: the STM32 page is good for about 10 000 erases; with skipped no-op writes and switching only per car,
that's years of normal use. Worth a counter in the log anyway.

## Maybe / parked
- **Live brake max force** (report 6 `damping_max`, sent by the Manager while the spinner moves). If it applies without
  a restart, max force could change per car in the pedals. Not needed while scaling is done in the plugin; unknown
  whether it writes flash every time.

## Known facts worth remembering
- USB VID_DDFD PID_6012. Interface MI_00 = joystick (report 1); MI_01 = settings (reports 2–6, 64 bytes).
- Firmware 0x2080 (FreeJoy-derived). Settings block 1116 bytes = 18 packets × 62. Axis i at 0x40 + 52·i;
  0 throttle, 1 brake, 2 clutch.
- Read: `[3, n]` → `[3, n, 62 bytes]`. Write: `[4, 0]` → `[4, 1]`, then `[4, n, 62 bytes]` → `[4, n+1]`; after the last
  packet the pedals save and reload live, with no answer (firmware code, D1; not a restart). `[4, 0xFE]` = version mismatch, nothing saved.
- Right after Simsonn Manager saves, the pedals may not answer the first read; the reader retries 4 times.
- Report 6 = command channel: `[6, "name", payload]`, replies `[6, "OK"…]` / `[6, "BCOK"…]`. Report 2 = params: host
  sends `[2, 0]` (repeat < 5 s), device sends raw sensor values (channel i int16 at packet 0 byte 6 + 2i; to verify).
- Axis fields are FreeJoy's: +0/+2/+4 CalibMin/Center/Max (our output ends), +6/+8 old_calib_min/max (our raw range).
  Axes 3–5 are each pedal's second sensor (disabled on this rig). Brake +43 is lighting, not throttle cut.
- Max force: raw_max = raw_min + kg × units/kg ÷ (1 − start dead zone); units/kg = 300 (fw ≤ 2.0.9) or 148 (fw ≥ 2.1.0).
- SimHub is 32-bit; the plugin is net48 AnyCPU. SimHub asks for approval whenever the plugin DLL changes.
- Windows PowerShell 5.1 adds a BOM to piped stdin: `release.ps1` feeds `git credential fill` from an ASCII file.
