# Open the block-schedule file on the router in Notepad, save back, reapply.
. "$PSScriptRoot\config.ps1"
if (-not (Test-KbConfig)) { exit 1 }

$tempFile = Join-Path $env:TEMP "kidblock-schedule-$([guid]::NewGuid()).conf"
$remotePath = '/config/scripts/kidblock-schedule.conf'
$scpTarget = "$($script:KB_RouterUser)@$($script:KB_RouterHost):$remotePath"

Write-Host "Fetching current schedule..."
& scp -i $script:KB_SSHKeyPath -o StrictHostKeyChecking=accept-new $scpTarget $tempFile
if ($LASTEXITCODE -ne 0) {
    [System.Windows.Forms.MessageBox]::Show("Failed to fetch schedule from router.", "kidblock", 'OK', 'Error') | Out-Null
    exit 1
}

$before = (Get-FileHash $tempFile).Hash
Start-Process notepad.exe -ArgumentList $tempFile -Wait
$after = (Get-FileHash $tempFile).Hash

if ($before -eq $after) {
    Remove-Item $tempFile -ErrorAction SilentlyContinue
    [System.Windows.Forms.MessageBox]::Show("No changes were made.", "kidblock - Edit Schedule", 'OK', 'Information') | Out-Null
    exit
}

Write-Host "Uploading new schedule..."
& scp -i $script:KB_SSHKeyPath -o StrictHostKeyChecking=accept-new $tempFile $scpTarget
if ($LASTEXITCODE -ne 0) {
    [System.Windows.Forms.MessageBox]::Show("Failed to upload schedule.", "kidblock", 'OK', 'Error') | Out-Null
    exit 1
}
Remove-Item $tempFile -ErrorAction SilentlyContinue

$result = Invoke-RouterCmd -Subcommand 'reapply'
Show-Result -Title 'kidblock - Schedule updated' -Result $result
