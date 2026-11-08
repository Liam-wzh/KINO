param([string]$Configuration = 'Debug', [string]$ExecutablePath = '', [switch]$NormalMode)
$ErrorActionPreference = 'Stop'
$workspacePath = Split-Path -Parent $PSScriptRoot
$taskbarExecutablePath = if ([string]::IsNullOrWhiteSpace($ExecutablePath)) { Join-Path $workspacePath "src\Kino\bin\$Configuration\net48\KINO.exe" } else { [System.IO.Path]::GetFullPath($ExecutablePath) }
$previewPath = Join-Path $workspacePath 'artifacts\previews'
New-Item -ItemType Directory -Path $previewPath -Force | Out-Null
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class KinoWindowProbe {
    public delegate bool EnumProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Bounds bounds);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string name, string title);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [StructLayout(LayoutKind.Sequential)] public struct Bounds { public int Left,Top,Right,Bottom; }
    public static IntPtr Find(int processId, bool child) {
        IntPtr result=IntPtr.Zero;
        EnumProc callback=(window, parameter)=>{
            uint pid; GetWindowThreadProcessId(window, out pid);
            var name=new StringBuilder(256); GetWindowText(window,name,256);
            if(pid==(uint)processId && (child ? name.ToString()=="KINO.Taskbar" : name.ToString().Contains("AI "))) { result=window; return false; }
            return true;
        };
        if(child) EnumChildWindows(FindWindow("Shell_TrayWnd",null),callback,IntPtr.Zero); else EnumWindows(callback,IntPtr.Zero);
        return result;
    }
}
'@
function Save-KinoWindow([IntPtr]$Window, [string]$FileName) {
    $bounds = New-Object KinoWindowProbe+Bounds
    [KinoWindowProbe]::GetWindowRect($Window, [ref]$bounds) | Out-Null
    $width = $bounds.Right - $bounds.Left
    $height = $bounds.Bottom - $bounds.Top
    Write-Output ("Screen bounds: " + $bounds.Left + ',' + $bounds.Top + ' ' + $width + 'x' + $height)
    if ($width -le 0 -or $height -le 0) { throw 'Window bounds are empty.' }
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $bitmap.Size)
        if ($FileName -eq 'taskbar-live.png') {
            $markerPixels = 0
            for ($x = 0; $x -lt [Math]::Min(24, $width); $x++) {
                for ($y = 0; $y -lt $height; $y++) {
                    $pixel = $bitmap.GetPixel($x, $y)
                    if ($pixel.G -gt $pixel.R + 40 -and $pixel.G -gt $pixel.B + 10 -and $pixel.B -gt $pixel.R + 20) { $markerPixels++ }
                }
            }
            if ($markerPixels -lt 20) { throw 'The taskbar HWND exists, but its KINO accent marker is not visible.' }
        }
        if ($FileName -eq 'card-live.png') {
            $contentPixels = 0
            for ($x = 0; $x -lt $width; $x += 2) {
                for ($y = [int]($height * .49); $y -lt [int]($height * .72); $y += 2) {
                    $pixel = $bitmap.GetPixel($x, $y)
                    if ($pixel.G -gt $pixel.R + 30 -and $pixel.B -gt $pixel.R + 20) { $contentPixels++ }
                }
            }
            if ($contentPixels -lt 100) { throw 'The flyout HWND exists, but its chart and accent content are not visible.' }
        }
        $bitmap.Save((Join-Path $previewPath $FileName), [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
    Write-Output "$FileName ${width}x${height}"
}
$previousDpi = [KinoWindowProbe]::SetThreadDpiAwarenessContext([IntPtr](-4))
$process = if ($NormalMode) { Start-Process -FilePath $taskbarExecutablePath -WindowStyle Hidden -PassThru } else { Start-Process -FilePath $taskbarExecutablePath -ArgumentList '--diagnose' -WindowStyle Hidden -PassThru }
try {
    $overlay = [IntPtr]::Zero
    for ($attempt = 0; $attempt -lt 25; $attempt++) {
        Start-Sleep -Milliseconds 100
        $overlay = [KinoWindowProbe]::Find($process.Id, $true)
        if ($overlay -ne [IntPtr]::Zero) { break }
    }
    if ($overlay -eq [IntPtr]::Zero) { throw 'The taskbar overlay was not embedded.' }
    if ([KinoWindowProbe]::GetAncestor($overlay, 1) -eq [IntPtr]::Zero) { throw 'The overlay is not a taskbar child.' }
    Start-Sleep -Milliseconds 500
    Save-KinoWindow $overlay 'taskbar-live.png'
    Save-KinoWindow ([KinoWindowProbe]::FindWindow('Shell_TrayWnd', $null)) 'taskbar-full.png'
    $initialPopup = [KinoWindowProbe]::Find($process.Id, $false)
    if ($initialPopup -ne [IntPtr]::Zero) {
        [KinoWindowProbe]::PostMessage($initialPopup, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        Start-Sleep -Milliseconds 250
    }
    [KinoWindowProbe]::PostMessage($overlay, 0x0202, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    $popup = [IntPtr]::Zero
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        Start-Sleep -Milliseconds 100
        $popup = [KinoWindowProbe]::Find($process.Id, $false)
        if ($popup -ne [IntPtr]::Zero) { break }
    }
    if ($popup -eq [IntPtr]::Zero) { throw 'Clicking the overlay did not open the flyout.' }
    Start-Sleep -Milliseconds 1200
    Save-KinoWindow $popup 'card-live.png'
    [KinoWindowProbe]::PostMessage($overlay, 0x0202, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 200
    if ([KinoWindowProbe]::Find($process.Id, $false) -ne [IntPtr]::Zero) { throw 'Clicking the overlay again did not close the flyout.' }
    Write-Output 'PASS actual taskbar embedding, Direct2D surface, click-to-open and click-to-close.'
} finally {
    if (-not $process.HasExited) {
        $sentinelPath = Join-Path (Split-Path -Parent $taskbarExecutablePath) 'shutdown.sentinel'
        [System.IO.File]::WriteAllText($sentinelPath, '')
        $process.WaitForExit(5000) | Out-Null
    }
    [KinoWindowProbe]::SetThreadDpiAwarenessContext($previousDpi) | Out-Null
}
