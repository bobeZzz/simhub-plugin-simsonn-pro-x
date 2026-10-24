# How the pedals apply dead zones and curves

From the 2.1.1 main firmware image (decrypted from `proxultra.sfu`, base 0x08010000), disassembled with Capstone.
FreeJoy code, unchanged in substance. Addresses are for 2.1.1.

## Per axis, in order (loop at 0x08010df4)
1. Filter on the raw value (0x08014458, filter bits from `+10`).
2. **Calibration map** (0x08019578, FreeJoy's centred map): raw below `+0` (output low end) → −32767, above `+4`
   (output high end) → +32767, otherwise linear through `+2` (centre), with the centre dead band `+23` (0 on the
   pedals). With the centre at the midpoint this is one straight line from low end to high end. The Manager's dead
   zones live only here, in `+0`/`+4`; `+34`/`+35` are just its UI percentages.
3. **Curve** (0x0801714c, `ShapeFunc`): if the 11 points are exactly −100, −80 … 100 the value passes through.
   Otherwise step = 65534 / 10; segment i = (value + 32767) / step (last segment for the top); its ends are
   point[i] · 32767 / 100 and point[i+1] · 32767 / 100; then `map2` (0x0801954c), a clamped **straight line**
   between them. So the curve is piecewise linear over 11 evenly spaced points.
4. Resolution reduction (`+22` low nibble), then invert (`+10` bit 1) negates the output, i.e. after the curve.

Simsonn Manager *draws* the curve with Qt's `QSplineSeries` (smooth), but the pedals use the straight segments above.

## What this means for the plugin
The plugin's chain (calibration → dead zone → 11-point piecewise-linear curve) is the same. A Manager point p
(−100…100) is plugin point (p + 100) / 2 %. Dead zones: start = (low end − raw min) / (raw max − raw min), end
likewise with the high end. Brake: the Manager's 100 % is at the high end, so "100% at" = (high end − raw min) / units
per kg, and the start dead zone is the low end's share of that force.

**Import Manager preset… / Import from pedals** (`ManagerImport`) do this conversion. Checked offline against a
simulation of the firmware steps above, on the rig dump `pedal-settings-20261024-183315-throttle-deadzone-start-10.bin`
(throttle dead zone 10 %, brake 10–90 % at 50 kg) with non-linear curves added: largest difference 0.004 %
(rounding of the stored dead zone and kg). Not carried over: the Manager's invert (applied after the curve in the
firmware, before it in the plugin) and the filter (the plugin has none).
