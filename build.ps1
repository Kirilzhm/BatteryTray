param([string]$Out = "BatteryTray.exe", [string]$Target = "winexe")
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
Set-Location $PSScriptRoot
& $csc /nologo /target:$Target /optimize+ /out:$Out /win32manifest:app.manifest `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll `
    (Get-ChildItem *.cs).Name
