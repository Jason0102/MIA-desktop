Option Explicit
Dim shell, fs, exe
Set shell = CreateObject("WScript.Shell")
Set fs = CreateObject("Scripting.FileSystemObject")
exe = fs.BuildPath(fs.GetParentFolderName(WScript.ScriptFullName), "dist\MIA-3.2-win-x64\MIA.exe")
If fs.FileExists(exe) Then
  shell.Run Chr(34) & exe & Chr(34) & " --exit", 0, False
End If
