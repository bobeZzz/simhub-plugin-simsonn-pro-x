# SIMSONN Pro X Ultra settings block (FreeJoy-derived)

Source: `pedal-settings-20261024-174747.bin` (read from the rig, 1116 bytes = 18 × 62-byte packets).
Firmware CPU1 v2.0.8 (`0x2080`), CPU2 v2.0.5. USB interface `MI_01` carries the settings reports
(IDs 2–6, 64 bytes each); `MI_00` is the joystick (ID 1, 7 bytes).

Status legend: **confirmed** = value matches the Manager UI or preset file; **likely** = matches FreeJoy
but not yet checked by a one-change test (F2); **unknown** = not identified.

## Protocol (confirmed for reads)
- Read: send output report `[3, n, 0…]` (n = 1-based packet); reply input report `[3, n, 62 bytes]`.
  No reply after the last packet. Joystick input pauses ~2 s while this runs.
- Write (FreeJoy, not yet used): output reports `[4, n, 62 bytes]`; device acks each with `[4, n+1]`.
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
| +0 | 2 | Simsonn extra (old_calib_min?) | -31561 / -28989 / -32767 | unknown |
| +2 | 2 | Simsonn extra (old_calib_max?) | -20828 / -13712 / 0 | unknown |
| +4 | 2 | calib_min (int16) | -10095 / 1565 / 32767 | likely |
| +6 | 2 | calib_center (int16) | -32767 / -30127 / -32767 | likely |
| +8 | 2 | calib_max (int16) | -8648 / 3233 / 32767 | likely |
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
| +34 | 1 | left dead zone % | 5 / 6 / 5 | likely (to confirm which side) |
| +35 | 1 | right dead zone % (100 − end) | 6 / 5 / 5 | likely |
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

## Not found yet
Max force (kg), elastomer and throttle-cut threshold have no obvious field. They may be encoded in the
calibration values (max force → calib_max), in the unknown bytes above, or sent through the separate
"params" reports (ID 2/6). F2 one-change tests will show which.
