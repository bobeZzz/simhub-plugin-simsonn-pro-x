# SIMSONN Pedals (SimHub plugin)

Throttle and brake shaping for the SIMSONN Pro X Ultra (USB `VID_DDFD` / `PID_6012`) inside SimHub.
The plugin reads the pedals, applies calibration → filter → dead zone → curve, and writes the result to a vJoy device.

## Rig setup (once)
1. Install the vJoy driver (2.1.9.1). In **Configure vJoy**, enable device 1 with at least the X and Y axes.
2. In Simsonn Manager, set throttle and brake to a **linear** curve, a 0–100 dead zone and the filter **off**. Set **Макс. сила** (max force) to the highest value it accepts, then save it to the pedals. Enter that same number as **Pedal max** in the plugin's brake panel, and pick your real max force with the **100% at** slider.
3. Close SimHub and copy `SimsonnPedals.dll` into the SimHub folder. Start SimHub and enable the plugin when it asks.
4. In iRacing, bind throttle to **vJoy X** and brake to **vJoy Y**, then recalibrate the pedals in iRacing.

## Behaviour
- Throttle reads axis X and brake reads axis Y of the pedal base. Change this under **Axis mapping** if needed.
- The filter is exponential smoothing with a time constant of 2 ms per level (level 2 adds about 4 ms).
- Each game can have its own preset, named after the game in SimHub (e.g. `IRacing`). When **auto switch** is on, the plugin applies it when the game changes and falls back to `Default` otherwise.
- SimHub properties: `SimsonnPedalsPlugin.Throttle.Output`, `.Brake.Output`, `.Throttle.Raw`, `.DeviceConnected`, `.VJoyStatus`, `.ActivePreset`.
- `NextPreset` action can be mapped to a wheel button.

## Build
`dotnet build -c Release` builds the plugin and copies it into `D:\stasiks` (override with `SIMHUB_INSTALL_PATH`, or skip the copy with `-p:DeployToSimHub=false`).
