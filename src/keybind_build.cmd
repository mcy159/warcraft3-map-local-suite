@echo off
rem keybind_v3.exe build (csc .NET Framework 4.x, no deps)
rem run in saved_src, output ..\verify_tmp\keybind_v3.exe
rem /codepage:65001 forces UTF-8 source reading (file is UTF-8 no BOM)
setlocal
set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [ERR] csc.exe not found
    exit /b 1
)
"%CSC%" /nologo /optimize+ /codepage:65001 /target:winexe /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:..\verify_tmp\keybind_v3.exe keybind.cs
if errorlevel 1 (
    echo [ERR] build failed, see messages above
    exit /b 1
)
echo [OK] keybind_v3.exe -^> ..\verify_tmp\keybind_v3.exe
endlocal
