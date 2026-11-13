Set WshShell = CreateObject("Shell.Application")
Set FSO = CreateObject("Scripting.FileSystemObject")
CurrentDir = FSO.GetParentFolderName(WScript.ScriptFullName)
ExePath = CurrentDir & "\bin\Release\net8.0-windows\HardwareWidget.exe"
WshShell.ShellExecute ExePath, "", CurrentDir, "runas", 1

