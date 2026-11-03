param([Parameter(Mandatory)][string]$Distribution,[Parameter(Mandatory)][string]$FakeServer)
$ErrorActionPreference = 'Stop'
$installer = Join-Path $Distribution 'UntamedManager-1.4.0-SETUP.exe'
$updater = Join-Path $Distribution 'UntamedUpdater.exe'
function Invoke-Checked {
    param([string]$Program,[string[]]$Parameters,[int]$Expected=0)
    $start = [Diagnostics.ProcessStartInfo]::new($Program)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($value in $Parameters) { $start.ArgumentList.Add($value) }
    $child = [Diagnostics.Process]::Start($start)
    try {
        if (!$child.WaitForExit(240000)) { $child.Kill(); $child.WaitForExit(); throw "Isolated test process timed out" }
        if ($child.ExitCode -ne $Expected) { throw "Unexpected test process exit $($child.ExitCode), expected $Expected" }
    } finally { $child.Dispose() }
}
$serverStart = [Diagnostics.ProcessStartInfo]::new($FakeServer)
$serverStart.UseShellExecute = $false
$serverStart.CreateNoWindow = $true
$serverStart.ArgumentList.Add('--updater-e2e-fixture')
$server = [Diagnostics.Process]::Start($serverStart)
try {
    foreach ($scenario in @('success','health-failure','interrupted')) {
        $testRoot = Join-Path ([IO.Path]::GetTempPath()) ('Untamed-UpdaterE2E-' + [Guid]::NewGuid().ToString('N'))
        $application = Join-Path $testRoot 'app'
        $backup = Join-Path $testRoot 'rollback-fixture'
        $appExe = Join-Path $application 'UntamedManager.exe'
        $testProfile = 'update-ci-' + [Guid]::NewGuid().ToString('N').Substring(0,12)
        New-Item -ItemType Directory -Path $testRoot | Out-Null
        Invoke-Checked -Program $installer -Parameters @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOCLOSEAPPLICATIONS',"/DIR=$application")
        if (!(Test-Path $appExe)) { throw 'Isolated initial installation missing' }
        New-Item -ItemType Directory -Path (Join-Path $application 'dependencies') | Out-Null
        [IO.File]::WriteAllText((Join-Path $application 'dependencies/previous.dll'),'synthetic previous dependency')
        [IO.File]::WriteAllText((Join-Path $application 'user-settings.json'),'synthetic preserved settings')
        $external = Join-Path $testRoot 'external-map-list.json'
        [IO.File]::WriteAllText($external,'synthetic external map list')
        $parameters = @('--pid','2147483647','--installer',$installer,'--app',$appExe,'--expected','1.4.0','--tag','v1.4.0-wpf-preview.1','--profile',$testProfile,'--backup',$backup,'--ci-root',$testRoot)
        if ($scenario -eq 'health-failure') { $parameters += '--ci-fail-health' }
        if ($scenario -eq 'interrupted') {
            $parameters += '--ci-interrupt-after-snapshot'
            Invoke-Checked -Program $updater -Parameters $parameters -Expected 17
            if (!(Test-Path (Join-Path $backup 'update-transaction.json'))) { throw 'Interrupted updater lost its journal' }
            [IO.File]::WriteAllText((Join-Path $application 'dependencies/previous.dll'),'synthetic interruption damage')
            Invoke-Checked -Program $updater -Parameters @('--recover','--app',$appExe,'--profile',$testProfile,'--backup',$backup,'--ci-root',$testRoot)
            $expectedResult = 'recovered'
        } else {
            Invoke-Checked -Program $updater -Parameters $parameters
            $expectedResult = if ($scenario -eq 'success') { 'committed' } else { 'rolledback' }
        }
        $report = Get-Content (Join-Path $testRoot 'updater-result.json') -Raw | ConvertFrom-Json
        if ($report.result -ne $expectedResult) { throw "Wrong updater outcome: $($report.result)" }
        if ([IO.File]::ReadAllText((Join-Path $application 'dependencies/previous.dll')) -ne 'synthetic previous dependency') { throw 'Dependency not preserved' }
        if ([IO.File]::ReadAllText((Join-Path $application 'user-settings.json')) -ne 'synthetic preserved settings') { throw 'Configuration not preserved' }
        if ([IO.File]::ReadAllText($external) -ne 'synthetic external map list') { throw 'External profile mutated' }
        if ($scenario -ne 'success') {
            $journal = Get-Content (Join-Path $backup 'update-transaction.json') -Raw | ConvertFrom-Json
            $actualFiles = @(Get-ChildItem -LiteralPath $application -File -Recurse)
            if ($actualFiles.Count -ne @($journal.Hashes.PSObject.Properties).Count) { throw 'Rollback file list differs' }
            foreach ($entry in $journal.Hashes.PSObject.Properties) {
                if ((Get-FileHash -LiteralPath (Join-Path $application $entry.Name) -Algorithm SHA256).Hash -ne $entry.Value) { throw 'Rollback hash mismatch' }
            }
        }
        if ($server.HasExited) { throw 'FakeAsa did not survive updater lifecycle' }
        Write-Output "PASS actual installer and updater $scenario; restart smoke checked; full directory validated; FakeAsa alive"
    }
} finally {
    if (!$server.HasExited) { $server.Kill(); $server.WaitForExit() }
    $server.Dispose()
}

# The interrupted-updater scenario deliberately exits with code 17.
# PowerShell retains that native exit code even after all subsequent managed test assertions pass.
# Set the script's final exit code only once every scenario has succeeded.
$global:LASTEXITCODE = 0
