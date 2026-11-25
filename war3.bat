@echo off
title KK Local Suite - Play
cd /d %~dp0
rem Multi-copy: point registry to this dir (YDWEConfig uses registry to find game)
set "GDIR=%~dp0"
set "GDIR=%GDIR:~0,-1%"
echo Y| reg add "HKCU\Software\Blizzard Entertainment\Warcraft III" /v InstallPath /d "%GDIR%" /f >nul 2>&1
echo Y| reg add "HKLM\SOFTWARE\WOW6432Node\Blizzard Entertainment\Warcraft III" /v InstallPath /d "%GDIR%" /f >nul 2>&1
rem Keybind panel (改键面板: 面板改键 + 血条默认开 + 视距 0/.)
if exist "tools\keybind_panel.exe" start "KeybindPanel" "tools\keybind_panel.exe"
bin\YDWEConfig.exe -launchwar3