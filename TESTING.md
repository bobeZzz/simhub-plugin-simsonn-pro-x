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

### 1.0.19 – Throttle cut and blip
- [ ] In SimHub **Controls and events**, map `SimsonnPedalsPlugin.Upshift` and `SimsonnPedalsPlugin.Downshift` to the
      paddles. On the plugin page, "Paddle presses seen" counts up with each paddle.
- [ ] Tick **Cut throttle on upshift** (60 ms). Flat-shift up in a car without its own cut: the shift goes through
      clean with the throttle held. Try 40–120 ms to find what the car needs.
- [ ] Tick **Blip throttle on downshift** (35 %, 120 ms). Downshift while braking: revs rise briefly, no lockup or
      lurch. Tune level and time per car, Save for that car.
- [ ] Tick **Cut throttle when braking** (30 %). Press brake past 30 % with throttle held: "brake cut", throttle 0;
      back off below about 28 %: throttle returns. A downshift blip still works while braking.
- [ ] **Test upshift / Test downshift** buttons flash "upshift cut" / "blip" next to them.
- [ ] Paddle-to-cut delay feels fine (SimHub reads the paddles, then the plugin). If the cut lands late, say so.
- [ ] Old presets load with all three off and aren't marked unsaved.

### 1.0.23 – Direct mode: safe writes, live brake, brake cut, page (D4–D6)
In direct mode (Output → "directly"), iRacing bound to the SIMSONN pedals.
- [ ] **Waits while driving**: on track, switch preset on the page (or Save a change). The status turns amber:
      "Waiting to send … the car is moving". Nothing freezes. Stop in the pits and release both pedals: it writes
      (a 2 s freeze you shouldn't notice while stopped), status "The pedals hold … (written …)".
- [ ] Stopped but holding the brake: it waits with "release throttle and brake"; let go and it writes.
- [ ] Car change: get into a car with its own preset in the garage; it's written while you're still stopped.
- [ ] With no game running, Save writes at once.
- [ ] **Live brake 100%**: move the brake's "100% at" slider (brake start dead zone 0). Half a second after you stop,
      the status says "brake 100% sent live" with no Save, and the brake feels it (the pedals pause only 0.2 s). With a
      brake start dead zone above 0 it waits for Save instead ("Edits not in the pedals yet").
- [ ] **Brake cut**: tick "Cut throttle when braking" at 30 %. The status adds "brake cut at 30 %"; throttle held +
      brake past 30 % gives 0 throttle in the game. At 70 % it's sent as 50 % (the pedals' limit). Untick: "brake cut
      off".
- [ ] **Page in direct mode**: no vJoy status line, no "Bind in game" row in the panels, no shift cut / blip rows (only
      the brake cut row and a note); the binding line says "bound to the SIMSONN pedals" or names what's wrong (e.g.
      still on vJoy, or brake on the throttle's axis).
- [ ] Back to vJoy: the firmware brake cut is switched off too (Read pedal settings shows "Firmware throttle cut: off"),
      and the vJoy-only parts come back.

### 1.0.21 – Direct mode (D3, no vJoy)
- [ ] Restart SimHub in direct mode: it reads the pedals once (about 2 s pause) and says the pedals hold the preset.

## Older, still open
- [ ] **Save / Revert** (1.0.5): edit, Save, edit again, Revert returns to the saved values; Save as moves the edits to
      the new preset.
- [ ] **Brake max force on 2.1.1** (1.0.6): needs a scale. Press until the brake shows 100 % and compare with
      "100% at" (2.1.x uses 148 units per kg, taken from the Manager's code).
- [ ] **Games bound to the pedals directly** now see them on Rx / Ry (firmware 2.1.1) and need rebinding, or bind them
      to vJoy instead.

## Passed
- 1.0.22 – Write pause test, 2026-10-24: a settings read freezes the pedals' joystick reports for 2074 ms, a write for
  2050 ms, then they come back (a firmware timer, see research/direct-mode.md).
- 1.0.21 – Direct mode, 2026-10-24: switched on, the pedals got the preset; iRacing bound to the pedals felt the same
  as vJoy mode; edit + Save reached the game; preset switch while driving with no dropout noticed; back to vJoy wrote
  the pedals neutral (85 kg kept) and vJoy mode works. (The neutral message stayed amber: fixed in 1.0.22.)
- 1.0.20 – Direct mode tests (D1) on firmware 2.1.1, 2026-10-24: live write applied with the pedals connected; live
  max force applied in about 0.2 s and saved; firmware brake cut works (the page wrongly said "Didn't cut", fixed in
  the next release); settings put back exactly every time.
- 1.0.14 – Save for this class in iRacing (single-class session), 2026-10-24.
