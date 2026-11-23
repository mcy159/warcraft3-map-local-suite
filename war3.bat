@echo off
title KK Local Suite - Play
cd /d %~dp0

rem 多魔兽共存：从哪个目录启动，注册表就指向哪个（YDWEConfig 按注册表找游戏）
reg add "HKCU\Software\Blizzard Entertainment\Warcraft III" /v InstallPath /d "%~dp0" /f >nul 2>&1
reg add "HKLM\SOFTWARE\WOW6432Node\Blizzard Entertainment\Warcraft III" /v InstallPath /d "%~dp0" /f >nul 2>&1

rem （可选工具）先启动 SuWar3Tools 改键/调视距工具，后台常驻
for /f "delims=" %%f in ('dir /b "tools\SuWar3Tools\*.exe" 2^>nul') do (start "" "tools\SuWar3Tools\%%f" & goto :tool_ok)
:tool_ok

rem 技能栏内存校准器（后台轮询等进图写 SuWar3Tools.cfg，免手动校准）
if exist "tools\skillbar_reader.exe" start "SkillbarCal" /min "tools\skillbar_reader.exe" --daemon --log "tools\skillbar_cal.log"
bin\YDWEConfig.exe -launchwar3