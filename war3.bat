@echo off
title KK Local Suite - Play
cd /d %~dp0

rem （可选工具）先启动 SuWar3Tools 改键/调视距工具，后台常驻
for /f "delims=" %%f in ('dir /b "tools\SuWar3Tools\*.exe" 2^>nul') do (start "" "tools\SuWar3Tools\%%f" & goto :tool_ok)
:tool_ok

rem （可选）热键桥：游戏内按 F7 呼出菜单，F8 关闭菜单
if exist "tools\HotkeyBridge\HotkeyBridge.exe" start "" "tools\HotkeyBridge\HotkeyBridge.exe"

rem 启动游戏（按注册表 InstallPath 定位魔兽）
bin\YDWEConfig.exe -launchwar3
