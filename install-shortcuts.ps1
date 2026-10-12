param([ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$executable = Join-Path $PSScriptRoot "dist\$Runtime\PC Companion.exe"
if (-not (Test-Path -LiteralPath $executable)) { throw 'Build the portable app first using build.ps1.' }
$shell = New-Object -ComObject WScript.Shell
$desktop = [Environment]::GetFolderPath('DesktopDirectory')
$programs = [Environment]::GetFolderPath('Programs')
foreach ($folder in @($desktop, $programs)) {
    $shortcut = $shell.CreateShortcut((Join-Path $folder 'PC Companion.lnk'))
    $shortcut.TargetPath = $executable
    $shortcut.WorkingDirectory = Split-Path -Parent $executable
    $shortcut.Description = 'Computer details and temporary-file cleanup'
    $shortcut.Save()
}
Write-Host 'PC Companion shortcuts created on your desktop and in the Start menu.'
