@echo off
cd /d "%~dp0"
set "EXE_PATH=%~dp0bin\Release\net8.0-windows\HardwareWidget.exe"
if exist "%EXE_PATH%" (
    powershell -NoProfile -WindowStyle Hidden -Command "Start-Process -FilePath '%EXE_PATH%' -WorkingDirectory '%~dp0' -Verb RunAs"
) else (
    dotnet run -c Release
)
