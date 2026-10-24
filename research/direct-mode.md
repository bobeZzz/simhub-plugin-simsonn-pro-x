# Direct mode: what the firmware does with new settings (phase D1)

From the main firmware images 2.0.8 and 2.1.1 (decrypted from the Manager's `.sfu` files, base 0x08010000),
disassembled with Capstone. Static reading only; the D1 rig tests (plugin 1.0.20) check it. Addresses are 2.1.1
unless marked; 2.0.8 has the same code at other addresses.

## The pedals keep two copies of the settings
- `0x20000c58`: the working copy the main loop shapes the axes with.
- `0x20002540`: a scratch copy the USB command handler fills (2.0.8: `0x20002098`).
- Flash at `0x0801F000` (two 1 KB pages) holds the saved block.

`0x0801298c` copies flash → a RAM copy; `0x080129b4` is FreeJoy's `ConfigSet`: unlock, erase both pages, program,
verify, lock. **No reset.** The only reset (`0x08016004`, `SCB->AIRCR = 0x05FA0004`) is called once, from the
"bootloader run" command (it sets the "BL" backup register and resets into the bootloader).

## Writing the block (report 4, CONFIG_OUT)
Packets 1–18 are copied into the scratch copy. After the last one (`0x08012e86`):
1. Version check (`version & 0xFFF0 == 0x2110`, PID word `& 0xFFF0 == 0x6010`), else reply `[4, 0xFE]`.
2. Fix the header: version, device name "SIMSONN Pro X Ultra", VID/PID `DDFD/6012`, some USB fields.
3. `ConfigSet` (save to flash), then set the flag at `0x200012e0` (2.0.8: `0x2000107c`). No reply is sent, which
   is why the writer sees "no answer" after the last packet.
4. The main loop (`0x08019124`) sees the flag, clears it, copies flash → working copy and re-runs the config init
   (`0x08010964`: counts axes and buttons for the report layout).

So a block write **applies live, without a restart**, at the cost of one flash erase/program of two pages. The
"pedals restart, USB drops" assumption in earlier notes came from upstream FreeJoy, not from this firmware.
Only startup-time setup (pins, ADC, timers) isn't redone, which shaping fields (output ends, curve, invert) don't need:
the axis loop reads them every cycle (research/curves.md).

## Report 6 commands
- `damping_max` (`0x08013a78`; 2.0.8 `0x08013da6`): copies flash → scratch, then for the **brake axis** (2.1.1 axis 4 at
  +0x110, 2.0.8 axis 1 at +0x74) sets raw max (+8) = first u16, output high end (+4) = second u16, centre (+2) =
  middle of the output ends; `ConfigSet`; reload flag. 2.1.1 replies `DMOK`, 2.0.8 sends nothing. So it is a
  live, saved change of the brake's 100 % point, the same as a block write of those three fields.
- `damping_type`: same pattern, also writes the elastomer letter (`0x20000b8c`).
- `brakeCutoff` (`0x08013adc`): enabled = byte != 0; threshold u16 with 0 → 1 and **capped at 500 (50 %)**; stored
  in RAM (`0x20000b82`, `0x20000348`), then a flag (`0x2000299c`) makes the main loop save it (`0x08016fc0`). Live;
  kept over a replug (seen on the rig with 1.0.7).
- Other names in the dispatcher: `bootloader run`, `init`, `calibration_app_quit`, `version`, `version_sub`,
  `read_sig`, `read_profile`, `read/write_motortype`, `read_damping_type`, `readBrakeCutoff`, plus 4-letter ones
  (`t_pf`, `b_pf`, `c_pf`, `w_pf`). None of them applies the block without saving it.

## Consequences for direct mode
- No USB drop to worry about if the rig confirms: per-car switching while driving becomes possible (D4).
- Every change still erases flash (STM32F1: about 10 000 cycles per page). Writes must be skipped when nothing
  changed and debounced while editing (D5); dragging a curve point must not write on every move.
- There is no RAM-only apply: any live change goes through `ConfigSet`.

## D1 rig tests (plugin 1.0.20, `DirectModeTests`)
- **Live write test**: throttle 100 % at half travel, written as a block while the throttle is held halfway; then the
  original back. Reports whether the throttle jumped live, how fast, and whether DirectInput lost the pedals.
- **Live max force test**: `damping_max` with half the brake force while the brake is held lightly; reads the block to
  see it saved; puts it back.
- **Firmware brake cut test**: `brakeCutoff` on at a level for 10 s; reports where the throttle was cut; sets it back.
Each writes a `direct-test-*.txt` report with a 50 ms trace to the dump folder.

### Rig run 2026-10-24 23:08–23:09 (firmware 2.1.1, `rig-data-from-simsonn-manager/test-results`)
From the settings dumps and write logs (the `direct-test-*.txt` reports weren't in the copy yet):
- **Live write test**: the test block (throttle axis 3 high end 5799 → 4179, centre moved) was written and read back
  exactly 2–5 s later, at the first attempt; the restore put the block back byte for byte. Only `0x0DE–0x0E1` changed.
- **Live max force test** (run twice): `damping_max` changed exactly the bytes predicted: brake axis 4 centre, high
  end and raw max (`0x112–0x115`, `0x118–0x119`; raw max 15509 → 9219). The block read right after shows them:
  **saved to flash**. The restore brought the block back byte for byte, both runs.
- **Firmware brake cut test**: not run yet.

### Rig run 2026-10-24 23:28–23:29 (firmware 2.1.1, reports in `d1-rig/direct-test-*.txt`)
- **Live write test: passed.** Throttle held at 51.8 % jumped to 100 % with no disconnect; restore verified.
  The trace shows the throttle reading *unchanged* for 2.7 s after the test began. That span covers the re-read
  before writing, the 18 packets and the read-back, so it doesn't time the apply itself. Reads are known to pause the
  pedals' joystick reports (about 2 s, "the pedals stop responding" in the UI texts); a constant value can also just
  be a steady foot (the throttle's raw span is only 3240 units on this calibration).
- **Live max force test: passed.** Brake 28 % → 57 % at the same press **219 ms** after `damping_max` was sent,
  saved in the stored settings, no disconnect, settings back byte for byte.
- **Firmware brake cut test at 20 %: works.** Five presses: throttle 98 % → 0 within one 50 ms trace step each time
  the brake passed the level, back to 98 % on release; the cut point came out at about 21 %. The page said "Didn't
  cut" because the summary treated any single 10 ms sample with the throttle still up above the level as a failure;
  those are the crossing instants (the two axes aren't sampled at the same moment). Fixed in the next release.
  Set back to off (threshold 10 %).

## D2: preset → settings block (`PedalExport`)
The plugin's chain before the curve (calibration, invert, brake force scaling, dead zone) is one clamped straight line
in the raw sensor value, so it becomes the firmware's output ends: the raw values where the output leaves 0 and
reaches 100 %. The 11 curve points go across as 2p − 100, rounded to whole numbers. Invert: the firmware inverts
*after* the curve and the plugin *before* it, so an inverted axis gets the mirrored curve (100 − 2·p[10−i]) plus the
invert bit, which gives the same output. Filter off; the Manager's dead zone percentages (+34/+35) are filled in for
display. Calibration (+6/+8) and every other byte stay as they are.

Reference frame: the pedals set up neutral, i.e. each axis outputs 0…100 % over its raw range (+6…+8), as in vJoy
mode. Exporting from any block gives the same result, so exports never stack, and the default preset on neutral
pedals exports to the same bytes (no write needed).

Offline checks ([export-test.ps1](export-test.ps1), 40 random presets per dump: dead zones, curves, plugin calibration, invert, brake
"100% at"), on the 2.1.1 D1 dump and the 2.0.8 18:33 dump, both set up neutral:
- Firmware simulation of the exported block vs the plugin in vJoy mode: largest difference 0.087 % (throttle on 2.1.1,
  whose raw span is only 3240 units, so the ends round to 0.03 % of travel), brake 0.034 %, clutch 0.008 %.
- Bytes changed outside the shaping fields: none. Exporting an exported block again: identical.
- Export then **Import** (plain presets): throttle within 0.05 %, brake within 0.35 % (import rounds "100% at" to
  0.1 kg).

For D3: in direct mode the pedals already output the shaped value, so the plugin must not shape it again. Its bars
should show the pedals' output, and the calibration capture must work on the neutral frame (or set the pedals neutral
while calibrating).

## D3 rig run (1.0.21, 2026-10-24 23:44, firmware 2.1.1)
Direct mode worked end to end (switch on, feel matches vJoy mode, Save writes, preset switch while driving, back to
neutral). **Write pause test** raw data (`d1-rig/direct-test-20261024-234448-write-pause.txt`): joystick reports
(MI_00) at 500/s, normally no gap over 4 ms. A **settings read** stopped them for **2075 ms**, starting the moment
the read began (the read itself took 681 ms, so the pause outlasts it by 1.4 s). At the **write** (started at
4182 ms) the reports had stopped by about 4.19 s and hadn't come back when the test ended at 6.0 s: at least
1.8 s. The 1.0.21 summary ("longest gap 3 ms / 2 ms") was wrong: it only looked at gaps that started and ended
inside the read/write span. 1.0.22 measures silences that overlap the span and listens 4 s after the write.

### 1.0.22 rerun (23:58) and the firmware's pause timer
Measured properly: reports at 500/s; a settings read silenced them for **2074 ms** from +1 ms, the write for
**2050 ms** from +2 ms; then they came back by themselves. Both the same length, so it isn't flash time.

Firmware 2.1.1, USB handler `0x08012ac0`: for every incoming report it sets the joystick report timer (`0x200012a0`)
to `millis() + 2000` (report 6 commands: `+ 200`; report 2 params: untouched). The joystick sender (`0x08017b24`)
sends only once `millis() − timer ≥ exchange period`. So **any config packet (reads 3, writes 4, flash 5) holds
joystick reports for 2 s after the last packet; report 6 commands hold them 200 ms; params requests don't.** This is
FreeJoy's "configurator connected" pause, built into the firmware: a settings write can't avoid it. The game sees the
last values held for those 2 s (a pressed brake stays pressed).

Consequences:
- **D4:** write the block only when it can't matter: car stopped (in the pits or garage, or not on track) and both
  pedals released. Hold a pending write until then.
- `damping_max` (brake "100% at") and `brakeCutoff` cost only 200 ms: those can follow edits more freely (D5).
- Reads cost 2 s too: direct mode already reads only once (start, replug, after other pedal operations).

So far: **reads and writes both pause the pedals' joystick reports for about 2 s**, even though the pedals stay
connected and the settings apply live. Not felt as a dropout when switching presets while driving, but a 2 s
freeze at the wrong moment matters: D4 should write only while the car is stopped (pits, garage) or not on track.

**D1 result:** writes and `damping_max` apply live and the pedals stay connected, so direct mode doesn't need to
avoid writing while driving. Open for D3: how long a *write alone* (no reads around it) pauses the joystick reports.
Direct mode should write from the block it already holds, without re-reading each time, and the D3 test should time
the pause.
