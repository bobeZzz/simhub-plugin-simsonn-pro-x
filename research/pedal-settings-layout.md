# SIMSONN Pro X Ultra settings block (FreeJoy-derived)

> **Corrections from the Manager's binaries (2026-10-24), see [manager-protocol.md](manager-protocol.md):** axes 3–5 are
> each pedal's second sensor, not unused; brake +43 is lighting, not throttle cut; throttle cut and elastomer are report 6
> commands outside this block; units per kg depend on the firmware (300 up to 2.0.9, 148 from 2.1.0).

Source: `pedal-settings-20261024-174747.bin` (read from the rig, 1116 bytes = 18 × 62-byte packets).
Firmware CPU1 v2.0.8 (`0x2080`), CPU2 v2.0.5. USB interface `MI_01` carries the settings reports
(IDs 2–6, 64 bytes each); `MI_00` is the joystick (ID 1, 7 bytes).

Status legend: **confirmed** = value matches the Manager UI or preset file; **likely** = matches FreeJoy
but not yet checked by a one-change test (F2); **unknown** = not identified.

## Protocol (confirmed for reads)
- Read: send output report `[3, n, 0…]` (n = 1-based packet); reply input report `[3, n, 62 bytes]`.
  No reply after the last packet. Joystick input pauses ~2 s while this runs.
- Write (FreeJoy, `PedalConfigWriter`; confirmed by the 1.0.3 rewrite test on 2026-10-24: start answered `[4, 1]`, no answer after packet 18 as the pedals restart, read-back identical): host sends `[4, 0]`,
  device answers `[4, 1]`; then output reports `[4, n, 62 bytes]`, each answered with `[4, n+1]`.
  After the last packet it checks `firmware_version & 0xFFF0`, saves to flash and **resets** (USB drops ~1–2 s).
  On version mismatch it replies `[4, 0xFE]` and saves nothing.

## Header
| Offset | Size | Field | Value | Status |
|---|---|---|---|---|
| 0x000 | 2 | firmware_version | 0x2080 | confirmed |
| 0x002 | 2 | firmware sub-version (CPU2) | 05 02 | confirmed |
| 0x004 | 26 | device_name | "SIMSONN Pro X Ultra" | confirmed |
| 0x01E | 2 | button_debounce_ms | 50 | confirmed |
| 0x020 | 1 | encoder_press_time_ms | 10 | confirmed |
| 0x021 | 1 | exchange_period_ms | 5 | likely |
| 0x022 | 30 | pins[30] (A0–A10, A15, B0, B1, B3–B15, C13–C15) | A15 = 17 | likely |

## Axes: 8 × 52 bytes from 0x040 (axis i at 0x040 + 52·i)
Axis 0 = throttle (HID X), 1 = brake (HID Y), 2 = clutch (HID Z), 3–7 unused.

