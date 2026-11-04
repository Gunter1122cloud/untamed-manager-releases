Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

function Assert-LabPath {
    param([string]$Root,[string]$Path)
    if(-not [IO.Path]::IsPathRooted($Root) -or -not [IO.Path]::IsPathRooted($Path) -or $Root.StartsWith('\\') -or $Path.StartsWith('\\')){throw 'Absolute local lab paths required'}
    $boundary=[IO.Path]::GetFullPath($Root).TrimEnd('\')
    $candidate=[IO.Path]::GetFullPath($Path)
    if(-not $candidate.StartsWith($boundary+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Path escapes dedicated lab root'}
    if($candidate.Contains('~') -or $candidate.Substring(3).Contains(':')){throw 'Lab path alias or stream rejected'}
    foreach($part in $candidate.Substring(3).Split('\')){if($part.EndsWith('.') -or $part.EndsWith(' ')){throw 'Lab path alias rejected'}}
    $node=$candidate
    while($node){
        if(Test-Path -LiteralPath $node){if(((Get-Item -Force -LiteralPath $node).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Linked lab paths rejected'}}
        $parent=Split-Path -Parent $node
        if($parent -eq $node){break};$node=$parent
    }
    if(([IO.DriveInfo]::new([IO.Path]::GetPathRoot($candidate))).DriveType -ne [IO.DriveType]::Fixed){throw 'Lab requires a fixed local disk'}
    return $candidate
}

function Assert-LabDisk {
    param([string]$Path)
    # A standalone qcow2 only: no backing file, external data file, encryption,
    # dirty/corrupt or unknown incompatible features can open another host file.
    $file=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        $header=[byte[]]::new(104)
        if($file.Read($header,0,104) -ne 104){throw 'Short lab disk'}
        if([Convert]::ToBase64String($header[0..3]) -ne 'UUZJ+w=='){throw 'Not qcow2'}
        if($header[4] -ne 0 -or $header[5] -ne 0 -or $header[6] -ne 0 -or $header[7] -ne 3){throw 'qcow2 v3 required'}
        foreach($offset in @(8..19)+@(32..35)+@(72..79)){if($header[$offset] -ne 0){throw 'Backing, encryption or incompatible qcow2 features rejected'}}
        $size=[byte[]]$header[24..31];[Array]::Reverse($size)
        $virtual=[BitConverter]::ToUInt64($size,0)
        if($virtual -lt 64GB -or $virtual -gt 160GB){throw 'Lab disk must be 64-160 GiB'}
    } finally {$file.Dispose()}
}

function New-IsolatedLabPlan {
    param([Parameter(Mandatory=$true)][string]$LabRoot,
          [Parameter(Mandatory=$true)][string]$GuestIso,
          [Parameter(Mandatory=$true)][string]$GuestDisk)
    $iso=Assert-LabPath $LabRoot $GuestIso
    $disk=Assert-LabPath $LabRoot $GuestDisk
    if([IO.Path]::GetExtension($iso) -ne '.iso' -or [IO.Path]::GetExtension($disk) -ne '.qcow2'){throw 'Explicit ISO and qcow2 required'}
    if(-not (Test-Path -LiteralPath $iso -PathType Leaf)){throw 'Guest ISO is missing'}
    Assert-LabDisk $disk
    $throttle=@{driver='throttle';'throttle-group'='labio';'node-name'='labdisk';file=@{driver='qcow2';file=@{driver='file';filename=$disk}}}|ConvertTo-Json -Compress -Depth 6
    $cd=@{driver='throttle';'throttle-group'='labio';'node-name'='labiso';'read-only'=$true;file=@{driver='raw';'read-only'=$true;file=@{driver='file';filename=$iso;'read-only'=$true}}}|ConvertTo-Json -Compress -Depth 6
    return [pscustomobject]@{
        Schema=1; LabRoot=[IO.Path]::GetFullPath($LabRoot); HostRebootAllowed=$false
        HostShares=@(); NetworkAdapters=0; HostPortForwards=@(); GuestRamMiB=8192
        CpuPercent=10; JobMemoryBytes=[UInt64]10GB; DiskBytesPerSecond=8MB; DiskIops=200
        GuestIso=$iso; GuestIsoSha256=(Get-FileHash -LiteralPath $iso -Algorithm SHA256).Hash
        GuestDisk=$disk; ProductionAcceptance=$false
        Arguments=@('-no-user-config','-nodefaults','-machine','q35,dump-guest-core=off',
            '-accel','tcg,thread=multi','-cpu','max','-smp','2','-m','8192',
            '-nic','none','-display','none','-monitor','none','-serial','none','-parallel','none',
            '-no-reboot','-object','throttle-group,id=labio,x-bps-total=8388608,x-iops-total=200',
            '-blockdev',$throttle,'-device','ide-hd,drive=labdisk,bus=ide.0',
            '-blockdev',$cd,'-device','ide-cd,drive=labiso,bus=ide.1','-device','VGA',
            '-boot','order=d,menu=off')
    }
}
Export-ModuleMember -Function Assert-LabPath,Assert-LabDisk,New-IsolatedLabPlan
