param(
 [Parameter(Mandatory=$true)][string]$ProfileRoot,
 [Parameter(Mandatory=$true)][string]$BindIp,
 [Parameter(Mandatory=$true)][string]$BotIp,
 [Parameter(Mandatory=$true)][ValidateRange(1024,65535)][int]$Port,
 [Parameter(Mandatory=$true)][string]$GuildId,
 [Parameter(Mandatory=$true)][string[]]$ChannelIds,
 [Parameter(Mandatory=$true)][string[]]$RoleIds
)
$ErrorActionPreference = 'Stop'
function IsPrivate([string]$ip) {
 $a = [System.Net.IPAddress]::Parse($ip)
 $b = $a.GetAddressBytes()
 return $b.Length -eq 4 -and ($b[0] -eq 10 -or ($b[0] -eq 192 -and $b[1] -eq 168) -or ($b[0] -eq 172 -and $b[1] -ge 16 -and $b[1] -le 31) -or $b[0] -eq 127)
}
if (!(IsPrivate $BindIp) -or !(IsPrivate $BotIp)) { throw 'Private IPv4 only' }
foreach ($id in @($GuildId)+$ChannelIds+$RoleIds) { if ($id -notmatch '^[0-9]{15,25}$') {throw 'Invalid Discord ID'} }
if ($ChannelIds.Count -lt 1 -or $ChannelIds.Count -gt 20 -or $RoleIds.Count -lt 1 -or $RoleIds.Count -gt 20) {throw 'Invalid role/channel count'}
$profile = Get-Item -LiteralPath $ProfileRoot
if (!$profile.PSIsContainer) {throw 'Existing Manager profile required'}
for ($d=$profile; $null -ne $d; $d=$d.Parent) {if ($d.Attributes -band [IO.FileAttributes]::ReparsePoint) {throw 'Linked profile denied'}}
$folder = Join-Path $profile.FullName 'discord-control-v1'
if (Test-Path -LiteralPath $folder) {
 if ((Get-Item -LiteralPath $folder).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked folder denied' }
} else { New-Item -ItemType Directory -Path $folder | Out-Null }
$file = Join-Path $folder 'settings.json'
if (Test-Path -LiteralPath $file) {throw 'Existing configuration; disable/rotate manually with Manager closed first'}
$cert = New-SelfSignedCertificate -DnsName 'Obsidian-Discord-Control' -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(2)
$random = [Security.Cryptography.RandomNumberGenerator]::Create()
$sha = [Security.Cryptography.SHA256]::Create()
try {
 $bytes = New-Object byte[] 32
 $random.GetBytes($bytes)
 $token = [BitConverter]::ToString($bytes).Replace('-','').ToLowerInvariant()
 $hash = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($token))).Replace('-','')
 $pin = [BitConverter]::ToString($sha.ComputeHash($cert.RawData)).Replace('-','')
 $settings = @{bindIp=$BindIp;port=$Port;botIp=$BotIp;certificateThumbprint=$cert.Thumbprint;tokenSha256=$hash;guildId=$GuildId;channelIds=@($ChannelIds);roleIds=@($RoleIds)}
 $json = $settings | ConvertTo-Json -Depth 4
 $tmp = $file+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
 [IO.File]::WriteAllText($tmp,$json,(New-Object Text.UTF8Encoding($false)))
 [IO.File]::Move($tmp,$file)
 Write-Host "One-time bot credential (store privately): $token"
 Write-Host "TLS SHA256 certificate pin: $pin"
 Write-Host 'Configure a restrictive Windows Firewall rule manually. Restart Manager only.'
} finally { $random.Dispose(); $sha.Dispose() }
