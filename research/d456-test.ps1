foreach ($d in "Newtonsoft.Json", "SharpDX", "SharpDX.DirectInput", "SimHub.Logging") { [void][Reflection.Assembly]::LoadFrom("D:\stasiks\$d.dll") }
[void][Reflection.Assembly]::LoadFrom("D:\stasiks-rest\claude\SimsonnPedals\bin\Release\net48\SimsonnPedals.dll")
$B = [SimsonnPedals.IRacingBindings]; $L = [SimsonnPedals.PedalSettingsLayout]; $X = [SimsonnPedals.PedalExport]

# --- D6: binding check in direct mode (synthetic controls.cfg built from the dev PC's real one) ---
$dir = "$([Environment]::GetFolderPath('MyDocuments'))\iRacing"
$bytes = [IO.File]::ReadAllBytes("$dir\controls.cfg")
$calib = $B::ReadCalibration([IO.File]::ReadAllLines("$dir\joyCalib.yaml"))
$vjoy = [Guid]'11111111-2222-3333-4444-555555555555'; $ped = [Guid]'99999999-2222-3333-4444-555555555555'
function Patch($control, $guid, $axis) {
  $name = [Text.Encoding]::ASCII.GetBytes($control + "`0`0`0`0`0")
  for ($i = 1; $i -lt $bytes.Length; $i++) { $ok = $true; for ($j = 0; $j -lt $name.Length; $j++) { if ($bytes[$i+$j] -ne $name[$j]) { $ok = $false; break } }; if ($ok) { break } }
  $o = $i + $name.Length
  [BitConverter]::GetBytes([int]1).CopyTo($bytes, $o + 4); [BitConverter]::GetBytes([int]$axis).CopyTo($bytes, $o + 8); $guid.ToByteArray().CopyTo($bytes, $o + 16)
}
$va = New-Object 'System.Collections.Generic.Dictionary[int,string]'; $va[0] = "X Axis"; $va[1] = "Y Axis"
$pa = New-Object 'System.Collections.Generic.Dictionary[int,string]'; $pa[3] = "X Rotation"; $pa[4] = "Y Rotation"; $pa[5] = "Z Rotation"
$calib[$vjoy] = [ValueTuple]::Create("vJoy Device", $va); $calib[$ped] = [ValueTuple]::Create("SIMSONN Pro X Ultra", $pa)
$att = New-Object 'System.Collections.Generic.Dictionary[Guid,System.ValueTuple[uint32,string]]'
$att[$vjoy] = [ValueTuple]::Create([uint32]3199013428, "vJoy Device"); $att[$ped] = [ValueTuple]::Create([uint32]0x6012DDFD, "SIMSONN Pro X Ultra")
$preset = [SimsonnPedals.PedalPreset]::CreateDefault()
$preset.Throttle.SourceAxis = 'RotationX'; $preset.Brake.SourceAxis = 'RotationY'; $preset.Clutch.SourceAxis = 'RotationZ'
Patch "Throttle" $ped 3; Patch "Brake" $ped 4
"direct, both on the pedals' right axes:      " + $B::Check($bytes, $calib, $att, $preset, $false, 0xDDFD, $true).Text
"vJoy mode, same file (should complain):       " + $B::Check($bytes, $calib, $att, $preset, $false, 0xDDFD, $false).Text
Patch "Brake" $ped 3
"direct, brake on the throttle's axis:         " + $B::Check($bytes, $calib, $att, $preset, $false, 0xDDFD, $true).Text
Patch "Brake" $vjoy 1
"direct, brake still on vJoy:                  " + $B::Check($bytes, $calib, $att, $preset, $false, 0xDDFD, $true).Text
"direct, clutch on and unbound:                " + $B::Check($bytes, $calib, $att, $preset, $true, 0xDDFD, $true).Text

# --- D5: which changes can go live (damping_max) ---
$block = [IO.File]::ReadAllBytes("D:\stasiks-rest\claude\SimsonnPedals\research\d1-rig\pedal-settings-20261024-230837-before-direct-mode-live-write-test.bin")
$kg = $L::BrakeMaxForceKg($block)
function NewPreset { $p = [SimsonnPedals.PedalPreset]::CreateDefault(); $p.Brake.FirmwareMaxForceKg = $kg; $p.Brake.MaxForceKg = $kg; $p }
$p = NewPreset; $p.Throttle.DeadzoneLow = 5; $p.Throttle.ApplyShape('Progressive'); $p.Brake.ApplyShape('SCurve')
$held = $X::ToBlock($block, $p, $false).Block
"pedals hold a preset; same preset exports unchanged: changed = " + $X::ToBlock($held, $p, $false).Changed
$p.Brake.MaxForceKg = 60
$e = $X::ToBlock($held, $p, $false)
"brake 100% at 85 -> 60 kg (no start dead zone): changed = {0}, live only = {1}, offsets {2}" -f $e.Changed, $X::OnlyBrakeHighEnd($held, $e.Block), (($X::Differences($held, $e.Block) | % { '0x{0:X3}' -f $_ }) -join ' ')
$p.Brake.DeadzoneLow = 4
$e = $X::ToBlock($held, $p, $false)
"same with a 4 % brake start dead zone:          changed = {0}, live only = {1} (the low end moves too, so it waits for Save)" -f $e.Changed, $X::OnlyBrakeHighEnd($held, $e.Block)
$p2 = NewPreset; $p2.Throttle.DeadzoneLow = 5; $p2.Throttle.ApplyShape('Progressive'); $p2.Brake.ApplyShape('SCurve'); $p2.Throttle.DeadzoneHigh = 90
$e = $X::ToBlock($held, $p2, $false)
"throttle dead zone end changed:                 changed = {0}, live only = {1}" -f $e.Changed, $X::OnlyBrakeHighEnd($held, $e.Block)
# After damping_max the pedals' display byte +35 is stale; that alone isn't a change.
$p.Brake.DeadzoneLow = 0
$e = $X::ToBlock($held, $p, $false)
$live = [byte[]]$held.Clone(); $o = $L::AxisOffset($L::BrakeAxis($held)); [Array]::Copy($e.Block, $o + 2, $live, $o + 2, 4)
"pedals after damping_max (display byte stale):  changed = {0}, display byte differs = {1}" -f $X::ToBlock($live, $p, $false).Changed, ($live[$o + 35] -ne $e.Block[$o + 35])
