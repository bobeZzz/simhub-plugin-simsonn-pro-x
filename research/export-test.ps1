foreach ($d in "Newtonsoft.Json", "SimHub.Logging") { [void][Reflection.Assembly]::LoadFrom("D:\stasiks\$d.dll") }
[void][Reflection.Assembly]::LoadFrom("D:\stasiks-rest\claude\SimsonnPedals\bin\Release\net48\SimsonnPedals.dll")
$L = [SimsonnPedals.PedalSettingsLayout]; $M = [SimsonnPedals.PedalMath]
$rng = New-Object Random 7

# Firmware 2.1.1 chain (research/curves.md): linear map between the output ends, 11-point piecewise curve, invert after.
function Firmware($a, [double]$s) {
  $v = ($s - $a.OutLow) / ($a.OutHigh - $a.OutLow); $v = [Math]::Max(0.0, [Math]::Min(1.0, $v))
  $pos = $v * 10; $i = [Math]::Min([int][Math]::Floor($pos), 9); $f = $pos - $i
  $c = [double[]]$a.Curve; $y = (($c[$i] + ($c[$i+1] - $c[$i]) * $f) + 100) / 200
  if ($a.Inverted) { 1 - $y } else { $y }
}
# vJoy mode: pedals set up neutral, so DirectInput r = 65535 * (s - rawMin) / (rawMax - rawMin); then the plugin.
function Plugin($cfg, $a, [double]$s) {
  $r = [int][Math]::Round(65535 * [Math]::Max(0.0, [Math]::Min(1.0, ($s - $a.RawMin) / ($a.RawMax - $a.RawMin))))
  $x = $M::Normalize($r, $cfg.CalibrationMin, $cfg.CalibrationMax, $cfg.Invert)
  $x = $M::ApplyMaxForce($x, $cfg.FirmwareMaxForceKg, $cfg.MaxForceKg)
  $x = $M::ApplyDeadzone($x, $cfg.DeadzoneLow, $cfg.DeadzoneHigh)
  $M::EvaluateCurve($cfg.CurvePoints, $x)
}
function MaxDiff($cfgA, $blockA, $cfgB, $blockB, $axis, [switch]$PluginOnly) {
  $a = $L::ReadAxis($blockA, $axis); $b = $L::ReadAxis($blockB, $axis); $max = 0.0
  for ($k = 0; $k -le 500; $k++) {
    $s = $a.RawMin + ($a.RawMax - $a.RawMin) * $k / 500
    $p = Plugin $cfgA $a $s
    $o = if ($PluginOnly) { Plugin $cfgB $b $s } else { Firmware $b $s }
    $max = [Math]::Max($max, [Math]::Abs($p - $o))
  }
  $max * 100
}
function RandomConfig($cfg, [switch]$Brake, [switch]$Plain) {
  $cfg.DeadzoneLow = 0; $cfg.DeadzoneHigh = 100
  $cfg.DeadzoneHigh = [Math]::Round(60 + $rng.NextDouble() * 40, 1); $cfg.DeadzoneLow = [Math]::Round($rng.NextDouble() * 25, 1)
  $pts = New-Object double[] 11; $acc = 0; for ($i = 1; $i -le 10; $i++) { $acc += $rng.NextDouble() }
  $run = 0; for ($i = 1; $i -le 10; $i++) { $run += $rng.NextDouble(); $pts[$i] = [Math]::Round([Math]::Min(100, $run / $acc * 100), 1) }
  $cfg.CurvePoints = $pts
  if (-not $Plain) {
    $cfg.CalibrationMin = $rng.Next(0, 6000); $cfg.CalibrationMax = $rng.Next(58000, 65536)
    $cfg.Invert = $rng.Next(0, 4) -eq 0
  }
  if ($Brake) { $cfg.MaxForceKg = [Math]::Round(20 + $rng.NextDouble() * ($cfg.FirmwareMaxForceKg - 20), 1) }
}

$dumps = @(
  @("2.1.1 rig (D1)", "D:\stasiks-rest\claude\SimsonnPedals\research\d1-rig\pedal-settings-20261024-230837-before-direct-mode-live-write-test.bin"),
  @("2.0.8 rig 18:33", "D:\stasiks-rest\claude\rig-data-from-simsonn-manager\pedal-settings-20261024-183315-throttle-deadzone-start-10.bin"))
