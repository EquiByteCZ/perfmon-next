#requires -Version 5.1
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$OutputDir
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$src = @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class NativeShot {
    public const uint PW_RENDERFULLCONTENT = 2;
    public const int SW_RESTORE = 9;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct DEVMODE {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion; public short dmDriverVersion; public short dmSize; public short dmDriverExtra;
        public int dmFields; public int dmPositionX; public int dmPositionY; public int dmDisplayOrientation; public int dmDisplayFixedOutput;
        public short dmColor; public short dmDuplex; public short dmYResolution; public short dmTTOption; public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels; public int dmBitsPerPel; public int dmPelsWidth; public int dmPelsHeight;
        public int dmDisplayFlags; public int dmDisplayFrequency;
        public int dmICMMethod; public int dmICMIntent; public int dmMediaType; public int dmDitherType;
        public int dmReserved1; public int dmReserved2; public int dmPanningWidth; public int dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Ansi)] public static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
    [DllImport("user32.dll", CharSet = CharSet.Ansi)] public static extern int ChangeDisplaySettings(ref DEVMODE devMode, int flags);

    public static string TryResolution(int w, int h) {
        var dm = new DEVMODE();
        dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        if (!EnumDisplaySettings(null, -1, ref dm)) return "EnumDisplaySettings failed";
        string before = dm.dmPelsWidth + "x" + dm.dmPelsHeight;
        dm.dmPelsWidth = w; dm.dmPelsHeight = h;
        dm.dmFields = 0x80000 | 0x100000;
        int rc = ChangeDisplaySettings(ref dm, 0);
        return "was " + before + ", ChangeDisplaySettings(" + w + "x" + h + ") = " + rc;
    }

    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    public static void CaptureScreen(IntPtr hwnd, string path) {
        RECT r;
        if (!GetWindowRect(hwnd, out r)) throw new Exception("GetWindowRect failed");
        int w = Math.Max(1, r.Right - r.Left);
        int h = Math.Max(1, r.Bottom - r.Top);
        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb)) {
            using (var g = Graphics.FromImage(bmp)) {
                g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
            }
            bmp.Save(path, ImageFormat.Png);
        }
    }

    public static void Capture(IntPtr hwnd, string path) {
        RECT r;
        if (!GetWindowRect(hwnd, out r)) throw new Exception("GetWindowRect failed");
        int w = Math.Max(1, r.Right - r.Left);
        int h = Math.Max(1, r.Bottom - r.Top);
        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb)) {
            using (var g = Graphics.FromImage(bmp)) {
                IntPtr hdc = g.GetHdc();
                bool ok = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
                g.ReleaseHdc(hdc);
                if (!ok) {
                    g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                }
            }
            bmp.Save(path, ImageFormat.Png);
        }
    }
}
"@
Add-Type -TypeDefinition $src -ReferencedAssemblies System.Drawing

function Write-Log([string]$Message) {
    Write-Host "[screenshots] $Message"
}

function Wait-AppWindow([System.Diagnostics.Process]$Proc, [int]$TimeoutSec) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        $Proc.Refresh()
        if ($Proc.MainWindowHandle -ne [IntPtr]::Zero) { return $Proc.MainWindowHandle }
        $other = Get-Process -Name "PerfMonNext" -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } | Select-Object -First 1
        if ($other) { return $other.MainWindowHandle }
        Start-Sleep -Milliseconds 400
    } while ((Get-Date) -lt $deadline)
    throw "Timed out waiting for the PerfMon Next window."
}

function Get-AutomationWindow([IntPtr]$Hwnd) {
    return [System.Windows.Automation.AutomationElement]::FromHandle($Hwnd)
}

