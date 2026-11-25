# -*- coding: utf-8 -*-
"""生成 deploy.bat / uninstall.bat (GBK 编码, Win7-Win11 cmd 兼容)。
用法: python gen_bat.py   -> 在当前目录写出 GBK 编码的两个 bat。
"""
import io, os

DEPLOY = r'''@echo off
setlocal enabledelayedexpansion
title Map Local Suite - Deploy (平台图离线游玩注入包)
rem ============================================================
rem  平台图离线游玩通用注入包 - 一键部署
rem  功能: 注入 japi 插件 + Game.dll 大图补丁 + 生成 war3.bat
rem  用法: deploy.bat [游戏目录或war3.exe路径]   (可不带参数)
rem  扩展: 设环境变量 NO_REG=1 跳过注册表询问, NO_MAP=1 跳过地图复制询问
rem ============================================================

set "PKG=%~dp0"
if "%PKG:~-1%"=="\" set "PKG=%PKG:~0,-1%"

echo ============================================
echo  平台图离线游玩通用注入包 - 部署
echo ============================================
echo.

rem ---------- 1. 定位目标游戏目录 ----------
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
  set /p TARGET=请输入魔兽争霸3游戏目录(含War3.exe的文件夹): 
)

rem 去掉尾部反斜杠
if defined TARGET (
  if "%TARGET:~-1%"=="\" set "TARGET=%TARGET:~0,-1%"
)

rem ---------- 2. 校验 ----------
if not defined TARGET goto :fail
if not exist "%TARGET%\War3.exe" (
  echo [错误] 未找到 War3.exe: %TARGET%\War3.exe
  goto :fail
)
if not exist "%TARGET%\Game.dll" (
  echo [错误] 未找到 Game.dll: %TARGET%\Game.dll
  goto :fail
)
echo [信息] 目标游戏目录: %TARGET%
echo.

rem ---------- 3. Game.dll 大图补丁 ----------
for /f "skip=1 delims=" %%a in ('certutil -hashfile "%TARGET%\Game.dll" MD5') do (
  set "GMD5=%%a"
  goto :md5done
)
:md5done
echo [信息] 当前 Game.dll MD5: %GMD5%

if /i "%GMD5%"=="267861A0DFD416DBAD13E7EE3EC7794A" (
  echo [信息] 检测到 1.27 原版 Game.dll, 执行备份与打补丁...
  if not exist "%TARGET%\Game.dll.bak" (
    copy /y "%TARGET%\Game.dll" "%TARGET%\Game.dll.bak" >nul
    if errorlevel 1 (
      echo [错误] 备份 Game.dll 失败, 请确认游戏已关闭且目录可写
      goto :fail
    )
    echo [OK] 已备份 Game.dll.bak
  ) else (
    echo [信息] 已存在 Game.dll.bak, 跳过备份
  )
  copy /y "%PKG%\Game_patched2.dll" "%TARGET%\Game.dll" >nul
  if errorlevel 1 (
    echo [错误] 覆盖 Game.dll 失败, 请确认游戏已关闭且目录可写
    goto :fail
  )
  echo [OK] Game.dll 已替换为大图补丁版 (8MB -^> 1GB)
) else (
  if /i "%GMD5%"=="690A546C9567C598262518E830286CC3" (
    echo [信息] Game.dll 已是补丁版, 跳过覆盖
  ) else (
    echo [警告] Game.dll MD5 与已知版本不匹配!
    echo [警告] 当前MD5: %GMD5%
    echo [警告] 本包仅支持 1.27.0.52240 原版, 不覆盖 Game.dll
  )
)
echo.

rem ---------- 4. 复制运行库与插件 ----------
if exist "%PKG%\bin" (
  if not exist "%TARGET%\bin" mkdir "%TARGET%\bin"
  xcopy /e /i /y "%PKG%\bin\*.*" "%TARGET%\bin\" >nul
  if not exist "%TARGET%\bin\ydbase.dll" (
    echo [警告] bin 复制可能失败, 请检查 ydbase.dll
  ) else (
    echo [OK] 运行库已复制到 bin\
  )
)
if exist "%PKG%\plugin\warcraft3" (
  if not exist "%TARGET%\plugin\warcraft3" mkdir "%TARGET%\plugin\warcraft3"
  xcopy /e /i /y "%PKG%\plugin\warcraft3\*.*" "%TARGET%\plugin\warcraft3\" >nul
  if not exist "%TARGET%\plugin\warcraft3\dzclient_api.dll" (
    echo [警告] plugin 复制可能失败, 请检查 dzclient_api.dll
  ) else (
    echo [OK] 插件已复制到 plugin\warcraft3\
  )
)

if exist "%PKG%\tools" (
  if not exist "%TARGET%\tools" mkdir "%TARGET%\tools"
  xcopy /e /i /y "%PKG%\tools\*.*" "%TARGET%\tools\" >nul
  echo [OK] keybind panel copied to tools\
)

rem ---------- 5. 生成 war3.bat ----------
if exist "%PKG%\war3.bat" (
  copy /y "%PKG%\war3.bat" "%TARGET%\war3.bat" >nul
) else (
  > "%TARGET%\war3.bat" echo bin\YDWEConfig.exe -launchwar3
)
if errorlevel 1 (
  echo [错误] 写入 war3.bat 失败
  goto :fail
)
echo [OK] 已生成 war3.bat
echo.

rem ---------- 6. 可选: 写注册表 InstallPath ----------
if /i "%NO_REG%"=="1" goto :skip_reg
set "REGANS="
set /p REGANS=是否写入注册表 InstallPath 指向游戏目录? (Y/N, 默认N): 
if /i "!REGANS!"=="Y" (
  reg add "HKCU\Software\Blizzard Entertainment\Warcraft III" /v InstallPath /t REG_SZ /d "%TARGET%" /f >nul 2>&1
  echo [OK] 已写入 HKCU InstallPath
  reg add "HKLM\SOFTWARE\Wow6432Node\Blizzard Entertainment\Warcraft III" /v InstallPath /t REG_SZ /d "%TARGET%" /f >nul 2>&1
  if errorlevel 1 (
    echo [提示] HKLM(Wow6432Node) 写入失败, 需要管理员权限; HKCU 已够用
  ) else (
    echo [OK] 已写入 HKLM(Wow6432Node) InstallPath
  )
  reg add "HKLM\SOFTWARE\Blizzard Entertainment\Warcraft III" /v InstallPath /t REG_SZ /d "%TARGET%" /f >nul 2>&1
  if errorlevel 1 (
    echo [提示] HKLM 写入失败, 需要管理员权限
  ) else (
    echo [OK] 已写入 HKLM InstallPath
  )
) else (
  echo [跳过] 未写入注册表
)
:skip_reg
echo.

rem ---------- 7. 可选: 复制地图 ----------
if /i "%NO_MAP%"=="1" goto :skip_map
if exist "%PKG%\maps\*.w3x" (
  set "MAPANS="
  set /p MAPANS=检测到 maps 目录有地图, 是否复制到游戏 Maps\Download? (Y/N, 默认Y): 
  if /i not "!MAPANS!"=="N" (
    if not exist "%TARGET%\Maps\Download" mkdir "%TARGET%\Maps\Download"
    copy /y "%PKG%\maps\*.w3x" "%TARGET%\Maps\Download\" >nul
    if errorlevel 1 (
      echo [警告] 地图复制失败, 请手动复制
    ) else (
      echo [OK] 地图已复制到 Maps\Download
    )
  ) else (
    echo [跳过] 未复制地图
  )
) else (
  echo [信息] maps 目录无地图文件, 跳过 (可稍后手动复制到游戏目录\Maps\Download)
)
:skip_map
echo.
goto :done

:fail
echo.
echo 部署未完成, 请检查上面的错误信息.
pause
exit /b 1

:done
echo ============================================
echo  部署完成!
echo  游戏目录 : %TARGET%
echo  启动方式 : 双击 %TARGET%\war3.bat
echo  放图位置 : %TARGET%\Maps\Download  (或使用本包 maps\ 目录自动复制)
echo  验证脚本 : powershell -ExecutionPolicy Bypass -File "%PKG%\verify.ps1" "%TARGET%"
echo ============================================
pause
exit /b 0
'''

