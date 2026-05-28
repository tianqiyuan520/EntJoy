@echo off
call "D:\Microsoft Visual Studio\2022\Community\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64
cd /d D:\Godot\Project\EntJoy
msbuild src\NativeDll\NativeDll.vcxproj /p:Configuration=Release /p:Platform=x64 /t:Rebuild
if %ERRORLEVEL% NEQ 0 (
    echo Build FAILED with error code %ERRORLEVEL%
    exit /b %ERRORLEVEL%
) else (
    echo Build SUCCEEDED
    exit /b 0
)
