# Install-Router-Scripts.ps1
#   One-time helper that uploads the router-side files to the EdgeRouter and
#   wires up the boot hook + the 1-minute task-scheduler tick.
#
# Run this ONCE, AFTER Setup-SSH-Key.ps1 has succeeded.

. "$PSScriptRoot\config.ps1"
if (-not (Test-KbConfig)) { exit 1 }

$routerFiles = Join-Path (Split-Path $PSScriptRoot -Parent) 'router'
$required = @(
    'kidblock.sh',
    'kidblock-macs.conf',
    'kidblock-schedule.conf',
    'kidblock-domains.conf',
    'kidblock-init.sh'
)
foreach ($f in $required) {
    if (-not (Test-Path (Join-Path $routerFiles $f))) {
        Write-Host "Missing file: $f in $routerFiles" -ForegroundColor Red
        exit 1
    }
}

$sshTarget = "$($script:KB_RouterUser)@$($script:KB_RouterHost)"
$sshOpts = @(
    '-i', $script:KB_SSHKeyPath
    '-o', 'StrictHostKeyChecking=accept-new'
    '-o', 'BatchMode=yes'
    '-o', 'ConnectTimeout=10'
)

function Run-Ssh {
    param([string]$Cmd)
    & ssh @sshOpts $sshTarget $Cmd
    return $LASTEXITCODE
}

function Run-Scp {
    param([string]$Local, [string]$Remote)
    & scp @sshOpts $Local "${sshTarget}:${Remote}"
    return $LASTEXITCODE
}

Write-Host "Uploading router files to /config/scripts/ ..." -ForegroundColor Cyan
foreach ($f in $required) {
    $local = Join-Path $routerFiles $f
    Write-Host "  $f"
    if ((Run-Scp $local "/config/scripts/$f") -ne 0) {
        Write-Host "scp failed for $f" -ForegroundColor Red
        exit 1
    }
}

Write-Host "Setting permissions and installing boot hook..." -ForegroundColor Cyan
$setupCmds = @'
sudo chmod +x /config/scripts/kidblock.sh /config/scripts/kidblock-init.sh && \
sudo mkdir -p /config/scripts/post-config.d && \
sudo cp /config/scripts/kidblock-init.sh /config/scripts/post-config.d/kidblock-init.sh && \
sudo chmod +x /config/scripts/post-config.d/kidblock-init.sh && \
echo "permissions OK"
'@
if ((Run-Ssh $setupCmds) -ne 0) {
    Write-Host "Setup commands failed." -ForegroundColor Red
    exit 1
}

Write-Host "Configuring task-scheduler tick (every 1 min)..." -ForegroundColor Cyan
# EdgeOS config commands have to go through vbash with the vyatta-cfg shell loaded.
$cfgScript = @'
source /opt/vyatta/etc/functions/script-template
configure
set system task-scheduler task kidblock-tick interval 1m
set system task-scheduler task kidblock-tick executable path /config/scripts/kidblock.sh
set system task-scheduler task kidblock-tick executable arguments reapply
commit
save
exit
'@
$tmpRemote = "/tmp/kidblock-install-$([guid]::NewGuid()).sh"
$tmpLocal  = Join-Path $env:TEMP "kidblock-install.sh"
$cfgScript | Out-File -FilePath $tmpLocal -Encoding ASCII -NoNewline
if ((Run-Scp $tmpLocal $tmpRemote) -ne 0) {
    Write-Host "Failed to upload config script." -ForegroundColor Red
    exit 1
}
if ((Run-Ssh "bash $tmpRemote && rm -f $tmpRemote") -ne 0) {
    Write-Host "task-scheduler configuration failed." -ForegroundColor Red
    exit 1
}
Remove-Item $tmpLocal -ErrorAction SilentlyContinue

Write-Host "Initializing iptables chain and applying current state..." -ForegroundColor Cyan
if ((Run-Ssh 'sudo /config/scripts/kidblock.sh init') -ne 0) {
    Write-Host "kidblock init failed." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Done. Final status:" -ForegroundColor Green
Write-Host ""
Run-Ssh 'sudo /config/scripts/kidblock.sh status' | Out-Null

$installDns = [System.Windows.Forms.MessageBox]::Show(
    "Install per-device DNS blocking now?`n`nThis blocks domains in kidblock-domains.conf (YouTube etc.) ONLY for the MACs in kidblock-macs.conf. Other devices on the network are unaffected.",
    "kidblock - Install per-device domain blocking?",
    'YesNo',
    'Question'
)
if ($installDns -eq 'Yes') {
    Run-Ssh 'sudo /config/scripts/kidblock.sh install-domains'
}

Write-Host ""
Write-Host "Installation complete. Next:" -ForegroundColor Green
Write-Host "  - Run Create-Desktop-Shortcuts.ps1 to put icons on your Desktop."
Write-Host "  - Verify the schedule with Status.ps1."
