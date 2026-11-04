$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'IsolatedLab.psm1') -Force
Add-Type -Path (Join-Path $PSScriptRoot 'WindowsLabJob.cs')
function Assert([bool]$Condition,[string]$Reason){if(-not $Condition){throw $Reason};Write-Output "PASS lab $Reason"}
function Refused([scriptblock]$Action,[string]$Reason){$denied=$false;try{& $Action|Out-Null}catch{$denied=$true};Assert $denied $Reason}
$root=Join-Path ([IO.Path]::GetTempPath()) ('Untamed-Lab-Policy-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
$disk=Join-Path $root 'synthetic.qcow2';$iso=Join-Path $root 'synthetic.iso'
# Synthetic header for policy tests only. It is NEVER passed to QEMU.
$header=[byte[]]::new(104);$header[0]=81;$header[1]=70;$header[2]=73;$header[3]=251;$header[7]=3
$size=[BitConverter]::GetBytes([UInt64]80GB);[Array]::Reverse($size);[Array]::Copy($size,0,$header,24,8)
[IO.File]::WriteAllBytes($disk,$header);[IO.File]::WriteAllText($iso,'Synthetic ISO identity fixture, not bootable')
$plan=New-IsolatedLabPlan -LabRoot $root -GuestIso $iso -GuestDisk $disk
Assert ($plan.NetworkAdapters -eq 0 -and $plan.HostShares.Count -eq 0 -and $plan.HostPortForwards.Count -eq 0) 'no guest NIC, shares or forwards'
Assert ($plan.Arguments -contains '-no-user-config' -and $plan.Arguments -contains '-nodefaults' -and $plan.Arguments -contains 'none') 'explicit QEMU default-device suppression'
Assert ($plan.Arguments -contains 'throttle-group,id=labio,x-bps-total=8388608,x-iops-total=200') 'shared disk and ISO I/O throttle'
Refused {New-IsolatedLabPlan $root $iso (Join-Path (Split-Path -Parent $root) 'outside.qcow2')} 'disk outside lab refused'
Refused {Assert-LabPath $root ($root+'\..\outside.qcow2')} 'dot traversal refused'
Refused {Assert-LabPath $root ($root+'\file.qcow2:stream')} 'alternate data stream refused'
Refused {Assert-LabPath $root ($root+'\file.\disk.qcow2')} 'trailing-dot alias refused'
Refused {Assert-LabPath $root '\\localhost\c$\file.qcow2'} 'UNC refused'
foreach($offset in @(8,16,32,72,79)){
    $bad=[byte[]]$header.Clone();$bad[$offset]=1;[IO.File]::WriteAllBytes($disk,$bad)
    Refused {Assert-LabDisk $disk} "unsafe qcow2 header byte $offset refused"
}
[IO.File]::WriteAllBytes($disk,$header)
$linked=Join-Path $root 'linked';$target=Join-Path $root 'real';[IO.Directory]::CreateDirectory($target)|Out-Null
New-Item -ItemType Junction -Path $linked -Target $target|Out-Null
Refused {Assert-LabPath $root (Join-Path $linked 'disk.qcow2')} 'junction refused'
$cmd=Join-Path $env:SystemRoot 'System32/cmd.exe'
$short=[UntamedLab.WindowsLabJob]::Start($cmd,[string[]]@('/d','/c','exit 7'),$root,10,[UInt64]512MB)
try{
    Assert ($short.CpuRate -eq 1000 -and $short.MemoryLimit -eq 512MB) 'native CPU and memory caps queried before resume'
    Assert ($short.Wait(15000) -and $short.ExitCode -eq 7) 'suspended child assigned to job then exits with original code'
}finally{$short.Dispose()}
$ready=Join-Path $root 'owned-ready'
$escaped=$ready.Replace("'","''")
$script="[IO.File]::WriteAllText('$escaped','ready');[Threading.Thread]::Sleep(60000)"
$encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
$powershell=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$long=[UntamedLab.WindowsLabJob]::Start($powershell,[string[]]@('-NoProfile','-NonInteractive','-EncodedCommand',$encoded),$root,10,[UInt64]512MB)
$child=[Diagnostics.Process]::GetProcessById($long.ProcessId)
try{
    $until=[DateTime]::UtcNow.AddSeconds(20)
    while(-not (Test-Path -LiteralPath $ready) -and [DateTime]::UtcNow -lt $until){Start-Sleep -Milliseconds 100}
    Assert (Test-Path -LiteralPath $ready) 'owned child executes only after limit assignment'
    Assert ($child.PriorityClass -eq [Diagnostics.ProcessPriorityClass]::Idle) 'lab child has idle priority'
    $long.Dispose()
    Assert ($child.WaitForExit(10000)) 'closing owned job terminates its child'
}finally{$long.Dispose();$child.Dispose()}
Refused {[UntamedLab.WindowsLabJob]::Start($cmd,[string[]]@('/d','/c','exit 0'),$root,11,[UInt64]512MB)} 'excessive CPU allocation rejected before creation'
Assert ([UntamedLab.WindowsLabJob]::Quote('a\') -eq '"a\\"') 'trailing slash quoted safely'
Assert ([UntamedLab.WindowsLabJob]::Quote('a"b') -eq '"a\"b"') 'embedded quote escaped safely'
Write-Output 'PASS lab policy and native resource helper; no VM or ASA was started'
