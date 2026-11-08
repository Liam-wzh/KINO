param([string]$ExecutablePath, [switch]$RequireVisible)
$ErrorActionPreference = 'Stop'
$workspacePath = Split-Path -Parent $PSScriptRoot
if (-not $ExecutablePath) { $ExecutablePath = Join-Path $workspacePath 'src/Kino/bin/Release/net48/KINO.exe' }
$ExecutablePath = [System.IO.Path]::GetFullPath($ExecutablePath)
$statusPath = Join-Path (Split-Path -Parent $ExecutablePath) 'notification-preview.log'
$previewPath = Join-Path $workspacePath 'artifacts/previews'
New-Item -ItemType Directory -Path $previewPath -Force | Out-Null
$userFiles = @((Join-Path $env:LOCALAPPDATA 'KINO/settings.json'), (Join-Path $env:LOCALAPPDATA 'KINO/alert-history.json'))
$originalHashes = @($userFiles | ForEach-Object { if (Test-Path -LiteralPath $_) { (Get-FileHash -LiteralPath $_).Hash } else { '' } })
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class KinoNotificationProbe {
    public delegate bool EnumProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string name, string title);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Bounds bounds);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [StructLayout(LayoutKind.Sequential)] public struct Bounds { public int Left,Top,Right,Bottom; }
    public static IntPtr Find(int processId, bool notifyIcon) {
        IntPtr result=IntPtr.Zero;
        EnumProc callback=(window, parameter)=>{
            uint pid; GetWindowThreadProcessId(window,out pid);
            if(pid!=(uint)processId) return true;
            var text=new StringBuilder(256); GetWindowText(window,text,256);
            var name=new StringBuilder(256); GetClassName(window,name,256);
            if(notifyIcon ? name.ToString().StartsWith("WindowsForms10.Window") : text.ToString().Contains("AI ")) { result=window; return false; }
            return true;
        };
        EnumWindows(callback,IntPtr.Zero);
        if(notifyIcon && result==IntPtr.Zero) {
            var message=IntPtr.Zero;
            while((message=FindWindowEx(new IntPtr(-3),message,null,null))!=IntPtr.Zero)
                if(!callback(message,IntPtr.Zero)) break;
        }
        return result;
    }
}
'@
$previousDpi = [KinoNotificationProbe]::SetThreadDpiAwarenessContext([IntPtr](-4))
$process = Start-Process -FilePath $ExecutablePath -ArgumentList '--notification-preview' -WindowStyle Hidden -PassThru
try {
    $notifyWindow = [IntPtr]::Zero
    $status = ''
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        Start-Sleep -Milliseconds 100
        if ($process.HasExited) { throw 'The notification preview exited before sending a test notification.' }
        $notifyWindow = [KinoNotificationProbe]::Find($process.Id, $true)
        if (Test-Path -LiteralPath $statusPath) { $status = [System.IO.File]::ReadAllText($statusPath) }
        if ($notifyWindow -ne [IntPtr]::Zero -and $status.Contains('submitted') -and $status.Contains('shown')) { break }
    }
    if ($notifyWindow -eq [IntPtr]::Zero) { throw 'The native notification callback window was not created.' }
    if (-not $status.Contains('submitted')) { throw 'The test notification was not submitted.' }
    if ($status.Contains('shown')) { Write-Output 'PASS Windows reported displaying the demo notification.' }
    elseif ($RequireVisible) { throw 'Windows did not report showing the notification. Check its notification and Do Not Disturb settings.' }
    else { Write-Output 'Notification submitted; Windows did not report showing it (system settings may suppress display).' }
    # Deliver the same native callback Windows sends when the balloon is clicked.
    # This checks activation independently of notification suppression by the OS.
    [KinoNotificationProbe]::PostMessage($notifyWindow, 0x0800, [IntPtr]::Zero, [IntPtr]0x0405) | Out-Null
    $card = [IntPtr]::Zero
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        Start-Sleep -Milliseconds 100
        $card = [KinoNotificationProbe]::Find($process.Id, $false)
        if ($card -ne [IntPtr]::Zero) { break }
    }
    if ($card -eq [IntPtr]::Zero -or -not [System.IO.File]::ReadAllText($statusPath).Contains('clicked')) { throw 'The notification click callback did not open the card.' }
    Start-Sleep -Milliseconds 700
    $bounds = New-Object KinoNotificationProbe+Bounds
    [KinoNotificationProbe]::GetWindowRect($card, [ref]$bounds) | Out-Null
    $width = $bounds.Right - $bounds.Left; $height = $bounds.Bottom - $bounds.Top
    if ($width -le 0 -or $height -le 0) { throw 'The notification card has no visible size.' }
    $bitmap = [System.Drawing.Bitmap]::new($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $bitmap.Size)
        $bitmap.Save((Join-Path $previewPath 'notification-card-live.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
    [KinoNotificationProbe]::PostMessage($card, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    if (-not $process.WaitForExit(5000) -or $process.ExitCode -ne 0) { throw 'The notification preview did not exit cleanly.' }
    for ($index = 0; $index -lt $userFiles.Count; $index++) {
        $hash = if (Test-Path -LiteralPath $userFiles[$index]) { (Get-FileHash -LiteralPath $userFiles[$index]).Hash } else { '' }
        if ($hash -ne $originalHashes[$index]) { throw 'The notification preview changed saved user settings or cooldown history.' }
    }
    Copy-Item -LiteralPath $statusPath -Destination (Join-Path $previewPath 'notification-preview.log') -Force
    Write-Output 'PASS notification submission, native click callback, demo card, clean exit and preserved user files.'
} finally {
    if (-not $process.HasExited) {
        [System.IO.File]::WriteAllText((Join-Path (Split-Path -Parent $ExecutablePath) 'shutdown.sentinel'), '')
        $process.WaitForExit(5000) | Out-Null
    }
    [KinoNotificationProbe]::SetThreadDpiAwarenessContext($previousDpi) | Out-Null
}
