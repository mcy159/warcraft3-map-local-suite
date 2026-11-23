@echo off
setlocal enabledelayedexpansion
title Map Local Suite - Uninstall （平台图离线游玩注入包 - 卸载）
rem ============================================================
rem  平台图离线游玩通用注入包 - 卸载
rem  功能: 还原 Game.dll （从 Game.dll.bak）, 删除 bin/plugin/war3.bat
rem  用法: uninstall.bat [游戏目录或war3.exe路径]   （可不带参数）
rem ============================================================

set "PKG=%~dp0"
if "%PKG:~-1%"=="\" set "PKG=%PKG:~0,-1%"

echo ============================================
echo  平台图离线游玩通用注入包 - 卸载
echo ============================================
echo.

set "TARGET="
if not "%~1"=="" (
  if /i "%~x1"==".exe" (
    set "TARGET=%~dp1"
  ) else (
    set "TARGET=%~1"
  )
)

if not defined TARGET (
  for /f "tokens=2,* delims= " %%a in ('reg query "HKCU\Software\Blizzard Entertainment\Warcraft III" /v InstallPath 2^>nul') do set "TARGET=%%b"
)
if not defined TARGET (
  for /f "tokens=2,* delims= " %%a in ('reg query "HKLM\SOFTWARE\Wow6432Node\Blizzard Entertainment\Warcraft III" /v InstallPath 2^>nul') do set "TARGET=%%b"
)
if not defined TARGET (
  for /f "tokens=2,* delims= " %%a in ('reg query "HKLM\SOFTWARE\Blizzard Entertainment\Warcraft III" /v InstallPath 2^>nul') do set "TARGET=%%b"
)

if not defined TARGET (
  if exist "%CD%\War3.exe" set "TARGET=%CD%"
)

if not defined TARGET (
  set /p TARGET=请输入魔兽争霸3游戏目录（含War3.exe的文件夹）: 
)

if defined TARGET (
  if "%TARGET:~-1%"=="\" set "TARGET=%TARGET:~0,-1%"
)

if not defined TARGET goto :abort
if not exist "%TARGET%\War3.exe" (
  echo [错误] 未找到 War3.exe: %TARGET%\War3.exe
  goto :abort
)
echo [信息] 目标游戏目录: %TARGET%
echo.

set "ANS="
set /p ANS=确认卸载? 将还原 Game.dll 并删除 bin/plugin/war3.bat （Y/N, 默认N）: 
if /i not "!ANS!"=="Y" goto :abort

if exist "%TARGET%\Game.dll.bak" (
  copy /y "%TARGET%\Game.dll.bak" "%TARGET%\Game.dll" >nul
  if errorlevel 1 (
    echo [错误] Game.dll 还原失败, 请关闭游戏后重试
  ) else (
    echo [OK] Game.dll 已还原为原版
    del /q "%TARGET%\Game.dll.bak" 2>nul
  )
) else (
  echo [信息] 未找到 Game.dll.bak, 跳过 Game.dll 还原
)

if exist "%TARGET%\bin\YDWEConfig.exe" (
  rd /s /q "%TARGET%\bin"
  echo [OK] 已删除 bin 目录
)
if exist "%TARGET%\plugin" (
  rd /s /q "%TARGET%\plugin"
  echo [OK] 已删除 plugin 目录
)
if exist "%TARGET%\war3.bat" (
  del /q "%TARGET%\war3.bat"
  echo [OK] 已删除 war3.bat
)

echo.
echo 卸载完成.
pause
exit /b 0

:abort
echo.
echo 已取消或目标无效, 未做任何修改.
pause
exit /b 1