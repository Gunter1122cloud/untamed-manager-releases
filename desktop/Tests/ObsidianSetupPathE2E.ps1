param(
  [Parameter(Mandatory)][string]$Setup,
  [Parameter(Mandatory)][string]$IsolatedRoot,
  [Parameter(Mandatory)][string]$ExpectedMarker
)
$ErrorActionPreference='Stop'

$setupPath=[IO.Path]::GetFullPath($Setup)
$isolatedPath=[IO.Path]::GetFullPath($IsolatedRoot).TrimEnd('\')
if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) { throw 'Obsidian Setup artifact is missing' }
if (-not (Test-Path -LiteralPath $isolatedPath -PathType Container)) { throw 'Isolated test root is missing' }
$runnerPath=if ($env:RUNNER_TEMP) { [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\') } else { '' }
$isRunnerTemp=$runnerPath -and [StringComparer]::OrdinalIgnoreCase.Equals($runnerPath,$isolatedPath)
$isDevToolsTestRoot=$isolatedPath -match '(?i)[\\/]\.dev-tools[\\/]test-installs$'
if (-not $isRunnerTemp -and -not $isDevToolsTestRoot) {
  throw 'Setup path regression tests are restricted to GitHub RUNNER_TEMP or .dev-tools/test-installs'
}

$setupHash=(Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash
$testId=[Guid]::NewGuid().ToString('N')
$testRoot=Join-Path $isolatedPath ("Obsidian-Setup-Path-E2E-$testId")
$ownerMarker=Join-Path $testRoot '.test-owner'
$blocker=Join-Path $testRoot 'destination-is-a-file.blocker'
$specialCharacter=[char]0x00E9
$target=Join-Path $testRoot ('Install operator''s (quoted) - special ' + $specialCharacter + ' & spaces')
$oldTemp=$env:TEMP
$oldTmp=$env:TMP
$oldCapture=$env:UNTAMED_OBSIDIAN_CAPTURE_PATH

function Invoke-SetupForPath([string]$TargetDirectory) {
  $start=[Diagnostics.ProcessStartInfo]::new()
  $start.FileName=$setupPath
  $start.UseShellExecute=$false
  $start.CreateNoWindow=$true
  # Keep the full /DIR value in one quoted Windows argv token, including spaces,
  # punctuation and non-ASCII characters. Do not split this into Start-Process args.
  $start.Arguments='/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR="' + $TargetDirectory + '" /NOCLOSEAPPLICATIONS'
  $process=[Diagnostics.Process]::Start($start)
  if ($null -eq $process) { throw 'Obsidian Setup failed to start' }
  try {
    if (-not $process.WaitForExit(90000)) {
      $process.Kill(); $process.WaitForExit()
      throw 'Obsidian Setup path test timed out'
    }
    return $process.ExitCode
  } finally { $process.Dispose() }
}

function Assert-NoUnexpectedTestRootChildren([string[]]$BeforeNames) {
  $afterNames=@(Get-ChildItem -LiteralPath $isolatedPath -Force | ForEach-Object { $_.Name })
  $newNames=@($afterNames | Where-Object { $BeforeNames -notcontains $_ })
  $unexpected=@($newNames | Where-Object { $_ -ne [IO.Path]::GetFileName($testRoot) })
  if ($unexpected.Count -gt 0) {
    throw ('Setup created unexpected sibling path(s): ' + ($unexpected -join ', '))
  }
}

try {
  $beforeNames=@(Get-ChildItem -LiteralPath $isolatedPath -Force | ForEach-Object { $_.Name })
  $defaultInstall=Join-Path $env:LOCALAPPDATA 'Programs\UntamedObsidianPreview'
  if (Test-Path -LiteralPath $defaultInstall) {
    throw 'Cannot run invalid-target test while the default Obsidian install path already exists'
  }
  New-Item -ItemType Directory -Path $testRoot | Out-Null
  Set-Content -LiteralPath $ownerMarker -Value $testId -NoNewline -Encoding utf8

  # A file used as a parent directory is a deterministic invalid target. Setup
  # must reject it without writing beneath it, changing it, or falling back.
  Set-Content -LiteralPath $blocker -Value $testId -NoNewline -Encoding utf8
  $blockerHash=(Get-FileHash -LiteralPath $blocker -Algorithm SHA256).Hash
  $invalidTarget=Join-Path $blocker 'child-install'
  $invalidExit=Invoke-SetupForPath $invalidTarget
  if ($invalidExit -eq 0) { throw 'Setup accepted a destination whose parent is a file' }
  if (Test-Path -LiteralPath $invalidTarget) { throw 'Invalid target unexpectedly became a directory' }
  if ((Get-FileHash -LiteralPath $blocker -Algorithm SHA256).Hash -ne $blockerHash) {
    throw 'Invalid target preflight changed the test-owned blocker file'
  }
  if (Test-Path -LiteralPath $defaultInstall) {
    $fallbackMarker=Join-Path $defaultInstall 'installed-release.txt'
    if ((Test-Path -LiteralPath $fallbackMarker) -and
        (Get-Content -LiteralPath $fallbackMarker -Raw) -eq $ExpectedMarker) {
      $fallbackUninstaller=Join-Path $defaultInstall 'unins000.exe'
      if (Test-Path -LiteralPath $fallbackUninstaller) {
        $cleanup=Start-Process -FilePath $fallbackUninstaller -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait -PassThru -WindowStyle Hidden
        if ($cleanup.ExitCode -ne 0) { throw 'Test-owned unexpected default install could not be uninstalled' }
      }
    }
    if (Test-Path -LiteralPath $defaultInstall) { throw 'Invalid target created or changed the default installation directory' }
  }
  Assert-NoUnexpectedTestRootChildren $beforeNames

  # Extend the path with ordinary nested components to exercise long install
  # paths while keeping individual Windows path components below 255 chars.
  while ($target.Length -lt 165) {
    $target=Join-Path $target 'nested directory with spaces 0123456789'
  }
  if ($target.Length -gt 220) { throw 'Generated Setup test path exceeds the safe Windows test length' }
  if (Test-Path -LiteralPath $target) { throw 'Unique Setup install target unexpectedly exists' }
  $validExit=Invoke-SetupForPath $target
  if ($validExit -ne 0) { throw "Obsidian Setup rejected the quoted special-character path ($validExit)" }
  if (-not (Test-Path -LiteralPath $target -PathType Container)) { throw 'Setup did not create the exact selected target' }
  $installedMarker=Join-Path $target 'installed-release.txt'
  if (-not (Test-Path -LiteralPath $installedMarker) -or
      (Get-Content -LiteralPath $installedMarker -Raw) -ne $ExpectedMarker) {
    throw 'Setup did not preserve the exact Obsidian release marker at the selected path'
  }
  foreach ($relative in @('UntamedDesktop.exe','UntamedUpdater.exe','ui\index.html')) {
    if (-not (Test-Path -LiteralPath (Join-Path $target $relative) -PathType Leaf)) {
      throw "Installed path is missing $relative"
    }
  }
  if ((Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash -ne $setupHash) {
    throw 'Setup artifact changed during installation'
  }
  Assert-NoUnexpectedTestRootChildren $beforeNames
  # Smoke the installed WebView2 app with an isolated profile and synthetic
  # native bridge. Its temporary browser data and screenshot stay in testRoot.
  $smokeTemp=Join-Path $testRoot 'smoke-temp'
  New-Item -ItemType Directory -Path $smokeTemp | Out-Null
  $env:TEMP=$smokeTemp
  $env:TMP=$smokeTemp
  $capture=Join-Path $testRoot 'webview2-smoke.png'
  $env:UNTAMED_OBSIDIAN_CAPTURE_PATH=$capture
  $smoke=Start-Process -FilePath (Join-Path $target 'UntamedDesktop.exe') -ArgumentList '--smoke-test' -Wait -PassThru -WindowStyle Hidden
  if ($smoke.ExitCode -ne 0) { throw "Installed synthetic WebView2 smoke failed ($($smoke.ExitCode))" }
  if (-not (Test-Path -LiteralPath $capture) -or (Get-Item -LiteralPath $capture).Length -lt 8000) {
    throw 'Installed WebView2 smoke screenshot is missing or empty'
  }
  $env:TEMP=$oldTemp
  $env:TMP=$oldTmp
  $env:UNTAMED_OBSIDIAN_CAPTURE_PATH=$oldCapture

  $uninstaller=Join-Path $target 'unins000.exe'
  if (-not (Test-Path -LiteralPath $uninstaller)) { throw 'Setup uninstaller is missing from the exact target' }
  $uninstall=Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait -PassThru -WindowStyle Hidden
  if ($uninstall.ExitCode -ne 0) { throw "Isolated Setup uninstall failed ($($uninstall.ExitCode))" }
  if (Test-Path -LiteralPath $target) {
    $leftovers=@(Get-ChildItem -LiteralPath $target -Force -Recurse -ErrorAction SilentlyContinue)
    if ($leftovers.Count -gt 0) { throw 'Uninstall left installed files in the selected test directory' }
  }
  Assert-NoUnexpectedTestRootChildren $beforeNames
  Write-Output ('PASS Setup path regression: invalid target rejected; special/long path installed, smoke-tested and uninstalled. Length=' + $target.Length)
} finally {
  $env:TEMP=$oldTemp
  $env:TMP=$oldTmp
  $env:UNTAMED_OBSIDIAN_CAPTURE_PATH=$oldCapture
  if ((Test-Path -LiteralPath $ownerMarker -PathType Leaf) -and
      (Get-Content -LiteralPath $ownerMarker -Raw) -eq $testId) {
    $resolvedRoot=[IO.Path]::GetFullPath($testRoot).TrimEnd('\')
    $allowedPrefix=$isolatedPath + '\'
    if ($resolvedRoot.StartsWith($allowedPrefix,[StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedRoot).StartsWith('Obsidian-Setup-Path-E2E-', [StringComparison]::Ordinal)) {
      $stillInstalled=Join-Path $target 'unins000.exe'
      if (Test-Path -LiteralPath $stillInstalled) {
        $cleanup=Start-Process -FilePath $stillInstalled -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait -PassThru -WindowStyle Hidden
        if ($cleanup.ExitCode -ne 0) { throw 'Test-owned Setup cleanup failed; preserving the isolated path for review' }
      }
      if (Test-Path -LiteralPath (Join-Path $target 'UntamedDesktop.exe')) {
        throw 'Test-owned install is still present; preserving the isolated path for review'
      }
      Remove-Item -LiteralPath $resolvedRoot -Force -Recurse
    }
  }
}
