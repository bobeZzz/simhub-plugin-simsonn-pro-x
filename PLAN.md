# SIMSONN Pedals: plan and status

Living plan for the SimHub plugin that replaces Simsonn Manager for the SIMSONN Pro X Ultra throttle and brake.
Update it when a phase starts, finishes or changes. Last updated 2026-10-24 (v1.0.8).

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
| F5 | Manager protocol read from its binaries (static only): report 6 commands, params report, calibration steps, kg scale per firmware, field names. See [research/manager-protocol.md](research/manager-protocol.md) | – |

## Next, in order
0. **Phase 9 – firmware update from the plugin: built in 1.0.9; second chip fixed in 1.0.10, rig check pending.**
   - 1.0.9 on the rig: bootloader entry and the main chip (2.1.1) worked; the flasher is PID 0x5055.
   - 1.0.9 then waited for the pedals to restart, but the second chip goes to the flasher on report 7. That left the
     pedals in update mode.
   - 1.0.10 sends the second chip to the flasher. Update firmware also finishes from update mode: it flashes both
     chips again without the bootloader command.
   - **1.0.10 on the rig: update to 2.1.1 / 2.0.6 done.** The read-back saw the second chip as 0.0.0 because it read
     too early; a dump a minute later showed 0x0206. 1.0.11 keeps reading until the second chip answers.
   - **2.1.1 reset the settings block to the Manager's 2.1 defaults (`env/Default_ProXUltra`):**
     - axes 0–2 are off (source 0xFF);
     - the pedals are on axes 3–5 (source 11), with dead zone 5–95;
     - calibration is wiped (raw −32767…32767);
     - the params report's raw channels 3–5 carry the sensors (≈2575 / 3003 / 108 at rest);
     - throttle cut (off) and elastomer (hard) survived.
   - 1.0.11 finds the pedal axes from the source byte (`PedalAxisBase`: 0 on 2.0.x, 3 on 2.1.x), and allows Set up and
     Calibrate on 2.1.x with 148 units/kg (from the Manager's code, untested).
   - 1.0.11 on the rig: calibration on 2.1.1 was written and verified.
     - Throttle axis 3: 2561…5779.
     - Brake axis 4: rest 2923, 85 kg = +12580 at 148 units/kg.
     - The pedals' processed output is ≈0 % at rest.
     - The plugin's bars stayed still: the presets read joystick X/Y, which are dead now.
   - FreeJoy names its axes X, Y, Z, Rx, Ry, Rz in settings order, so axes 3–4 should arrive as Rx/Ry.
   - 1.0.12 sets every preset's source axes from the settings block after Read, Set up and Calibrate. Rig check:
     "Raw axes" shows Rx/Ry moving and the bars follow.
   - Protocol: `research/firmware-update.md`.
   - Code: `FirmwareFile` reads and decrypts the `.sfu` files using the key from the user's Manager install, and checks
     model and CRCs. `PedalFirmwareUpdater` does bootloader run → flasher report 5 → second chip report 7 → version
     read-back.
   - The UI section "Pedal firmware" offers the Manager's file (2.1.1) or an older one (2.0.8 rollback).
   - Tested offline:
     - decryption matches openssl;
     - the flash exchange works against a simulated bootloader (success, CRC ERROR, timeout).
   - To check on the rig, with Simsonn Manager kept installed as the fallback:
     - the flasher appears (note its PID from the report);
     - report 7 exists after 2.1.1;
     - the versions read back;
     - what 2.1.1 keeps of the settings, from Read pedal settings.
   - Set up and Calibrate refuse anything but 2.0.x (`PedalSettingsLayout.IsLayoutVerified`) until a 2.1.1 dump has
     been compared with 2.0.8.
1. **Phase 3 – per-car presets.** Lookup car → car class → game → Default; "Save for this car / class"; show which
   level is active. Needs SimHub's car id and class from GameData.
2. **Brake not going to 0 when SimHub exits** (deferred by the user; vJoy keeps the last value, so a crash can leave
   the brake pressed in the sim). Zeroing in End/ProcessExit isn't enough; an earlier exit-watchdog attempt was abandoned.
3. **Phase 4 – clutch panel** (axis 2 / HID Z), full panel like throttle and brake.
4. **Phase 5 – iRacing binding check**: warn when the game's pedals aren't bound to vJoy.
5. **Phase 6 – exact curves**: match Simsonn Manager's curve shapes exactly.
6. **Phase 7 – throttle cut / blip from the shift paddles**, plus cutting throttle above a brake level.

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
