param([Parameter(Mandatory=$true)][string]$LabRoot,
      [Parameter(Mandatory=$true)][string]$GuestIso,
      [Parameter(Mandatory=$true)][string]$GuestDisk,
      [Parameter(Mandatory=$true)][string]$QemuExecutable,
      [Parameter(Mandatory=$true)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$QemuSha256,
      [Parameter(Mandatory=$true)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$IsoSha256,
      [string]$GuestAnswerMedia='',
      [string]$GuestAnswerMediaSha256='',
      [ValidateRange(1,60)][int]$MaximumMinutes=10)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'IsolatedLab.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'LabQmp.psm1') -Force
$monitorPipe='UntamedLab-Qmp-'+[Guid]::NewGuid().ToString('N')
$plan=New-IsolatedLabPlan -LabRoot $LabRoot -GuestIso $GuestIso -GuestDisk $GuestDisk -GuestAnswerMedia $GuestAnswerMedia -MonitorPipe $monitorPipe
if($plan.GuestIsoSha256 -ne $IsoSha256){throw 'Guest ISO hash mismatch'}
if($GuestAnswerMedia -and ($GuestAnswerMediaSha256 -notmatch '^[A-Fa-f0-9]{64}$' -or $plan.GuestAnswerMediaSha256 -ne $GuestAnswerMediaSha256)){throw 'Lab answer media hash mismatch'}
if(-not [IO.Path]::IsPathRooted($QemuExecutable) -or [IO.Path]::GetFileName($QemuExecutable) -ne 'qemu-system-x86_64.exe'){throw 'Explicit QEMU executable required'}
if((Get-FileHash -LiteralPath $QemuExecutable -Algorithm SHA256).Hash -ne $QemuSha256){throw 'QEMU runtime hash mismatch'}
Add-Type -Path (Join-Path $PSScriptRoot 'WindowsLabJob.cs')
$machine=Split-Path -Parent $plan.GuestDisk
$guard=[IO.File]::Open((Join-Path $plan.LabRoot '.untamed-host-lab-owner'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
$owned=$null
function Assert-Headroom {
    $os=Get-CimInstance Win32_OperatingSystem
    if(([UInt64]$os.FreePhysicalMemory)*1KB -lt 24GB){throw 'Host memory reserve reached; stopping only owned lab process'}
    $drive=[IO.DriveInfo]::new([IO.Path]::GetPathRoot($plan.GuestDisk))
    if($drive.AvailableFreeSpace -lt 192GB){throw 'Host disk reserve reached; stopping only owned lab process'}
}
try {
    Assert-Headroom
    $plan|ConvertTo-Json -Depth 8|Set-Content -Encoding UTF8 -LiteralPath (Join-Path $machine 'last-launch-plan.json')
    if($GuestAnswerMedia){Write-LabSetupClaim -LabRoot $LabRoot -GuestDisk $GuestDisk -MediaSha256 $GuestAnswerMediaSha256}
    $artifacts=Assert-LabPath $LabRoot (Join-Path $LabRoot ('artifacts/guest-'+(Split-Path -Leaf $machine)))
    [IO.Directory]::CreateDirectory($artifacts)|Out-Null
    $owned=[UntamedLab.WindowsLabJob]::Start($QemuExecutable,[string[]]$plan.Arguments,$machine,10,[UInt64]10GB)
    $until=[DateTime]::UtcNow.AddMinutes($MaximumMinutes)
    $nextScreen=[DateTime]::UtcNow
    Write-Output "Isolated QEMU PID $($owned.ProcessId); CPU cap verified $($owned.CpuRate)/10000; job memory $($owned.MemoryLimit) bytes"
    while(-not $owned.Wait(5000)){
        Assert-Headroom
        if([DateTime]::UtcNow -ge $until){throw 'Lab time budget reached; stopping only owned lab process'}
        if([DateTime]::UtcNow -ge $nextScreen){
            try{
                $png=Join-Path $artifacts ('screen-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N')+'.png')
                $screen=Save-LabGuestScreen -LabRoot $LabRoot -OutputFile $png -PipeName $monitorPipe -ExpectedQemuPid $owned.ProcessId
                Write-Output ('Owned guest display: '+$screen.GuestStatus+'; '+$screen.Path)
            }catch{Write-Output 'Owned guest display diagnostics unavailable; no host screen captured'}
            $nextScreen=[DateTime]::UtcNow.AddSeconds(60)
        }
    }
    if($owned.ExitCode -ne 0){throw "QEMU exited with code $($owned.ExitCode)"}
} finally {
    if($null -ne $owned){$owned.Dispose()}
    $guard.Dispose()
}