UNINSTALL = r'''@echo off
setlocal enabledelayedexpansion
title Map Local Suite - Uninstall (平台图离线游玩注入包 - 卸载)
rem ============================================================
rem  平台图离线游玩通用注入包 - 卸载
rem  功能: 还原 Game.dll (从 Game.dll.bak), 删除 bin/plugin/war3.bat
rem  用法: uninstall.bat [游戏目录或war3.exe路径]   (可不带参数)
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
  set /p TARGET=请输入魔兽争霸3游戏目录(含War3.exe的文件夹): 
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
set /p ANS=确认卸载? 将还原 Game.dll 并删除 bin/plugin/war3.bat (Y/N, 默认N): 
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
'''

PATCHED2_MD5 = "690a546c9567c598262518e830286cc3"  # 690A546C9567C598262518E830286CC3 真实值, 已验证

def sanitize_bat(text):
    """cmd 括号块(if/for 的 (...))内, echo/set 文本中的半角括号会被当作块结构,
    导致 ': was unexpected at this time.'。对非结构行统一把半角括号转全角。"""
    out = []
    for line in text.splitlines():
        s = line.strip()
        if s.startswith(("if ", "for ", "else", "goto ", ":", "(", ")")) or s in (")", "("):
            out.append(line)  # 结构行: 保留
        elif "(" in line or ")" in line:
            out.append(line.replace("(", "（").replace(")", "）"))
        else:
            out.append(line)
    return "\n".join(out)

def main():
    here = os.path.dirname(os.path.abspath(__file__))
    for name, content in (("deploy.bat", DEPLOY), ("uninstall.bat", UNINSTALL)):
        content = content.replace("690A546C9567C598262518E830286CC3", PATCHED2_MD5)
        content = sanitize_bat(content)
        path = os.path.join(here, name)
        with io.open(path, "w", encoding="gbk", newline="\r\n") as f:
            f.write(content)
        print("[OK] wrote %s (%d bytes)" % (path, len(content.encode("gbk"))))

if __name__ == "__main__":
    main()
