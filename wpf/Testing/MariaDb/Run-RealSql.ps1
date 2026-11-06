$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -ne 'true'){throw 'Real SQL engine acceptance runs only on the separate GitHub Windows runner'}
$packageRoot=Join-Path $env:RUNNER_TEMP ('untamed-mariadb-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $packageRoot|Out-Null
$zip=Join-Path $packageRoot 'mariadb.zip'
Invoke-WebRequest -Uri 'https://archive.mariadb.org/mariadb-11.4.13/winx64-packages/mariadb-11.4.13-winx64.zip' -OutFile $zip
if((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne 'D62986D433EEEBFDE218560B276103831604A61E929E87F1A17F5AEBD80257E2'){throw 'Official pinned MariaDB package hash mismatch'}
Expand-Archive -LiteralPath $zip -DestinationPath $packageRoot
$client=Get-ChildItem -LiteralPath $packageRoot -Recurse -Filter mariadb.exe|Select-Object -First 1
if(-not $client){throw 'Pinned SQL client missing'}
dotnet run --project wpf/Testing/MariaDb/MariaDb.csproj -c Release -- $client.DirectoryName
if($LASTEXITCODE -ne 0){throw 'Real MariaDB dump/restore acceptance failed'}
