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
