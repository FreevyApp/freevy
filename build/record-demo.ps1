# Records the README demo: a demo Freevy (separate data folder with the made-up snippets in
# build\demo\data) expands snippets typed into a neutral "New message" page (build\demo\message.html).
# Build the app in Release first, then run this while nobody touches the keyboard or mouse. It stops
# as soon as the demo window loses focus, so keys never land in another app, and starts the
# installed Freevy again at the end. Needs ffmpeg and Microsoft Edge.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File build\record-demo.ps1
#
# The raw recording lands in build\out\demo\demo.mkv; trim it into docs\images\freevy-demo.gif with
# the ffmpeg commands in docs\recording-the-demo.md.
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\src\Freevy.App\bin\Release\net10.0-windows\Freevy.exe'),
    [string]$Out = (Join-Path $PSScriptRoot 'out\demo')
)
$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Keys {
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] struct UNION { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public int type; public UNION u; }
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern short VkKeyScan(char c);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attribute, out RECT rect, int size);
    // The window as drawn on screen, without the invisible resize borders.
    public static RECT Bounds(IntPtr h) { RECT r; DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(RECT))); return r; }
    static INPUT K(int vk, bool up) { var i = new INPUT { type = 1 }; i.u.ki.wVk = (ushort)vk; i.u.ki.dwFlags = up ? 2u : 0u; return i; }
    static void Send(params INPUT[] i) { SendInput((uint)i.Length, i, Marshal.SizeOf(typeof(INPUT))); }
    // Real virtual keys (like a keyboard or the on-screen keyboard), so the text expander sees ordinary typing.
    public static void Type(char c) {
        if (c == '\n') { Send(K(0x0D, false), K(0x0D, true)); return; }
        short scan = VkKeyScan(c);
        int vk = scan & 0xFF, mods = (scan >> 8) & 0xFF;
        var list = new System.Collections.Generic.List<INPUT>();
        if ((mods & 2) != 0) list.Add(K(0x11, false));
        if ((mods & 4) != 0) list.Add(K(0x12, false));
        if ((mods & 1) != 0) list.Add(K(0x10, false));
        list.Add(K(vk, false)); list.Add(K(vk, true));
        if ((mods & 1) != 0) list.Add(K(0x10, true));
        if ((mods & 4) != 0) list.Add(K(0x12, true));
        if ((mods & 2) != 0) list.Add(K(0x11, true));
        Send(list.ToArray());
    }
}
'@

New-Item -ItemType Directory -Force $Out | Out-Null
$edgeProfile = Join-Path $Out 'edge-profile'
# Freevy writes to its data folder, so every recording starts from a fresh copy of the demo snippets.
$Data = Join-Path $Out 'data'
Remove-Item -Recurse -Force $Data -ErrorAction SilentlyContinue
Copy-Item -Recurse (Join-Path $PSScriptRoot 'demo\data') $Data
$Url = 'file:///' + ((Resolve-Path (Join-Path $PSScriptRoot 'demo\message.html')).Path -replace '\\', '/')

Write-Output '== Demo Freevy'
Get-Process Freevy -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
Start-Process $Exe -ArgumentList '--tray', '--data', "`"$Data`""
Start-Sleep -Seconds 5

Write-Output '== Message window'
$edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
Start-Process $edge -ArgumentList "--app=$Url", '--window-size=880,620', '--window-position=120,80', "--user-data-dir=`"$edgeProfile`"", '--no-first-run', '--disable-features=Translate'
$window = [IntPtr]::Zero
for ($i = 0; $i -lt 40 -and $window -eq [IntPtr]::Zero; $i++) {
    Start-Sleep -Milliseconds 250
    $window = (Get-Process msedge -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -eq 'New message' } | Select-Object -First 1).MainWindowHandle
    if ($null -eq $window) { $window = [IntPtr]::Zero }
}
if ($window -eq [IntPtr]::Zero) { throw 'The message window did not open.' }
Start-Sleep -Seconds 2
[Keys]::SetForegroundWindow($window) | Out-Null
Start-Sleep -Milliseconds 600

Write-Output '== Recording'
$video = Join-Path $Out 'demo.mkv'
# A screen region, not the window itself: Chromium draws with the GPU, which window capture sees as blank.
$r = [Keys]::Bounds($window)
$w = ($r.Right - $r.Left) - (($r.Right - $r.Left) % 2)
$h = ($r.Bottom - $r.Top) - (($r.Bottom - $r.Top) % 2)
Write-Output "   region $($r.Left),$($r.Top) $($w)x$($h)"
Remove-Item $video -ErrorAction SilentlyContinue
$ffmpeg = Start-Process ffmpeg -ArgumentList '-y', '-loglevel', 'error', '-f', 'gdigrab', '-framerate', '25', '-draw_mouse', '0', '-offset_x', $r.Left, '-offset_y', $r.Top, '-video_size', "$($w)x$($h)", '-i', 'desktop', '-t', '24', '-c:v', 'libx264', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p', "`"$video`"" -PassThru -WindowStyle Hidden -RedirectStandardError (Join-Path $Out 'ffmpeg.log')
Start-Sleep -Seconds 1

$script = "Hi Sam,`n`n;thx Teh numbers for ;date are attached.`n`n;sig "
$aborted = $false
foreach ($c in $script.ToCharArray()) {
    if ([Keys]::GetForegroundWindow() -ne $window) { $aborted = $true; break }
    [Keys]::Type($c)
    # A pause after each snippet trigger lets the expansion finish before typing goes on.
    Start-Sleep -Milliseconds ($(if ($c -eq ' ' -or $c -eq "`n") { 260 } else { 95 }))
}
Start-Sleep -Seconds 3

Write-Output '== Cleanup'
$ffmpeg | Wait-Process -Timeout 40 -ErrorAction SilentlyContinue
Get-Process msedge -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -eq 'New message' } | ForEach-Object { $_.CloseMainWindow() | Out-Null }
Get-Process Freevy -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
# The installed Freevy again, started through WMI so it runs with the real user profile.
Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = "`"$env:USERPROFILE\AppData\Local\Freevy\current\Freevy.exe`" --tray" } | Out-Null
if ($aborted) { Write-Output 'ABORTED: the message window lost focus; nothing was typed elsewhere.' } else { Write-Output "DONE: $video" }
