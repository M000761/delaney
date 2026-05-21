# Open the MAC list on the router in a temp editor, save back, then reapply.
. "$PSScriptRoot\config.ps1"
if (-not (Test-KbConfig)) { exit 1 }

$tempFile = Join-Path $env:TEMP "kidblock-macs-$([guid]::NewGuid()).conf"
$remotePath = '/config/scripts/kidblock-macs.conf'
$scpTarget = "$($script:KB_RouterUser)@$($script:KB_RouterHost):$remotePath"

# Pull current file
Write-Host "Fetching current MAC list..."
& scp -i $script:KB_SSHKeyPath -o StrictHostKeyChecking=accept-new $scpTarget $tempFile
if ($LASTEXITCODE -ne 0) {
    [System.Windows.Forms.MessageBox]::Show("Failed to fetch MAC list from router.", "kidblock", 'OK', 'Error') | Out-Null
    exit 1
}

# Hash before edit
$before = (Get-FileHash $tempFile).Hash

# Open Notepad and wait for user to close it
Write-Host "Opening Notepad. Save and close when done."
Start-Process notepad.exe -ArgumentList $tempFile -Wait

# Hash after edit
$after = (Get-FileHash $tempFile).Hash

if ($before -eq $after) {
    Remove-Item $tempFile -ErrorAction SilentlyContinue
    [System.Windows.Forms.MessageBox]::Show("No changes were made.", "kidblock - Edit Devices", 'OK', 'Information') | Out-Null
    exit
}

# Push it back
Write-Host "Uploading new MAC list..."
& scp -i $script:KB_SSHKeyPath -o StrictHostKeyChecking=accept-new $tempFile $scpTarget
if ($LASTEXITCODE -ne 0) {
    [System.Windows.Forms.MessageBox]::Show("Failed to upload new MAC list.", "kidblock", 'OK', 'Error') | Out-Null
    exit 1
}
Remove-Item $tempFile -ErrorAction SilentlyContinue

# Force a reapply so changes take effect now (otherwise within 1 minute)
$result = Invoke-RouterCmd -Subcommand 'reapply'
Show-Result -Title 'kidblock - Devices updated' -Result $result
