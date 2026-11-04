Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
function Save-LabGuestScreen {
    param([Parameter(Mandatory=$true)][string]$LabRoot,
          [Parameter(Mandatory=$true)][string]$OutputFile,
          [Parameter(Mandatory=$true)][string]$PipeName,
          [Parameter(Mandatory=$true)][UInt32]$ExpectedQemuPid)
    Import-Module (Join-Path $PSScriptRoot 'IsolatedLab.psm1')
    $target=Assert-LabPath $LabRoot $OutputFile
    if([IO.Path]::GetExtension($target) -ne '.png' -or (Test-Path -LiteralPath $target)){throw 'Fresh lab PNG target required'}
    if(-not (Test-Path -LiteralPath (Split-Path -Parent $target) -PathType Container)){throw 'Prepared lab artifact directory required'}
    if(-not ('UntamedLab.LabPipeChannel' -as [type])){Add-Type -Path (Join-Path $PSScriptRoot 'LabPipeChannel.cs')}
    $channel=[UntamedLab.LabPipeChannel]::Connect($PipeName,$ExpectedQemuPid)
    try{
        $greeting=$channel.ReadLine()|ConvertFrom-Json
        if($null -eq $greeting.PSObject.Properties['QMP']){throw 'Lab QMP greeting missing'}
        function Request([string]$Command,[string]$Id,$Arguments=$null){
            if($Command -notin @('qmp_capabilities','query-status','screendump')){throw 'Lab diagnostic command is not allowed'}
            $request=@{execute=$Command;id=$Id};if($null -ne $Arguments){$request.arguments=$Arguments}
            $channel.WriteLine(($request|ConvertTo-Json -Compress -Depth 5))
            for($index=0;$index -lt 16;$index++){
                $response=$channel.ReadLine()|ConvertFrom-Json
                if($null -ne $response.PSObject.Properties['id'] -and $response.id -eq $Id){
                    if($null -ne $response.PSObject.Properties['error']){throw 'Lab QMP diagnostic request rejected'}
                    if($null -eq $response.PSObject.Properties['return']){throw 'Lab QMP response missing'}
                    return $response
                }
            }
            throw 'Lab QMP response count limit'
        }
        Request 'qmp_capabilities' 'capabilities'|Out-Null
        $status=Request 'query-status' 'status'
        if($null -eq $status.'return' -or $null -eq $status.'return'.PSObject.Properties['status'] -or [string]$status.'return'.status -cnotmatch '^[a-z-]{1,32}$'){throw 'Invalid lab guest status'}
        Request 'screendump' 'screen' @{filename=$target;format='png'}|Out-Null
        $file=Get-Item -LiteralPath $target
        if($file.Length -lt 8 -or $file.Length -gt 16777216){throw 'Invalid lab screenshot size'}
        $stream=[IO.File]::OpenRead($target)
        try{$signature=[byte[]]::new(8);if($stream.Read($signature,0,8) -ne 8 -or [Convert]::ToBase64String($signature) -ne 'iVBORw0KGgo='){throw 'Lab screenshot is not PNG'}}finally{$stream.Dispose()}
        return [pscustomobject]@{QemuPid=$ExpectedQemuPid;GuestStatus=$status.'return'.status;Path=$target;Bytes=$file.Length;HostScreenCaptured=$false}
    }finally{$channel.Dispose()}
}
Export-ModuleMember -Function Save-LabGuestScreen
