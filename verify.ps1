# Map Local Suite - deployment verification script (PS 5.1 compatible, English output)
# Usage: powershell -ExecutionPolicy Bypass -File verify.ps1 [game_dir]
# NOTE (2026-11-25): war3.bat feature check now matches the keybind_panel launcher
#   (skillbar_reader / keybind.exe were removed in the pkg-sync cleanup); item #5
#   verifies tools\keybind_panel.exe against the v3 build (59,904B / C5973F2E...,
#   AutoReadHotkeys auto-read skillbar/item hotkeys via RPM read-only). When the
#   panel is upgraded, update the expected size/MD5 here too.
param(
    [string]$Target = "",
    [string]$Injector = ""   # 注入器绿色包目录（可选；空=自动探测桌面/工作区）
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

# 4. war3.bat content (feature check: YDWEConfig launcher + keybind_panel daemon line)
$bat = Join-Path $Target "war3.bat"
if (Test-Path $bat) {
    $c = (Get-Content -Raw -Encoding Default $bat).Trim()
    $hasLaunch = ($c -match "bin\\YDWEConfig\.exe\s+-launchwar3")
    $hasPanel = ($c -match "keybind_panel")
    if ($hasLaunch -and $hasPanel) {
        Report "war3.bat" $true "launcher OK (YDWEConfig + keybind_panel)"
    } elseif ($hasLaunch) {
        Report "war3.bat" $false "missing keybind_panel daemon launcher"
    } else {
        Report "war3.bat" $false ("unexpected content: " + $c)
    }
} else {
    Report "war3.bat" $false "not found"
}

# 5. tools\keybind_panel.exe (v3 build: 59,904B, MD5 C5973F2E82B92403863C69BC25239961)
$sr = Join-Path $Target "tools\keybind_panel.exe"
if (Test-Path $sr) {
    $srSize = (Get-Item $sr).Length
    $srMd5 = (Get-FileHash -Algorithm MD5 $sr).Hash.ToUpper()
    if ($srSize -eq 59904 -and $srMd5 -eq "C5973F2E82B92403863C69BC25239961") {
        Report "tools\keybind_panel.exe" $true "59,904B C5973F2E (v3 auto-read hotkeys) OK"
    } else {
        Report "tools\keybind_panel.exe" $false ("size=" + $srSize + " md5=" + $srMd5 + " (expected 59904B / C5973F2E...)")
    }
} else {
    Report "tools\keybind_panel.exe" $false "not found"
}

# 6. Maps\Download exists (map drop folder)
$maps = Join-Path $Target "Maps\Download"
if (Test-Path $maps) {
    $n = (Get-ChildItem $maps -Filter *.w3x -ErrorAction SilentlyContinue | Measure-Object).Count
    Report "Maps\Download" $true ("folder OK, w3x count: " + $n)
} else {
    Report "Maps\Download" $false "folder missing"
}

# 7. injector green package (runtime unlock payload: kkemu_unlock.dll generic 6-native build)
#    Expected: engine\unlock\kkemu_unlock.dll 46,592B SHA256 222039D9...8AC568
#              MapModGUI.exe 43,520B SHA256 1CB96DE1...BCB51 (GUI v1.5)
$injRoot = $Injector
if (-not $injRoot) {
    $cands = @(
        (Join-Path (Split-Path $Target -Parent) "注入器绿色包"),
        "C:\Users\Administrator\Desktop\注入器绿色包",
        "E:\aiwork\kk_re\deliverable\injector_green"
    )
    foreach ($c in $cands) { if (-not $injRoot -and (Test-Path (Join-Path $c "engine\unlock\kkemu_unlock.dll"))) { $injRoot = $c } }
}
if ($injRoot -and (Test-Path $injRoot)) {
    Write-Host ("   (injector package: {0})" -f $injRoot)
    $dll = Join-Path $injRoot "engine\unlock\kkemu_unlock.dll"
    if (Test-Path $dll) {
        $dSize = (Get-Item $dll).Length
        $dSha = (Get-FileHash -Algorithm SHA256 $dll).Hash.ToUpper()
        if ($dSize -eq 46592 -and $dSha -eq "222039D99349B969FF2DA6079EE7EF0B7719EDD37EF8E9FB988885C9178AC568") {
            Report "unlock\kkemu_unlock.dll" $true "46,592B 222039D9 (generic 6-native) OK"
        } else {
            Report "unlock\kkemu_unlock.dll" $false ("size=" + $dSize + " sha256=" + $dSha + " (expected 46592 / 222039D9...)")
        }
    } else {
        Report "unlock\kkemu_unlock.dll" $false "not found"
    }
    foreach ($n in @("inject_unlock.py", "hook32.ps1", "unlock_rva.json", "native_table.json", "datatype_map.flat.json", "push_policy.py", "config.json")) {
        Report ("unlock\" + $n) (Test-Path (Join-Path $injRoot ("engine\unlock\" + $n))) "payload file"
    }
    $stale = Join-Path $injRoot "engine\unlock\kkemu_unlock_t3.dll"
    Report "unlock\ no stale T3 dll" (-not (Test-Path $stale)) "old T3 must live in _legacy_t3\ only"
    $gui = Join-Path $injRoot "MapModGUI.exe"
    if (Test-Path $gui) {
        $gSize = (Get-Item $gui).Length
        $gSha = (Get-FileHash -Algorithm SHA256 $gui).Hash.ToUpper()
        if ($gSize -eq 43520 -and $gSha -eq "1CB96DE15B2692E897A3A4110E89EB20421C421977DD543C31851953BB9BCB51") {
            Report "MapModGUI.exe" $true "43,520B 1CB96DE1 (v1.5 generic-unlock GUI) OK"
        } else {
            Report "MapModGUI.exe" $false ("size=" + $gSize + " sha256=" + $gSha + " (expected 43520 / 1CB96DE1...)")
        }
    } else {
        Report "MapModGUI.exe" $false "not found"
    }
} else {
    Write-Host "   (injector package: not found - skipped; pass -Injector DIR to verify unlock payload)" -ForegroundColor Yellow
}

Write-Host ""
if ($ok) {
    Write-Host "ALL CHECKS PASSED" -ForegroundColor Green
    exit 0
}
Write-Host "SOME CHECKS FAILED - see [FAIL] lines above" -ForegroundColor Yellow
exit 1
