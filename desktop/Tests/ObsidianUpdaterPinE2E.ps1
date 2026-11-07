param(
  [Parameter(Mandatory)][string]$Setup,
  [Parameter(Mandatory)][string]$Updater,
  [Parameter(Mandatory)][string]$DesktopExe
)
$ErrorActionPreference='Stop'
if (-not $env:CI -or -not $env:RUNNER_TEMP) {
    throw 'Isolated CI only: no updater tamper test on production'
}
$root=Join-Path $env:RUNNER_TEMP ("Obsidian-Updater-ShaPin-"+[Guid]::NewGuid().ToString('N'))
$app=Join-Path $root 'app'
$backup=Join-Path $root 'backup'
New-Item -ItemType Directory -Force $app | Out-Null
$appExe=Join-Path $app 'UntamedDesktop.exe'
Copy-Item -LiteralPath $DesktopExe -Destination $appExe
$before=(Get-FileHash -LiteralPath $appExe -Algorithm SHA256).Hash
$original=(Get-FileHash -LiteralPath $Setup -Algorithm SHA256).Hash
$altered=Join-Path $root 'tampered-Obsidian-Setup.exe'
Copy-Item -LiteralPath $Setup -Destination $altered
$stream=[IO.FileStream]::new($altered,[IO.FileMode]::Append,[IO.FileAccess]::Write,[IO.FileShare]::None)
try { $stream.WriteByte(0x42); $stream.Flush($true) } finally { $stream.Dispose() }

function Invoke-PinTest {
  param([bool]$WithPin,[string]$ExpectedCode)
  $resultFile=Join-Path $root 'updater-result.json'
  if (Test-Path $resultFile) { Remove-Item -LiteralPath $resultFile }
  $start=[Diagnostics.ProcessStartInfo]::new($Updater)
  $start.UseShellExecute=$false
  $start.CreateNoWindow=$true
  $args=@(
    '--pid','2147483647','--installer',$altered,'--app',$appExe,
    '--expected','2.0.0','--tag','v2.0.0-obsidian-preview.999',
    '--profile','default','--backup',$backup,'--ci-root',$root)
  if ($WithPin) { $args+=@('--sha256',$original) }
  foreach ($argument in $args) { $start.ArgumentList.Add($argument) }
  $p=[Diagnostics.Process]::Start($start)
  if ($null -eq $p) { throw 'Updater failed to start' }
  try {
    if (-not $p.WaitForExit(45000)) {
      $p.Kill(); $p.WaitForExit(); throw 'Updater SHA pin test timed out'
    }
    if ($p.ExitCode -eq 0) { throw 'Tampered or unpinned Obsidian package wrongly accepted' }
  } finally { $p.Dispose() }
  if (-not (Test-Path $resultFile)) { throw 'Updater did not report rejected package' }
  $report=Get-Content $resultFile -Raw | ConvertFrom-Json
  if ($report.result -ne 'failed' -or
      ($ExpectedCode -and $report.code -ne $ExpectedCode)) {
    throw ("Unexpected updater rejection: result="+$report.result+" code="+$report.code)
  }
  if ((Get-FileHash -LiteralPath $appExe -Algorithm SHA256).Hash -ne $before) {
    throw 'Updater modified existing app despite rejected installer'
  }
  if (Test-Path (Join-Path $backup 'update-transaction.json')) {
    throw 'Updater started application snapshot for untrusted installer'
  }
  Write-Output ("PASS: Obsidian updater rejected untrusted installer before mutation ("+$report.code+")")
}
Invoke-PinTest -WithPin $true -ExpectedCode 'INSTALLER_SHA256_MISMATCH'
Invoke-PinTest -WithPin $false -ExpectedCode ''