function Select-ListItem([System.Windows.Automation.AutomationElement]$Root, [string]$Name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition (
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $item = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if (-not $item) { return $false }
    $item.SetFocus()
    try {
        $sel = $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $sel.Select()
    } catch {
        [System.Windows.Forms.SendKeys]::SendWait("{SPACE}")
    }
    return $true
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputDir = (Resolve-Path $OutputDir).Path
$logPath = Join-Path $OutputDir "capture-log.txt"
$ExePath = (Resolve-Path $ExePath).Path
Write-Log "Starting $ExePath"

try {
    foreach ($size in @(@(1600, 1000), @(1680, 1050), @(1600, 1200), @(1920, 1080), @(1600, 900))) {
        $msg = [NativeShot]::TryResolution($size[0], $size[1])
        Write-Log "Display: $msg"
        if ($msg -match "= 0$") { break }
    }
} catch { Write-Log "Display resize failed: $_" }

$proc = Start-Process -FilePath $ExePath -PassThru -WorkingDirectory (Split-Path $ExePath)
try {
    $hwnd = Wait-AppWindow -Proc $proc -TimeoutSec 90
    Write-Log "Window handle $hwnd"
    [NativeShot]::ShowWindow($hwnd, 9) | Out-Null
    [NativeShot]::SetWindowPos($hwnd, [IntPtr]::Zero, 0, 0, 1440, 920, 0x0044) | Out-Null
    Start-Sleep -Milliseconds 500
    [NativeShot]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 400

    # Default workspace already has Processor / Memory / Queue on this computer.
    [System.Windows.Forms.SendKeys]::SendWait("{F5}")
    Write-Log "Sent F5 to start sampling"
    Start-Sleep -Seconds 8

    $chartPath = Join-Path $OutputDir "main-window-live-chart.png"
    [NativeShot]::Capture($hwnd, $chartPath)
    Write-Log "Wrote $chartPath"

    $pickerOk = $false
    try {
        $root = Get-AutomationWindow $hwnd
        foreach ($name in @("Processor", "Memory", "System")) {
            if (Select-ListItem -Root $root -Name $name) {
                Write-Log "Selected catalog set '$name'"
                $pickerOk = $true
                break
            }
        }
        if (-not $pickerOk) {
            Write-Log "Could not select a counter set via UI Automation; capturing the window as-is."
        }
        Start-Sleep -Milliseconds 800
        [NativeShot]::SetForegroundWindow($hwnd) | Out-Null
        Start-Sleep -Milliseconds 200
        $pickerPath = Join-Path $OutputDir "counter-picker.png"
        [NativeShot]::Capture($hwnd, $pickerPath)
        Write-Log "Wrote $pickerPath"
    } catch {
        Write-Log "Picker automation failed: $_"
        $pickerPath = Join-Path $OutputDir "counter-picker.png"
        if (-not (Test-Path $pickerPath)) {
            [NativeShot]::Capture($hwnd, $pickerPath)
            Write-Log "Wrote fallback $pickerPath"
        }
    }

    # Series bulk actions: select every row, open the right-click menu, capture from the screen
    # so the popup menu is included.
    try {
        $root = Get-AutomationWindow $hwnd
        $gridCond = New-Object System.Windows.Automation.PropertyCondition (
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "SeriesGrid")
        $grid = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $gridCond)
        if ($grid) {
            [NativeShot]::SetForegroundWindow($hwnd) | Out-Null
            $grid.SetFocus()
            Start-Sleep -Milliseconds 300
            [System.Windows.Forms.SendKeys]::SendWait("^a")
            Start-Sleep -Milliseconds 300
            [System.Windows.Forms.SendKeys]::SendWait("+{F10}")
            Start-Sleep -Milliseconds 900
            $menuPath = Join-Path $OutputDir "series-bulk-actions.png"
            [NativeShot]::CaptureScreen($hwnd, $menuPath)
            Write-Log "Wrote $menuPath"
            [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
        } else {
            Write-Log "Series grid not found via UI Automation; skipping bulk-actions shot."
        }
    } catch {
        Write-Log "Series menu automation failed: $_"
    }
} finally {
    try {
        if ($proc -and -not $proc.HasExited) { $proc.CloseMainWindow() | Out-Null }
        Start-Sleep -Milliseconds 500
        Get-Process -Name "PerfMonNext" -ErrorAction SilentlyContinue | Stop-Process -Force
    } catch { }
}

$files = Get-ChildItem $OutputDir -Filter *.png
$files | ForEach-Object { Write-Log "artifact $($_.Name) $($_.Length) bytes" }
if ($files.Count -eq 0) { throw "No screenshots were captured." }
$files | ForEach-Object { $_.FullName } | Out-File -FilePath $logPath -Encoding utf8
Write-Log "Done."
