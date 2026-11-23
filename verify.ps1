# Map Local Suite - deployment verification script (PS 5.1 compatible, English output)
# Usage: powershell -ExecutionPolicy Bypass -File verify.ps1 [game_dir]
param(
    [string]$Target = ""
)
$ErrorActionPreference = "Stop"

if (-not $Target) { $Target = (Get-Location).Path }
$Target = $Target.TrimEnd('\')
$ok = $true

function Report($name, $pass, $detail) {
    if ($pass) {
        Write-Host ("[PASS] {0} - {1}" -f $name, $detail) -ForegroundColor Green
    } else {
        Write-Host ("[FAIL] {0} - {1}" -f $name, $detail) -ForegroundColor Red
        $script:ok = $false
    }
}

Write-Host ("== Map Local Suite verify: {0} ==" -f $Target)

# 1. Game.dll present + MD5 (patched2 = 690A546C9567C598262518E830286CC3, original = 267861A0DFD416DBAD13E7EE3EC7794A)
$game = Join-Path $Target "Game.dll"
if (Test-Path $game) {
    $md5 = (Get-FileHash -Algorithm MD5 $game).Hash.ToUpper()
    if ($md5 -eq "690A546C9567C598262518E830286CC3") {
        Report "Game.dll MD5" $true "patched2 (large-map patch) OK"
    } elseif ($md5 -eq "267861A0DFD416DBAD13E7EE3EC7794A") {
        Report "Game.dll MD5" $false ("original 1.27, patch NOT applied: " + $md5)
    } else {
        Report "Game.dll MD5" $false ("unknown version: " + $md5)
    }
} else {
    Report "Game.dll exists" $false "not found"
}

# 2. bin runtime present
$binDll = Join-Path $Target "bin\ydbase.dll"
Report "bin\ydbase.dll" (Test-Path $binDll) "runtime lib present"

# 3. plugin config.cfg with dzclient_api enabled
$cfg = Join-Path $Target "plugin\warcraft3\config.cfg"
if (Test-Path $cfg) {
    $content = Get-Content -Raw -Encoding Default $cfg
    if ($content -match "dzclient_api\.dll\s*=\s*1") {
        Report "config.cfg dzclient_api" $true "plugin enabled"
    } else {
        Report "config.cfg dzclient_api" $false "dzclient_api.dll not set to 1"
    }
} else {
    Report "plugin\warcraft3\config.cfg" $false "not found"
}

# 4. war3.bat content
$bat = Join-Path $Target "war3.bat"
if (Test-Path $bat) {
    $c = (Get-Content -Raw -Encoding Default $bat).Trim()
    if ($c -eq "bin\YDWEConfig.exe -launchwar3") {
        Report "war3.bat" $true "content OK"
    } else {
        Report "war3.bat" $false ("unexpected content: " + $c)
    }
} else {
    Report "war3.bat" $false "not found"
}

# 5. Maps\Download exists (map drop folder)
$maps = Join-Path $Target "Maps\Download"
if (Test-Path $maps) {
    $n = (Get-ChildItem $maps -Filter *.w3x -ErrorAction SilentlyContinue | Measure-Object).Count
    Report "Maps\Download" $true ("folder OK, w3x count: " + $n)
} else {
    Report "Maps\Download" $false "folder missing"
}

Write-Host ""
if ($ok) {
    Write-Host "ALL CHECKS PASSED" -ForegroundColor Green
    exit 0
}
Write-Host "SOME CHECKS FAILED - see [FAIL] lines above" -ForegroundColor Yellow
exit 1
