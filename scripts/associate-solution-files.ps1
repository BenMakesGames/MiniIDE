<#
.SYNOPSIS
    Associates .sln and .slnx files with MiniIDE.

.DESCRIPTION
    Prompts for the path to MiniIde.exe, accepting a few convenient forms:
        C:\Path\MiniIde.exe   -> taken as-is
        C:\Path               -> \MiniIde.exe appended
        C:\Path\              -> MiniIde.exe appended

    If a MiniIde.exe is found, the resolved path is shown for a Y/N confirmation.
    If none of the candidate paths exist, it says so and exits.

    Associations are written under HKCU (no admin rights required).
#>

$ErrorActionPreference = 'Stop'

$ExeName  = 'MiniIde.exe'
$ProgId   = 'MiniIDE.Solution'
$Extensions = @('.sln', '.slnx')

# Reports, in human-friendly terms, what an extension currently opens with.
# Honours the per-user UserChoice (what double-click actually uses) first,
# then falls back to the classes default. Returns a short description string.
function Get-CurrentHandler {
    param([string] $Extension)

    # UserChoice is what Explorer actually respects for double-click.
    $userChoicePath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\$Extension\UserChoice"
    $handlerProgId  = (Get-ItemProperty -LiteralPath $userChoicePath -Name 'ProgId' -ErrorAction SilentlyContinue).ProgId

    # Fall back to the HKCU classes default for the extension.
    if ([string]::IsNullOrWhiteSpace($handlerProgId)) {
        $extPath = Join-Path 'HKCU:\Software\Classes' $Extension
        $handlerProgId = (Get-ItemProperty -LiteralPath $extPath -Name '(default)' -ErrorAction SilentlyContinue).'(default)'
    }

    if ([string]::IsNullOrWhiteSpace($handlerProgId)) {
        return '(none — Windows will prompt)'
    }

    # Try to resolve the ProgID to its actual open command for a readable label.
    foreach ($hive in 'HKCU:\Software\Classes', 'HKLM:\Software\Classes') {
        $cmdPath = Join-Path (Join-Path $hive $handlerProgId) 'shell\open\command'
        $command = (Get-ItemProperty -LiteralPath $cmdPath -Name '(default)' -ErrorAction SilentlyContinue).'(default)'
        if (-not [string]::IsNullOrWhiteSpace($command)) {
            return "$handlerProgId  ->  $command"
        }
    }

    return $handlerProgId
}

# --- Resolve the exe path from whatever the user pastes ---------------------

$pasted = (Read-Host 'Paste the path to MiniIde.exe (or its folder)').Trim().Trim('"')

if ([string]::IsNullOrWhiteSpace($pasted)) {
    Write-Host 'No path entered. Exiting.' -ForegroundColor Yellow
    exit 1
}

# Build candidate paths for the three accepted forms.
$candidates = [System.Collections.Generic.List[string]]::new()
$candidates.Add($pasted)                                   # C:\Path\MiniIde.exe
$candidates.Add((Join-Path $pasted $ExeName))             # C:\Path (+ \MiniIde.exe) and C:\Path\ (+ MiniIde.exe)

$resolved = $null
foreach ($candidate in $candidates) {
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        # Only accept it if it actually points at the exe we want.
        if ([System.IO.Path]::GetFileName($candidate) -ieq $ExeName) {
            $resolved = (Resolve-Path -LiteralPath $candidate).Path
            break
        }
    }
}

if (-not $resolved) {
    Write-Host "Could not find $ExeName at any of these locations:" -ForegroundColor Red
    foreach ($candidate in $candidates) { Write-Host "  $candidate" -ForegroundColor Red }
    Write-Host 'Nothing was changed. Exiting.' -ForegroundColor Yellow
    exit 1
}

# --- Confirm ----------------------------------------------------------------

Write-Host ''
Write-Host 'Found MiniIDE at:' -ForegroundColor Green
Write-Host "  $resolved"
Write-Host ''
Write-Host 'Current association:' -ForegroundColor Cyan
foreach ($ext in $Extensions) {
    Write-Host ("  {0,-6} {1}" -f $ext, (Get-CurrentHandler $ext))
}
Write-Host ''
$answer = (Read-Host "Associate .sln and .slnx files with this? (Y/N)").Trim()

if ($answer -notmatch '^(y|yes)$') {
    Write-Host 'Cancelled. Nothing was changed.' -ForegroundColor Yellow
    exit 0
}

# --- Register the ProgID and extensions -------------------------------------

$classesRoot = 'HKCU:\Software\Classes'
$progIdPath  = Join-Path $classesRoot $ProgId
$command     = "`"$resolved`" `"%1`""

# ProgID: friendly name, icon (from the exe), and the open command.
New-Item -Path (Join-Path $progIdPath 'shell\open\command') -Force | Out-Null
New-Item -Path (Join-Path $progIdPath 'DefaultIcon') -Force | Out-Null
Set-ItemProperty -Path $progIdPath -Name '(default)' -Value 'Solution File'
Set-ItemProperty -Path (Join-Path $progIdPath 'DefaultIcon') -Name '(default)' -Value "`"$resolved`",0"
Set-ItemProperty -Path (Join-Path $progIdPath 'shell\open\command') -Name '(default)' -Value $command

# Point each extension at the ProgID.
foreach ($ext in $Extensions) {
    $extPath = Join-Path $classesRoot $ext
    New-Item -Path $extPath -Force | Out-Null
    Set-ItemProperty -Path $extPath -Name '(default)' -Value $ProgId
    Write-Host "Associated $ext" -ForegroundColor Green
}

# Ask Explorer to refresh its icon/association cache.
try {
    $sig = '[DllImport("shell32.dll")] public static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);'
    $shell = Add-Type -MemberDefinition $sig -Name 'ShellNotify' -Namespace 'Win32' -PassThru
    $shell::SHChangeNotify(0x08000000, 0x0000, [IntPtr]::Zero, [IntPtr]::Zero)  # SHCNE_ASSOCCHANGED
} catch {
    # Non-fatal: associations are set, cache will refresh on next logon.
}

Write-Host ''
Write-Host 'Done. .sln and .slnx files now open with MiniIDE.' -ForegroundColor Green
Write-Host 'Note: Windows may show a one-time "How do you want to open this?" prompt' -ForegroundColor DarkGray
Write-Host '      the first time you double-click — pick MiniIDE and "Always".' -ForegroundColor DarkGray
