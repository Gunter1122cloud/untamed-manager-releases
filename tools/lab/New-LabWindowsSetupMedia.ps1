param([Parameter(Mandatory=$true)][string]$LabRoot,
      [Parameter(Mandatory=$true)][string]$MachineDirectory,
      [Parameter(Mandatory=$true)][ValidateRange(1,64)][int]$VerifiedImageIndex)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'IsolatedLab.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'LabWindowsSetup.psm1') -Force
if(-not ('UntamedLab.LabAnswerMedia' -as [type])){Add-Type -Path (Join-Path $PSScriptRoot 'LabAnswerMedia.cs')}
$machine=Assert-LabPath $LabRoot $MachineDirectory
if(-not (Test-Path -LiteralPath $machine -PathType Container)){throw 'Prepared lab machine directory required'}
$disk=Join-Path $machine 'system.qcow2';Assert-LabPath $LabRoot $disk|Out-Null;Assert-LabDisk $disk
$media=Assert-LabPath $LabRoot (Join-Path $machine 'windows-setup-answer.img')
$credential=Assert-LabPath $LabRoot (Join-Path $machine 'guest-admin.credential.dpapi')
if((Test-Path -LiteralPath $media) -or (Test-Path -LiteralPath $credential)){throw 'Existing lab setup media or credential is never overwritten'}
$random=[byte[]]::new(24);$rng=[Security.Cryptography.RandomNumberGenerator]::Create()
try{$rng.GetBytes($random)}finally{$rng.Dispose()}
$password='Aa9!'+[BitConverter]::ToString($random).Replace('-','')
$xml=New-LabWindowsAnswerXml -ImageIndex $VerifiedImageIndex -GuestPassword $password
$bytes=[Text.UTF8Encoding]::new($false).GetBytes($xml)
Read-LabAnswerXml $bytes|Out-Null
$image=[UntamedLab.LabAnswerMedia]::Create($bytes)
[UntamedLab.LabAnswerMedia]::ExtractVerified($image)|Out-Null
$protected=ConvertFrom-SecureString (ConvertTo-SecureString -String $password -AsPlainText -Force)
$output=[IO.File]::Open($credential,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try{$encoded=[Text.Encoding]::UTF8.GetBytes($protected);$output.Write($encoded,0,$encoded.Length);$output.Flush($true)}finally{$output.Dispose()}
$output=[IO.File]::Open($media,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try{$output.Write($image,0,$image.Length);$output.Flush($true)}finally{$output.Dispose()}
# Never emit the guest credential or XML. The real VM has no network adapter.
[pscustomobject]@{AnswerMedia=$media;Sha256=(Get-FileHash -LiteralPath $media -Algorithm SHA256).Hash;Bytes=$image.Length;GuestImageIndex=$VerifiedImageIndex;GuestBooted=$false;HostInstallExecuted=$false}
