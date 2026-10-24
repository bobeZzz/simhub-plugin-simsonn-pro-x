# How Simsonn Manager updates the pedal firmware

Taken from the Manager's binaries: `SSNetwork.dll` handles decryption, `SSCore.dll` does the flashing and device
lookup, and `SIMSONN Manager.exe` sequences the steps. Nothing here has been run on the rig yet.

## Firmware files
`env/firmware/pedal/*.sfu` in the Manager install. The Manager picks a file by the pedals' USB PID:

| PID | File | Main firmware | Sub firmware |
|---|---|---|---|
| 0x6012 (this rig, Pro X Ultra) | `proxultra.sfu` | 2.1.1 | 2.0.6 |
| 0x6020 (Pro X Ultra v1.5) | `proxultrav1.5.sfu` | 2.1.1 | 2.0.6 |

Older Pro X Ultra files are also shipped, presumably for rolling back through the Manager's file picker:

| File | Main | Sub |
|---|---|---|
| `proxultra208.sfu` | 2.0.8 | 2.0.5 (what the rig runs now) |
| `proxultra-old.sfu` | 2.0.7 | 2.0.5 |
| `proxultrav1.5-210.sfu` | 2.1.0 | 2.0.6 |

### File layout
Every header field is a 16-byte, zero-padded ASCII string.

| Offset | Field | Example (`proxultra208.sfu`) |
|---|---|---|
| 0x00 | magic | `SIMSONN` |
| 0x10 | model | `proxultra` / `proxultrav1.5` |
| 0x20 | main version | `2.0.8` |
| 0x30 | main CRC, hex | `ABF5` |
| 0x40 | main encrypted length, hex | `0000AA80` |
| 0x50 | sub version | `2.0.5` |
| 0x60 | sub CRC, hex | `AF4C` |
| 0x70 | sub encrypted length, hex | `0000B5A0` |
| 0x80 | AES key, RSA-2048 encrypted (PKCS#1 v1.5), 256 bytes | |
| 0x180 | AES IV, 16 bytes | |
| 0x190 | main image, AES-256-CBC with PKCS#7 padding, then the sub image the same way | |

- Each image is decrypted on its own, with the same key and IV.
- The RSA private key is a PEM block embedded in `SSNetwork.dll` (`NetService::decryptSfuFile`).
- The CRC is CRC-16/ARC (poly 0xA001 reflected, init 0) over the decrypted image.
  - Checked on all of 2.0.8 and 2.1.1: ABF5/AF4C and BA36/48A7 match.
  - Because of this check, a wrong key or a corrupt file is caught before anything is sent.
- The decrypted images are STM32 apps:
  - main: vector table pointing into 0x0801xxxx, so a 64 KB bootloader sits in front of it;
  - sub: pointing into 0x0800xxxx, so it is a second chip.
  - The main image contains the strings `bootloader run` and `version_sub`.

## Sequence (Manager's firmware page, per device)
1. **`enterToFlashMode(pid)`**
   - Writes report 5 `"bootloader run"` (ASCII, zero padded to 64 bytes) to the pedals' settings interface.
   - This is FreeJoy's "enter bootloader" command, with SIMSONN's report ID.
   - It then waits up to 1.7 s.
2. **Sleep 2 s.** The pedals drop off USB and come back as the bootloader.
   - The bootloader is VID 0xDDFD, product string `SIMSONN Flasher` (or `FreeJoy Flasher`).
   - Its PID is in the 0x50xx/0x60xx family. The Manager maps 0x5051/52/56/57/58/59/0x6060/61 to other SIMSONN
     products and everything else to the pedal group (0x5008). The exact pedal flasher PID is unknown until the rig
     shows it; probably 0x5050 or 0x5055.
3. **`flashFirmware(main, mode 0, mainCrc, pid, 0)`**
   - Checks the CRC of the decrypted image against the header first, and aborts if it differs.
   - Waits up to 3 s for the flasher to appear, polling every 100 ms.
   - Opens it, retrying 5 times with 500 ms between tries.
   - Then runs the exchange below with report ID **5**.
4. **On success: sleep 1 s, then `flashFirmware(sub, mode 1, subCrc, pid, subVersion)`**
   - This is the same exchange with report ID **7**, sent to the pedals' normal settings interface, now running the new
     main firmware. The main chip passes the image on to the sub chip.
   - Afterwards it reads `version_sub` up to 7 times, 1 s apart, until it matches.
   - Firmware 2.0.8 lists only reports 2–6 on that interface, so report 7 must come with the new main firmware. That
     can't be checked statically: the HID descriptors are stored compressed in the image.

Mode 2 (report ID 4) is used only for older devices (PID 0x5008 group, `enterToFlashMode(0x5008)`).

### Flash exchange (FreeJoy bootloader protocol, report ID R = 5 or 7)
All packets are 64 bytes.

1. **Host start packet:** `[R, 0x00, 0x00, 0x00, len lo, len hi, crc lo, crc hi, 0…]`
   - `len` is the decrypted image length (low 16 bits).
   - `crc` is the CRC-16/ARC.
2. **Loop:** read 64 bytes (5 s timeout per read, 300 s overall). Replies with a first byte other than 4, 5 or 7 are
   ignored. `n = buf[1] << 8 | buf[2]` (big-endian).
   - `n = 0xF000`: done, success.
   - `n = 0xF001`: SIZE ERROR. `0xF002`: CRC ERROR. `0xF003`: ERASE ERROR.
   - Any other `n`: the device asks for packet `n` (starting at 1). Send `[R, n hi, n lo, 0x00, image[(n−1)·60 …
     +60]]`, with the last packet zero padded.
3. **Progress** is `(n−1)·60 / len`.

## What this means for the plugin
- It can update the pedals with the Manager's own files and key, read from the user's Manager install. Nothing from
  SIMSONN needs to ship in the plugin or the public repo.
- Before sending anything it can verify:
  - the magic and the model (`proxultra` for 0x6012, `proxultrav1.5` for 0x6020);
  - the decrypted lengths;
  - both CRCs.
- After the update, the params report gives the new main and sub versions, so the result can be checked.
- 2.1.x changes max force to 148 units/kg. The plugin already returns that for 210–219, but it is untested. The
  settings block layout may also differ, so Set up and Calibrate should stay off on 2.1.x until a read-only dump has
  been compared with 2.0.8.
- **Risk:** if the main flash stops part way, the pedals should come back as the bootloader, and the Manager detects
  flasher devices and can flash again. That recovery path is not proven on this rig.
