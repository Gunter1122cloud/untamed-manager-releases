# Registers the EXISTING bot as a single local, console-free Windows task.
# Run as the same Windows account that owns the existing bot. Never put a
# token in command arguments. Stop the former manually started bot first.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Pythonw,
    [Parameter(Mandatory=$true)][string]$EntryScript,
    [switch]$StartAtLogon
)
$ErrorActionPreference = 'Stop'
$name = 'Obsidian-DiscordBot'
$marker = 'Obsidian Discord bot runtime v1 (local administrator provisioned)'
if (Get-ScheduledTask -TaskName $name -TaskPath '\' -ErrorAction SilentlyContinue) {
    throw 'Task already exists; inspect it locally before changing or removing it.'
}
function Resolve-LocalFile([string]$Path, [string]$ExpectedName, [string]$ExpectedExtension) {
    $item = Get-Item -LiteralPath $Path
    if ($item.PSIsContainer -or $item.FullName.StartsWith('\\') -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'An ordinary local file is required.'
    }
    if ($ExpectedName -and $item.Name -ine $ExpectedName) { throw 'Select pythonw.exe from the bot environment.' }
    if ($ExpectedExtension -and $item.Extension -ine $ExpectedExtension) { throw 'Select the existing Python entry script.' }
    $dir = $item.Directory
    while ($null -ne $dir) {
        if ($dir.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked directories are not supported.' }
        $dir = $dir.Parent
    }
    if (([IO.DriveInfo]::new([IO.Path]::GetPathRoot($item.FullName))).DriveType -ne [IO.DriveType]::Fixed) {
        throw 'Use a fixed local drive.'
    }
    return $item.FullName
}
$exe = Resolve-LocalFile $Pythonw 'pythonw.exe' ''
$entry = Resolve-LocalFile $EntryScript '' '.py'
$working = [IO.Path]::GetDirectoryName($entry)
$user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$action = New-ScheduledTaskAction -Execute $exe -Argument ('"' + $entry + '"') -WorkingDirectory $working
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
$definition = @{
    TaskName=$name; TaskPath='\'; Description=$marker
    Action=$action; Principal=$principal; Settings=$settings
}
if ($StartAtLogon) { $definition.Trigger = New-ScheduledTaskTrigger -AtLogOn -User $user }
Register-ScheduledTask @definition | Out-Null
Write-Output 'Registered; not started. Open Obsidian tray > Discord bot. Do not start another copy.'
