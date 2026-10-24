# SIMSONN Pedals (SimHub plugin)

Throttle and brake shaping for the SIMSONN Pro X Ultra (USB `VID_DDFD` / `PID_6012`) inside SimHub.
The plugin reads the pedals, applies calibration → dead zone → curve, and writes the result to a vJoy device.

## Rig setup (once)
1. Install the vJoy driver (2.1.9.1). In **Configure vJoy**, enable device 1 with at least the X and Y axes.
2. Calibrate the pedals once in Simsonn Manager, then close it. On the plugin page, enter a **brake max force in the pedals** above your hardest press and click **Set up pedals**. That writes linear curves, a 0–100 dead zone, filter off and that max force to the pedals (keeping the calibration), and fills in **Pedal max** in every preset. Pick your real max force with the brake panel's **100% at** slider.
3. Close SimHub and copy `SimsonnPedals.dll` (from the newest [release](https://github.com/bobeZzz/simhub-plugin-simsonn-pro-x/releases/latest)) into the SimHub folder. Start SimHub and enable the plugin when it asks.
4. In iRacing, bind throttle to **vJoy X** and brake to **vJoy Y**, then recalibrate the pedals in iRacing.

## Behaviour
- Throttle reads axis X and brake reads axis Y of the pedal base. Change this under **Axis mapping** if needed.
- There is no smoothing filter: the output follows the pedal with no added delay.
- Edits take effect immediately and survive a SimHub restart, but stay marked as unsaved until you click **Save**. **Revert** puts the preset back to its last Save. **Save as** moves the edits into the new preset and puts the original back to its last Save.
- Each game can have its own preset, named after the game in SimHub (e.g. `IRacing`). When **auto switch** is on, the plugin applies it when the game changes and falls back to `Default` otherwise.
- SimHub properties: `SimsonnPedalsPlugin.Throttle.Output`, `.Brake.Output`, `.Throttle.Raw`, `.DeviceConnected`, `.VJoyStatus`, `.ActivePreset`.
- `NextPreset` action can be mapped to a wheel button.

## Updates
The **Plugin updates** section of the settings page checks the newest GitHub release the first time the page opens each session, or when you click **Check for updates**. **Update** downloads `SimsonnPedals.dll`, checks its SHA-256 and version, and puts it in place of the running DLL (the old one is renamed and deleted on the next start). **Restart SimHub** loads it; SimHub then asks you to approve the changed plugin.

## Build
`dotnet build -c Release` builds the plugin and copies it into `D:\stasiks` (override with `SIMHUB_INSTALL_PATH`, or skip the copy with `-p:DeployToSimHub=false`).

## Release
From a clean, pushed `main`: `.\release.ps1 -Version 1.0.1` (optionally `-Notes "..."`). It sets the version, builds, commits, tags `v1.0.1`, pushes, and publishes a GitHub release with the DLL attached. Notes default to the commit subjects since the last release. Builds need SimHub's DLLs, so releases are made locally rather than by GitHub Actions.
