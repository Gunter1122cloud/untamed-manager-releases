# Builds ten offline-bundled, distinct high resolution landscape photos for the WPF dashboard.
# Images are from Unsplash; no network connection is needed at application runtime.
$ErrorActionPreference = "Stop"
$assetRoot = Join-Path $PSScriptRoot "Assets"
New-Item -Path $assetRoot -ItemType Directory -Force | Out-Null
$photos = @(
 "photo-1518709268805-4e9042af9f23",
 "photo-1462331940025-496dfbfc7564",
 "photo-1500534623283-312aade485b7",
 "photo-1519608487953-e999c86e7455",
 "photo-1470770841072-f978cf4d019e",
 "photo-1464822759023-fed622ff2c3b",
 "photo-1519681393784-d120267933ba",
 "photo-1509316785289-025f5b846b35",
 "photo-1441974231531-c6227db76b6e",
 "photo-1542662565-7e4b66bae529"
)
for ($i = 0; $i -lt $photos.Count; $i++) {
    $target = Join-Path $assetRoot ("card-{0:d2}.jpg" -f $i)
    $url = "https://images.unsplash.com/$($photos[$i])?fit=crop&w=720&h=420&q=84&fm=jpg"
    $done = $false
    foreach ($try in 1..3) {
        try {
            Invoke-WebRequest -Uri $url -OutFile $target -TimeoutSec 35 -MaximumRedirection 5
            $bytes = [IO.File]::ReadAllBytes($target)
            if ($bytes.Length -lt 8000 -or $bytes[0] -ne 255 -or $bytes[1] -ne 216) { throw "Invalid JPEG response" }
            $done = $true
            break
        } catch {
            Write-Warning "Card $i download attempt $try failed: $($_.Exception.Message)"
            Start-Sleep -Seconds 2
        }
    }
    if (-not $done) { throw "Failed to fetch card image $i; refusing to publish blurry placeholders" }
}
Write-Host "Prepared ten distinct offline WPF map-card images."