| Offset in axis | Size | Field | Throttle / Brake / Clutch | Status |
|---|---|---|---|---|
| +0 | 2 | output low end = raw_min + dz_start% · (raw_max − raw_min) | -31561 / -28989 / -32767 | confirmed (F2) |
| +2 | 2 | output centre = midpoint of +0 and +4 (Manager doesn't always refresh it) | -20828 / -13712 / 0 | confirmed (F2) |
| +4 | 2 | output high end = raw_max − (100 − dz_end)% · (raw_max − raw_min) | -10095 / 1565 / 32767 | confirmed (F2) |
| +6 | 2 | raw_min: sensor value at rest (calibration) | -32767 / -30127 / -32767 | confirmed (F2) |
| +8 | 2 | raw_max: sensor value at full travel. Brake: set from max force, −32767 + kg · 1000/3 | -8648 / 3233 / 32767 | confirmed (F2, brake from 2 points) |
| +10 | 1 | bit0 out_enabled, bit1 inverted, bits2–4 function, bits5–7 filter | 0x41 (on, filter 2) all three | confirmed (filter 2) |
| +11 | 11 | curve points, int8 −100…100 | brake point 5 = 12, others linear | confirmed |
| +22 | 1 | resolution:4 / channel:4 | 0x0F | likely |
| +23 | 1 | deadband_size:7 / dynamic:1 | 0 | likely |
| +24 | 1 | source_main (int8) | 11 | likely |
| +25 | 1 | source_secondary / offset_angle | 0 | likely |
| +26 | 3 | button1..3 (int8) | -1 | likely |
| +29 | 1 | divider | 50 | likely |
| +30 | 1 | i2c_address | 72 | likely |
| +31 | 1 | button types (bitfield) | 0xBC | likely |
| +32 | 1 | prescaler | 100 | likely |
| +33 | 1 | ? | 0 | unknown |
| +34 | 1 | dead zone start % (Manager UI value, kept for display) | 5 / 6 / 5 | confirmed (F2) |
| +35 | 1 | dead zone end as 100 − end % | 6 / 5 / 5 | confirmed (F2) |
| +36 | 16 | lighting (color_1 u32, color_2 u32, model, brightness, speed, control, abs, tc, sync…) | | likely |

## Rest of the block
| Offset | Size | Field | Status |
|---|---|---|---|
| 0x1E0 | 384 | buttons[128] × 3 bytes (physical −1, type, …) | likely |
| 0x360 | 6 | timers 1–3 (u16): 50, 200, 300 | confirmed |
| 0x3E8 | 5 | shift modifiers (−1) | likely |
| 0x3EE | 2 | VID 0xDDFD | confirmed |
| 0x3F0 | 2 | PID 0x6012 | confirmed |
| 0x3F2 | 8 | LED PWM (u16 × 4 = 50) | likely |
| 0x3FA | 48 | LEDs[24] × 2 bytes | likely |
| 0x42A | 16 | encoders (type 1) | likely |
| 0x43A– | | zeros / padding | unknown |

## F2 one-change tests (2026-10-24, cumulative: each step kept the previous changes)
Files: `pedal-settings-20261024-18*.bin/.txt`. The baseline is a reset state: every axis has raw_min −32767,
raw_max/ends 32767, dead zones 0, filter off (`+10` = 0x01), linear curves.

| Step | Bytes changed | Meaning |
|---|---|---|
| brake dead zone start 10 | brake +34 = 10; +0 = −32567, +2, +4/+8 = −30767 | start dz; Manager also wrote raw_max −30767 (= 6 kg by the formula) |
| brake dead zone end 90 | brake +35 = 10; +2 = −31767, +4 = −30967 | end dz stored as 100 − end; centre recomputed |
| brake max force 50 | brake +8 = −16101, +0 = −31100, +4 = −17767 | max force sets raw_max; ends recomputed with the 10 % dead zones |
| brake elastomer (medium) | none | not in this block |
| throttle cut 30 | none | not in this block |
| brake filter 5 | skipped | filter dropped from the plugin; field already known (+10 bits 5–7) |
| brake curve p8 40 | none | unexpected: curves are at +11 (brake p5 = 12 in the 17:47 dump). Probably not saved to the pedals; retest |
| throttle dead zone start 10 | throttle +34 = 10; +0 = −26213, +2 = 3277 | same formula on the full −32767…32767 range |
| brake calibrated | read failed (no reply) | retest with Simsonn Manager closed |

Checks: 17:47 throttle (raw −32767…−8648, dz 5/6) gives ends −31561/−10095 and centre −20828, exactly as stored.
The 17:47 brake start end doesn't fit (1138 instead of 2002 for 6 %); unexplained.

## Round 2 (2026-10-24 19:03–19:25, cumulative from `baseline2`, which is a reset state again)
| Step | Bytes changed | Meaning |
|---|---|---|
| throttle threshold 50 %, Manager then shows 0.1 % | brake +43: 0 → 0xFF; brake +0 = −32767, +4/+8 = −28567 | throttle cut lives on the **brake** axis (cut throttle above a brake level). 50 % was stored as 0xFF (−1, FreeJoy's "none"), which Manager shows as 0.1 %: the value isn't accepted. Manager also rewrote brake raw_max (4200 above min) |
| throttle cut off (shows off) | none | on/off isn't a separate field, or 0xFF already means off |
| brake elastomer hard | none | elastomer isn't stored in the pedals: a Manager-only setting |
| brake curve p8 | brake +18: 40 → 99 | curve point 8 = +18 (index 7). Linear point 8 is already 40, which is why round 1 showed nothing |
| brake max force 80 | read failed | the reader now asks up to 4 times before giving up |
| brake calibrated | read failed | as above |

## Round 3 (2026-10-24 19:31–19:33, plugin 1.0.2 reader, both reads answered first time)
| Step | Bytes changed | Meaning |
|---|---|---|
| max force 80 (after the brake calibration from round 2) | brake +6 raw_min −32767 → −30341; +8 raw_max → −6341; +0/+4 follow (dz 0); +2 centre −18838 | calibration sets raw_min (rest value). Max force sets raw_max = raw_min + 24000 = 80 × 300 |
| throttle threshold low | none (brake +43 stays 0xFF) | the threshold isn't stored in this block |
| max force 60, same calibration (19:37) | brake +8 raw_max → −12341; +0/+4 follow | span 18000 = 60 × 300: **raw_max = raw_min + 300 · kg** confirmed |

Max force scale: 300 units/kg, confirmed by 80 kg → 24000 and 60 kg → 18000 on the same calibration.
Round 1 (reset state, uncalibrated) fitted 333.3 units/kg instead; the round-1 kg values were probably not
what was assumed, or the uncalibrated state scales differently. Use 300 on a calibrated pedal.

## Not found yet
- Throttle cut threshold: not in the block. Brake +43 went to 0xFF on the first save and never changed again,
  whatever the threshold. The plugin will handle throttle cut itself.
- Elastomer: not in the pedals (Manager-only).
- Centre word (+2): not always the midpoint of the ends (−18838 vs −18341 in round 3); likely unused for pedals.
