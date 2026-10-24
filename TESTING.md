# Rig tests to do

Everything released since the per-car-class fix (1.0.14) that still needs a check on the rig. Update from
**Plugin updates** first. When a test passes, move it to **Passed** with the date; when it fails, note what happened.
Every new release adds its checks here.

## To test

### 1.0.15 – Clutch panel
- [ ] Tick **Clutch pedal connected** (above the pedal panels). A Clutch panel appears on the left.
- [ ] Press the clutch: its Position bar moves. If not, find the moving axis on the **Raw axes** line at the top and
      pick it under the panel's **Axis mapping** (expected Rz on firmware 2.1.1).
- [ ] **Calibrate** the clutch (press fully a few times, Finish); Position goes 0 → 100 %.
- [ ] Bind it in iRacing with **Move vJoy axis**. If the vJoy status says there's no Z axis, enable Z in Configure vJoy.
- [ ] Ends feel right. If the start or end seems cut, report it: the pedals still hold their own 5–95 % clutch dead
      zone on 2.1.1, which Set up pedals doesn't clear yet.
- [ ] Untick the toggle: the panel disappears and the game's clutch stays at 0.

### 1.0.16 – Brake drops to 0 when SimHub exits
- [ ] iRacing on the controls screen. Hold the brake and quit SimHub from the tray icon: the brake goes to 0 % within
      about a second.
- [ ] Same, but end SimHub in Task Manager.
- [ ] Send `%LocalAppData%\SimsonnPedals\exit-guard.log`. Each run should show "Watching SimHub", then "set N vJoy axes
      to 0". Only "Watching" = the guard was closed together with SimHub; "set … to 0" but the brake stayed = iRacing
      ignores the change.
- [ ] Start SimHub again right away: the pedals work normally (the guard doesn't hold on to vJoy).

### 1.0.17 – iRacing binding check
- [ ] With everything bound to vJoy, the line under the vJoy status says throttle, brake (and clutch) are bound to vJoy.
- [ ] Rebind the brake in iRacing to another vJoy axis (or to the pedals directly), save: the line turns amber and
      names the problem within a second or two. Put it back.
- [ ] If it warns about something that is actually fine, send the text and `Documents\iRacing\joyCalib.yaml` (vJoy
      axis names "X Axis", "Y Rotation"… are assumed).

### 1.0.18 – Import Simsonn Manager settings
- [ ] **Import Manager preset…** opens in the Manager's `etc\device` folder; pick a pedal preset. The status lists
      dead zones, curves and the brake's 100 % force, and the preset shows as unsaved.
- [ ] Feel matches what the pedals did with that preset in the Manager (same dead zones, curve, brake force).
- [ ] **Revert** undoes the import.
- [ ] **Import from pedals** reads the pedals (about 2 s) and fills the same way. After Set up pedals it should give
      linear curves and no dead zones.

## Older, still open
- [ ] **Save / Revert** (1.0.5): edit, Save, edit again, Revert returns to the saved values; Save as moves the edits to
      the new preset.
- [ ] **Brake max force on 2.1.1** (1.0.6): needs a scale. Press until the brake shows 100 % and compare with
      "100% at" (2.1.x uses 148 units per kg, taken from the Manager's code).
- [ ] **Games bound to the pedals directly** now see them on Rx / Ry (firmware 2.1.1) and need rebinding, or bind them
      to vJoy instead.

## Passed
- 1.0.14 – Save for this class in iRacing (single-class session), 2026-10-24.
