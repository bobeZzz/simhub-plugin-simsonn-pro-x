# How Simsonn Manager talks to the pedals

Source: static disassembly (objdump; no decompiler) of Simsonn Manager's `bin\SSCore.dll` (HID layer, hidapi)
and `bin\SIMSONN Manager.exe` (UI), plus the UI strings in `env\translations\SSRacing_en.ts`, read on 2026-10-24.
Nothing here was observed at runtime yet: items marked **verify** need a rig test before the plugin relies on them.

## Summary
The Manager uses three HID channels on interface MI_01, all 64-byte reports:

| Report | Direction | Use |
|---|---|---|
| 2 (params) | host `[2, 0]` request / keep-alive every < 5 s; device `[2, seq, 62 bytes]` × 2 | Live raw sensor values. Used by the calibration dialog |
| 3 / 4 (config in / out) | as in `pedal-settings-layout.md` | The 1116-byte settings block. **Save** and **Finish calibration** write it; the pedals restart |
| 6 (commands) | host `[6, "name" ASCII, payload]`; device `[6, reply…]` | Settings outside the block: throttle cut, elastomer, live max force, blend, plus reads of those |

`SSCore` keeps one state per device (2 = command, 3 = read config, 4 = write config, 5 = flash) and runs one exchange
at a time. All pedal PIDs (0x6010–0x6012, 0x6019, 0x6020, 0x5008, 0x5017, 0x5018) share one handle.

## Report 6: command channel
Packet: byte 0 = 6, then the command name in ASCII, then the payload right after the name (no separator), zero
padded to 64 bytes. Replies are also report 6.

Pedal commands used by the Manager (`GraphView` = the throttle/brake page):

| Command | Payload | Sent when | Meaning |
|---|---|---|---|
| `brakeCutoff` | 3 bytes: `enabled` (0/1), threshold u16 LE in 0.1 % (500 = 50 %) | "Cut Throttle Threshold" checkbox or slider changes (live, not on Save) | Firmware cuts throttle when brake exceeds the threshold |
| `readBrakeCutoff` | none | on connect | Reply `[6, 'B','C','O','K', enabled, threshold u16 LE]` |
| `damping_type` | 10 bytes: u16 raw value (axis +0 + max force × units/kg), u8 letter `'s'`/`'m'`/`'h'` | Elastomer Soft / Medium / Hard | Also caps the Max Force spinner at 60 / 100 / 140 kg. Not in the settings block |
| `read_damping_type` | none | on connect | Reply `[6, 'O','K', letter]` |
| `damping_max` | 10 bytes: raw_max u16, output high end u16, rest 0 | Max Force spinner changes (live) | Same raw_max formula as the block. **verify** whether it applies without a restart and whether it persists |
| `set_blend` | 10 bytes: axis (0–2), ratio 0–100, rest 0 | "Ratio (%)" pressure/angle blend | Mixes the pressure and angle sensors. Read with `read_blend` (only asked for on PID 0x5017/0x5018) |
| `read_raw` | none | Main window | Reply `[6, 'O','K', 3 × int16 LE]`: raw sensor values |
| `read_sig` | none | on connect | Signature / licence check; not needed by the plugin |

Other commands in `SSCore` belong to other devices (handbrake `handbrake_point`, shifter `hshifter_point`,
`write_hs_mode`, `setThrottle`, motor `read/write_motortype`, `read/write_offset`, telemetry lights).

## Report 2: params (live raw values) — **verify**
- The host writes `[2, …]` (`SSCore::writeParam`), and `SSCore` repeats it every 5 s while connected.
- The device answers with two packets `[2, 0, 62 bytes]` and `[2, 1, 8 bytes]`, joined into a 70-byte struct
  (FreeJoy `params_report_t`).
- The calibration dialog reads raw sensor channel *i* as int16 at struct offset `4 + 2i` (packet 0, byte `6 + 2i`).
  Processed axis values start at struct offset 20. Channels 0–2 are the main sensors of throttle, brake and clutch;
  3–5 are their second sensors (angle vs pressure), see below.

## Calibration (PedalCalibrationDialog)
1. "Confirm that no pedal is pressed, then click Start Calibration"; then "press all pedals with your most
   comfortable force, then click Finish". While open, the dialog tracks min and max of each live raw channel.
2. On **Finish**, for every channel whose span (max − min) is more than 999:
   - axis +0 and +6 (CalibMin, old_calib_min) = min; +4 and +8 (CalibMax, old_calib_max) = max; +2 = midpoint.
     Dead zones are wiped (ends = raw range).
   - Channels: throttle → axis 0 and 3, brake → axis 1 and 4, clutch → axis 2 and 5.
   - The brake needs a minimum span, else the dialog complains: 1000 raw units on this rig's firmware (the code
     compares the raw version word 0x2080 with 0xD1, so the 1800 / 2160 limits only apply to very old firmware).
3. Axis 7 is cleared (ends 0x7FFF, output disabled, source −1).
4. The whole block is written with CONFIG_OUT (reports 4), so the pedals restart; 5 s timeout.

Max force is applied afterwards by the Max Force spinner (`damping_max`, and the block on Save).

## Settings block: corrections to `pedal-settings-layout.md`
The Manager's preset files (`env\Default_ProXUltra`, INI, FreeJoy key names) name the axis fields:

| Offset | Manager / FreeJoy name | Our name |
|---|---|---|
| +0 / +2 / +4 | CalibMin / CalibCenter / CalibMax | output low end / centre / high end |
| +6 / +8 | old_calib_min / old_calib_max | raw_min / raw_max |
| +34 / +35 | left_deadband_size / right_deadband_size | dead zone start / 100 − end |
| +36… | color_1, color_2, light_model, color_brightness, color_speed, light_control, light_abs, light_tc, light_sync | lighting |

- Axes 3–5 are the **second sensor** of each pedal, not unused. On this rig they are disabled (source −1) and
  untouched by calibration.
- Brake +43 (the round-2 "throttle threshold" change to 0xFF) is inside the lighting fields (color_2), not throttle cut.
- `damping_type` (elastomer) is kept in the preset file per axis, but not in the block: the pedals get it via report 6.

## Max force units per kg depend on the firmware
`GraphView` reads the firmware version (block +0, e.g. `0x2080` → "208"): **≤ 209 → 300 units/kg, otherwise
148 units/kg** (Pro X Ultra, PID 0x6012/0x6020). The rig runs 2.0.8 (300, matches our tests). The Manager's bundled
default for Pro X Ultra says firmware 2100, and it ships `proxultrav1.5-210.sfu`: a firmware update from the
Manager would make the plugin's 300 wrong.

## Max force formula (Manager)
`raw_max = raw_min + kg × units_per_kg / (1 − dz_start/100)` (clamped to int16); then
`out_low = raw_min + (raw_max − raw_min) · dz_start/100`, `out_high = raw_max − (raw_max − raw_min) · (100 − dz_end)/100`.
With no start dead zone (the plugin's base setup) this is the `raw_min + 300 · kg` we found.
