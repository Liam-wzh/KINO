param([string]$ExecutablePath)
$ErrorActionPreference = 'Stop'
$workspacePath = Split-Path -Parent $PSScriptRoot
if (-not $ExecutablePath) { $ExecutablePath = Join-Path $workspacePath 'src\Kino\bin\Release\net48\KINO.exe' }
$ExecutablePath = [System.IO.Path]::GetFullPath($ExecutablePath)
$settingsPath = Join-Path $env:LOCALAPPDATA 'KINO\settings.json'
$originalHash = if (Test-Path -LiteralPath $settingsPath) { (Get-FileHash -LiteralPath $settingsPath).Hash } else { '' }
$previewPath = Join-Path $workspacePath 'artifacts\previews'
New-Item -ItemType Directory -Path $previewPath -Force | Out-Null
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class KinoOnboardingProbe {
    public delegate bool EnumProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Bounds bounds);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [StructLayout(LayoutKind.Sequential)] public struct Bounds { public int Left,Top,Right,Bottom; }
    public static IntPtr Find(int processId, string title) {
        IntPtr result=IntPtr.Zero;
        EnumProc callback=(window, parameter)=>{
            uint pid; GetWindowThreadProcessId(window, out pid);
            var text=new StringBuilder(256); GetWindowText(window,text,256);
            if(pid==(uint)processId && text.ToString()==title) { result=window; return false; }
            return true;
        };
        EnumWindows(callback,IntPtr.Zero); return result;
    }
}
'@
$previousDpi = [KinoOnboardingProbe]::SetThreadDpiAwarenessContext([IntPtr](-4))
$process = Start-Process -FilePath $ExecutablePath -ArgumentList '--connect-preview' -PassThru -WindowStyle Hidden
try {
    $window = [IntPtr]::Zero
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        Start-Sleep -Milliseconds 100
        if ($process.HasExited) { throw 'The process exited before showing onboarding.' }
        $window = [KinoOnboardingProbe]::Find($process.Id, '连接站点 · KINO')
        if ($window -ne [IntPtr]::Zero) { break }
    }
    if ($window -eq [IntPtr]::Zero) { throw 'The first-run connection window was not shown.' }
    Start-Sleep -Milliseconds 500
    $bounds = New-Object KinoOnboardingProbe+Bounds
    [KinoOnboardingProbe]::GetWindowRect($window, [ref]$bounds) | Out-Null
    $width = $bounds.Right - $bounds.Left; $height = $bounds.Bottom - $bounds.Top
    if ($width -le 0 -or $height -le 0) { throw 'The onboarding window has no visible size.' }
    $bitmap = [System.Drawing.Bitmap]::new($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $bitmap.Size)
        $bitmap.Save((Join-Path $previewPath 'connect-live.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
    [KinoOnboardingProbe]::PostMessage($window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    if (-not $process.WaitForExit(5000)) { throw 'The preview did not exit after closing onboarding.' }
    if ($process.ExitCode -ne 0) { throw 'Onboarding preview exited with an error.' }
    $currentHash = if (Test-Path -LiteralPath $settingsPath) { (Get-FileHash -LiteralPath $settingsPath).Hash } else { '' }
    if ($currentHash -ne $originalHash) { throw 'The preview changed saved user settings.' }
    Write-Output 'PASS first-run connection window, clean exit and preserved user settings.'
} finally {
    if (-not $process.HasExited) {
        [System.IO.File]::WriteAllText((Join-Path (Split-Path -Parent $ExecutablePath) 'shutdown.sentinel'), '')
        $process.WaitForExit(5000) | Out-Null
    }
    [KinoOnboardingProbe]::SetThreadDpiAwarenessContext($previousDpi) | Out-Null
}
