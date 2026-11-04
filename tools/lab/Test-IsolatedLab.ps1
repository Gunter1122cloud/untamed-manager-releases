$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'IsolatedLab.psm1') -Force
Add-Type -Path (Join-Path $PSScriptRoot 'WindowsLabJob.cs')
Add-Type -Path (Join-Path $PSScriptRoot 'LabAnswerMedia.cs')
Import-Module (Join-Path $PSScriptRoot 'LabWindowsSetup.psm1') -Force
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
$xml=New-LabWindowsAnswerXml -ImageIndex 2 -GuestPassword 'Synthetic-Lab-Only-Aa9!<&'
$answer=[Text.Encoding]::UTF8.GetBytes($xml)
$media=[UntamedLab.LabAnswerMedia]::Create($answer)
$roundtrip=[UntamedLab.LabAnswerMedia]::ExtractVerified($media)
Assert ([Convert]::ToBase64String($answer) -ceq [Convert]::ToBase64String($roundtrip)) 'FAT12 long-name answer file preserves exact XML bytes'
$document=Read-LabAnswerXml $roundtrip
$ns=[Xml.XmlNamespaceManager]::new($document.NameTable);$ns.AddNamespace('u','urn:schemas-microsoft-com:unattend')
Assert ($document.SelectSingleNode('//u:AdministratorPassword/u:Value',$ns).InnerText -ceq 'Synthetic-Lab-Only-Aa9!<&') 'guest password XML escaping round-trips without logging it'
Assert ($document.SelectSingleNode('//u:InstallTo/u:DiskID',$ns).InnerText -eq '0' -and $document.SelectSingleNode('//u:InstallTo/u:PartitionID',$ns).InnerText -eq '2') 'answer file targets only the guest disk layout'
$answerPath=Join-Path $root 'answer.img';[IO.File]::WriteAllBytes($answerPath,$media)
$withAnswer=New-IsolatedLabPlan -LabRoot $root -GuestIso $iso -GuestDisk $disk -GuestAnswerMedia $answerPath
Assert ($withAnswer.GuestAnswerMediaSha256 -eq (Get-FileHash -LiteralPath $answerPath -Algorithm SHA256).Hash -and $withAnswer.Arguments -contains 'floppy,drive=labanswer,drive-type=144') 'read-only guest answer medium is explicit and hashed'
$media[100]=1;[IO.File]::WriteAllBytes($answerPath,$media)
Refused {New-IsolatedLabPlan -LabRoot $root -GuestIso $iso -GuestDisk $disk -GuestAnswerMedia $answerPath} 'unrecognized answer-media header refused'
Refused {Read-LabAnswerXml ([Text.Encoding]::UTF8.GetBytes('<!DOCTYPE x [<!ENTITY secret SYSTEM "file:///C:/Windows/win.ini">]><unattend xmlns="urn:schemas-microsoft-com:unattend">&secret;</unattend>'))} 'external XML entity cannot read host files'
$sevenZip=Join-Path $env:ProgramFiles '7-Zip/7z.exe'
if(-not (Test-Path -LiteralPath $sevenZip)){throw 'Independent 7-Zip FAT12 validation tool unavailable on Windows runner'}
[IO.File]::WriteAllBytes($answerPath,[UntamedLab.LabAnswerMedia]::Create($answer))
$extracted=Join-Path $root 'answer-extracted'
& $sevenZip x $answerPath ('-o'+$extracted) '-y' '-bso0' '-bsp0'
if($LASTEXITCODE -ne 0){throw 'Independent FAT12 extraction failed'}
Assert ([IO.File]::ReadAllText((Join-Path $extracted 'Autounattend.xml')) -ceq $xml) 'independent 7-Zip reader recognizes FAT12 and exact long filename'
$linked=Join-Path $root 'linked';$target=Join-Path $root 'real';[IO.Directory]::CreateDirectory($target)|Out-Null
New-Item -ItemType Junction -Path $linked -Target $target|Out-Null
Refused {Assert-LabPath $root (Join-Path $linked 'disk.qcow2')} 'junction refused'
$powershell=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$exitSeven=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes('exit 7'))
$short=[UntamedLab.WindowsLabJob]::Start($powershell,[string[]]@('-NoProfile','-NonInteractive','-EncodedCommand',$exitSeven),$root,10,[UInt64]512MB)
try{
    Assert ($short.CpuRate -eq 1000 -and $short.MemoryLimit -eq 512MB) 'native CPU and memory caps queried before resume'
    $finished=$short.Wait(20000)
    Assert ($finished -and $short.ExitCode -eq 7) "suspended child assigned to job then exits with original code (actual $($short.ExitCode))"
}finally{$short.Dispose()}
$ready=Join-Path $root 'owned-ready'
$escaped=$ready.Replace("'","''")
$script="[IO.File]::WriteAllText('$escaped','ready');[Threading.Thread]::Sleep(60000)"
$encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
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
Refused {[UntamedLab.WindowsLabJob]::Start($powershell,[string[]]@('-NoProfile','-NonInteractive','-EncodedCommand',$exitSeven),$root,11,[UInt64]512MB)} 'excessive CPU allocation rejected before creation'
Assert ([UntamedLab.WindowsLabJob]::Quote('a\') -eq '"a\\"') 'trailing slash quoted safely'
Assert ([UntamedLab.WindowsLabJob]::Quote('a"b') -eq '"a\"b"') 'embedded quote escaped safely'
Write-Output 'PASS lab policy and native resource helper; no VM or ASA was started'
