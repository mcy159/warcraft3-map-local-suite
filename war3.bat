@echo off
title KK Local Suite - Play
cd /d %~dp0
rem Multi-copy: point registry to this dir (YDWEConfig uses registry to find game)
echo Y| reg add "HKCU\Software\Blizzard Entertainment\Warcraft III" /v InstallPath /d "%~dp0" /f >nul 2>&1
echo Y| reg add "HKLM\SOFTWARE\WOW6432Node\Blizzard Entertainment\Warcraft III" /v InstallPath /d "%~dp0" /f >nul 2>&1
rem Optional: start SuWar3Tools (keyboard/camera tool) in background
for /f "delims=" %%f in ('dir /b "tools\SuWar3Tools\*.exe" 2^>nul') do (start "" "tools\SuWar3Tools\%%f" & goto :tool_ok)
:tool_ok
rem Skillbar calibrator (writes SuWar3Tools.cfg automatically)
if exist "tools\skillbar_reader.exe" start "SkillbarCal" /min "tools\skillbar_reader.exe" --daemon --log "tools\skillbar_cal.log"
bin\YDWEConfig.exe -launchwar3