foreach ($d in $dumps) {
  $block = [IO.File]::ReadAllBytes($d[1])
  $kg = $L::BrakeMaxForceKg($block)
  $axes = @($L::ThrottleAxis($block), $L::BrakeAxis($block), $L::ClutchAxis($block))
  "== {0}: firmware {1}, brake set up for {2} kg, axes {3}" -f $d[0], $L::FirmwareVersionText($block), $kg, ($axes -join ',')

  # 1. The default preset on a block set up neutral needs no write.
  $base = $L::BaseSetup($block, $kg)
  $def = [SimsonnPedals.PedalPreset]::CreateDefault(); $def.Brake.FirmwareMaxForceKg = $kg; $def.Brake.MaxForceKg = $kg
  $e = [SimsonnPedals.PedalExport]::ToBlock($base, $def, $false)
  "  default preset on neutral pedals: changed = {0}" -f $e.Changed

  # 2. Random presets: firmware output vs plugin, bytes touched, no stacking, import round trip.
  $worst = @(0.0, 0.0, 0.0); $worstImport = @(0.0, 0.0); $badBytes = 0; $stacked = 0
  $allowed = @{}; foreach ($ax in $axes) { $o = $L::AxisOffset($ax); foreach ($off in @(0,1,2,3,4,5,10) + (11..21) + @(34,35)) { $allowed[$o + $off] = 1 } }
  for ($n = 0; $n -lt 40; $n++) {
    $p = [SimsonnPedals.PedalPreset]::CreateDefault(); $p.Brake.FirmwareMaxForceKg = $kg; $p.Brake.MaxForceKg = $kg
    RandomConfig $p.Throttle; RandomConfig $p.Brake -Brake; RandomConfig $p.Clutch
    $e = [SimsonnPedals.PedalExport]::ToBlock($base, $p, $true)
    $cfgs = @($p.Throttle, $p.Brake, $p.Clutch)
    for ($j = 0; $j -lt 3; $j++) {
      if (-not $L::IsAxisUsed($block, $axes[$j])) { continue }
      $worst[$j] = [Math]::Max($worst[$j], (MaxDiff $cfgs[$j] $base $cfgs[$j] $e.Block $axes[$j]))
    }
    $badBytes += (0..($block.Length - 1) | Where-Object { $base[$_] -ne $e.Block[$_] -and -not $allowed.ContainsKey($_) }).Count
    $again = [SimsonnPedals.PedalExport]::ToBlock($e.Block, $p, $true)
    if (-not [Linq.Enumerable]::SequenceEqual([byte[]]$again.Block, [byte[]]$e.Block)) { $stacked++ }

    # Import round trip: plain presets (plugin calibration full range, no invert), as Import assumes.
    $q = [SimsonnPedals.PedalPreset]::CreateDefault(); $q.Brake.FirmwareMaxForceKg = $kg; $q.Brake.MaxForceKg = $kg
    RandomConfig $q.Throttle -Plain; RandomConfig $q.Brake -Brake -Plain
    $eq = [SimsonnPedals.PedalExport]::ToBlock($base, $q, $false)
    $back = [SimsonnPedals.PedalPreset]::CreateDefault(); $back.Brake.FirmwareMaxForceKg = $kg; $back.Brake.MaxForceKg = $kg
    [void][SimsonnPedals.ManagerImport]::Apply($eq.Block, $back, $false)
    $worstImport[0] = [Math]::Max($worstImport[0], (MaxDiff $q.Throttle $base $back.Throttle $base $axes[0] -PluginOnly))
    $worstImport[1] = [Math]::Max($worstImport[1], (MaxDiff $q.Brake $base $back.Brake $base $axes[1] -PluginOnly))
  }
  "  40 random presets, firmware vs plugin, largest difference: throttle {0:0.000} %, brake {1:0.000} %, clutch {2:0.000} %" -f $worst[0], $worst[1], $worst[2]
  "  bytes changed outside the shaping fields: {0}; exports that change when exported again: {1}" -f $badBytes, $stacked
  "  export then import, plugin output vs original preset: throttle {0:0.000} %, brake {1:0.000} %" -f $worstImport[0], $worstImport[1]
}
