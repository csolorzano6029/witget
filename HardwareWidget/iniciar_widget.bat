@echo off
cd /d "%~dp0"
set "EXE_PATH=%~dp0bin\Release\net8.0-windows\HardwareWidget.exe"
if exist "%EXE_PATH%" (
    start "" "%EXE_PATH%"
) else (
    dotnet run -c Release
)
