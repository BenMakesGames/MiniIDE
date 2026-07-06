<#
.SYNOPSIS
    Removes MiniIDE's association with .sln and .slnx files.

.DESCRIPTION
    Companion to associate-solution-files.ps1. This does NOT try to restore
    whatever handler was set before MiniIDE — it simply removes MiniIDE as the
    associated app:
        * deletes the MiniIDE.Solution ProgID
        * clears each extension's default value if (and only if) it still points
          at MiniIDE.Solution

    All changes are under HKCU (no admin rights required). After this runs,
    Windows will prompt you to pick a handler the next time you open a solution.
#>

$ErrorActionPreference = 'Stop'

$ProgId     = 'MiniIDE.Solution'
$Extensions = @('.sln', '.slnx')

$classesRoot = 'HKCU:\Software\Classes'
$progIdPath  = Join-Path $classesRoot $ProgId

$removedAnything = $false

# Clear each extension's default value only if it still points at our ProgID,
# so we don't clobber an association someone set afterwards.
foreach ($ext in $Extensions) {
    $extPath = Join-Path $classesRoot $ext
    if (-not (Test-Path -LiteralPath $extPath)) { continue }

    $current = (Get-ItemProperty -LiteralPath $extPath -Name '(default)' -ErrorAction SilentlyContinue).'(default)'
    if ($current -eq $ProgId) {
        Set-ItemProperty -LiteralPath $extPath -Name '(default)' -Value ''
        Write-Host "Cleared $ext (was MiniIDE)" -ForegroundColor Green
        $removedAnything = $true
    } else {
        Write-Host "Left $ext alone (points at '$current', not MiniIDE)" -ForegroundColor DarkGray
    }
}

# Remove the ProgID itself.
if (Test-Path -LiteralPath $progIdPath) {
    Remove-Item -LiteralPath $progIdPath -Recurse -Force
    Write-Host "Removed $ProgId ProgID" -ForegroundColor Green
    $removedAnything = $true
}

# Ask Explorer to refresh its icon/association cache.
try {
    $sig = '[DllImport("shell32.dll")] public static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);'
    $shell = Add-Type -MemberDefinition $sig -Name 'ShellNotify' -Namespace 'Win32' -PassThru
    $shell::SHChangeNotify(0x08000000, 0x0000, [IntPtr]::Zero, [IntPtr]::Zero)  # SHCNE_ASSOCCHANGED
} catch {
    # Non-fatal: changes are done, cache will refresh on next logon.
}

Write-Host ''
if ($removedAnything) {
    Write-Host 'Done. MiniIDE is no longer associated with .sln / .slnx files.' -ForegroundColor Green
    Write-Host 'Windows will ask which app to use the next time you open one.' -ForegroundColor DarkGray
} else {
    Write-Host 'Nothing to do — MiniIDE was not associated with .sln / .slnx.' -ForegroundColor Yellow
}
