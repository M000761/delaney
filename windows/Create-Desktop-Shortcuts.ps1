# Create-Desktop-Shortcuts.ps1
#   Creates .lnk shortcuts on the Desktop for each kidblock action.

$desktop = 'C:\Users\dr_go\OneDrive\Desktop'
$scriptDir = $PSScriptRoot
$ws = New-Object -ComObject WScript.Shell

# Subfolder on the Desktop so we don't litter it
$folder = Join-Path $desktop 'KidBlock'
New-Item -ItemType Directory -Path $folder -Force | Out-Null

$shortcuts = @(
    @{ Name = 'KidBlock - Status';          Script = 'Status.ps1';          Icon = 'shell32.dll,277' }
    @{ Name = 'KidBlock - Override Allow';  Script = 'Override-Allow.ps1';  Icon = 'shell32.dll,17'  }
    @{ Name = 'KidBlock - Override Block';  Script = 'Override-Block.ps1';  Icon = 'shell32.dll,131' }
    @{ Name = 'KidBlock - Block Now';       Script = 'Block-Now.ps1';       Icon = 'shell32.dll,109' }
    @{ Name = 'KidBlock - Allow Now';       Script = 'Allow-Now.ps1';       Icon = 'shell32.dll,247' }
    @{ Name = 'KidBlock - Clear Override';  Script = 'Clear-Override.ps1';  Icon = 'shell32.dll,238' }
    @{ Name = 'KidBlock - Edit Devices';    Script = 'Edit-Devices.ps1';    Icon = 'shell32.dll,269' }
    @{ Name = 'KidBlock - Edit Schedule';   Script = 'Edit-Schedule.ps1';   Icon = 'shell32.dll,239' }
)

foreach ($s in $shortcuts) {
    $lnkPath = Join-Path $folder "$($s.Name).lnk"
    $target  = Join-Path $scriptDir $s.Script
    if (-not (Test-Path $target)) {
        Write-Host "  SKIP: $($s.Script) not found in $scriptDir" -ForegroundColor Yellow
        continue
    }
    $lnk = $ws.CreateShortcut($lnkPath)
    $lnk.TargetPath       = 'powershell.exe'
    $lnk.Arguments        = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$target`""
    $lnk.WorkingDirectory = $scriptDir
    $lnk.IconLocation     = $s.Icon
    $lnk.Description      = $s.Name
    $lnk.Save()
    Write-Host "  Created: $lnkPath"
}

Write-Host ""
Write-Host "Done. Open the 'KidBlock' folder on your Desktop." -ForegroundColor Green
