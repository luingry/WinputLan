param(
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts\prototype-fidelity-smoke.png'),
    [switch]$OpenHotkeyOverlay
)
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WinputCapture {
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
 [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
$process = Get-Process WinputLan -ErrorAction Stop | Select-Object -First 1
$null = [WinputCapture]::ShowWindowAsync($process.MainWindowHandle, 9)
$null = [WinputCapture]::SetWindowPos($process.MainWindowHandle, [IntPtr](-1), 0, 0, 0, 0, 3)
$null = [WinputCapture]::SetForegroundWindow($process.MainWindowHandle)
if ($OpenHotkeyOverlay) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Editar atalhos')
    $button = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $button) { throw 'Botão Editar atalhos não foi localizado pela automação.' }
    $invoke = $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    ([System.Windows.Automation.InvokePattern]$invoke).Invoke()
    Start-Sleep -Milliseconds 150
}
$rect = New-Object WinputCapture+RECT
if (-not [WinputCapture]::GetWindowRect($process.MainWindowHandle, [ref]$rect)) { throw 'Não foi possível localizar a janela Winput LAN.' }
$width = $rect.Right - $rect.Left; $height = $rect.Bottom - $rect.Top
$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
$directory = Split-Path -Parent $Output; New-Item -ItemType Directory -Force -Path $directory | Out-Null
$bitmap.Save($Output, [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose(); $bitmap.Dispose(); Write-Output $Output
