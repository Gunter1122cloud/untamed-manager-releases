$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -ne 'true'){throw 'Real SQL engine acceptance runs only on the separate GitHub Windows runner'}
$packageRoot=Join-Path $env:RUNNER_TEMP ('untamed-mariadb-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $packageRoot|Out-Null
$zip=Join-Path $packageRoot 'mariadb.zip'
Invoke-WebRequest -Uri 'https://archive.mariadb.org/mariadb-10.11.19/winx64-packages/mariadb-10.11.19-winx64.zip' -OutFile $zip
if((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne '398EA30E5036010BBEBE01D2B1804280424DCC2626E36D8E95155C04D25A0490'){throw 'Official pinned MariaDB package hash mismatch'}
Expand-Archive -LiteralPath $zip -DestinationPath $packageRoot
$client=Get-ChildItem -LiteralPath $packageRoot -Recurse -Filter mariadb.exe|Select-Object -First 1
if(-not $client){throw 'Pinned SQL client missing'}
dotnet run --project wpf/Testing/MariaDb/MariaDb.csproj -c Release -- $client.DirectoryName
if($LASTEXITCODE -ne 0){throw 'Real MariaDB dump/restore acceptance failed'}
