# Setup-SSH-Key.ps1
#   One-time helper to set up passwordless SSH from this notebook to the
#   EdgeRouter. After this runs successfully, every other Windows script in
#   this folder will work without prompting for a password.
#
# What it does:
#   1. Generates an ed25519 SSH keypair if one doesn't already exist.
#   2. Copies the PUBLIC key to your clipboard and prints it.
#   3. Walks you through pasting the right EdgeOS commands.

. "$PSScriptRoot\config.ps1"

# Step 1: generate key if missing
$keyDir = Split-Path $script:KB_SSHKeyPath -Parent
if (-not (Test-Path $keyDir)) {
    New-Item -ItemType Directory -Path $keyDir -Force | Out-Null
}

if (-not (Test-Path $script:KB_SSHKeyPath)) {
    Write-Host "Generating new ed25519 SSH key at $script:KB_SSHKeyPath..." -ForegroundColor Cyan
    & ssh-keygen -t ed25519 -f $script:KB_SSHKeyPath -N '""' -C "kidblock@$env:COMPUTERNAME"
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ssh-keygen failed. Is OpenSSH installed? (Settings -> Apps -> Optional features -> OpenSSH Client)" -ForegroundColor Red
        exit 1
    }
} else {
    Write-Host "Reusing existing key at $script:KB_SSHKeyPath" -ForegroundColor Yellow
}

# Step 2: load pubkey
$pubKeyPath = "$script:KB_SSHKeyPath.pub"
if (-not (Test-Path $pubKeyPath)) {
    Write-Host "ERROR: Public key not found at $pubKeyPath" -ForegroundColor Red
    exit 1
}
$pubKey = (Get-Content -Raw $pubKeyPath).Trim()

# Parse "<type> <base64> <comment>"
$parts = $pubKey -split '\s+', 3
if ($parts.Length -lt 2) {
    Write-Host "ERROR: Public key file looks malformed: $pubKey" -ForegroundColor Red
    exit 1
}
$keyType = $parts[0]   # ssh-ed25519
$keyData = $parts[1]   # AAAAC3...
$keyName = "kidblock-$env:COMPUTERNAME".ToLower() -replace '[^a-z0-9-]','-'

# Copy pubkey to clipboard
Set-Clipboard -Value $pubKey

# Build the EdgeOS configuration block
$edgeosBlock = @"
configure
set system login user $($script:KB_RouterUser) authentication public-keys $keyName type $keyType
set system login user $($script:KB_RouterUser) authentication public-keys $keyName key $keyData
commit
save
exit
"@

Write-Host ""
Write-Host "=== SSH KEY SETUP ===" -ForegroundColor Green
Write-Host ""
Write-Host "Your PUBLIC key (already copied to clipboard):" -ForegroundColor Cyan
Write-Host $pubKey
Write-Host ""
Write-Host "Now do this:" -ForegroundColor Cyan
Write-Host "  1. Open a NEW PowerShell or Command Prompt window."
Write-Host "  2. Run:    ssh $($script:KB_RouterUser)@$($script:KB_RouterHost)"
Write-Host "     (it will ask for the password since the key isn't installed yet)"
Write-Host "  3. Paste the block below at the router prompt, exactly as shown,"
Write-Host "     then press Enter after 'exit'."
Write-Host ""
Write-Host "----- PASTE THIS INTO THE ROUTER -----" -ForegroundColor Yellow
Write-Host $edgeosBlock
Write-Host "----- END -----" -ForegroundColor Yellow
Write-Host ""

# Also save the block to a file in case clipboard is overwritten
$blockFile = Join-Path $env:TEMP "kidblock-edgeos-key-install.txt"
$edgeosBlock | Out-File -FilePath $blockFile -Encoding ASCII
Write-Host "(A copy was also saved to: $blockFile)" -ForegroundColor DarkGray
Write-Host ""

Read-Host "After you've pasted those commands on the router and seen 'commit' + 'save' succeed, press Enter here to test the key"

# Step 3: test
Write-Host ""
Write-Host "Testing key-based login..." -ForegroundColor Cyan
& ssh -i $script:KB_SSHKeyPath `
      -o StrictHostKeyChecking=accept-new `
      -o BatchMode=yes `
      -o ConnectTimeout=8 `
      "$($script:KB_RouterUser)@$($script:KB_RouterHost)" `
      'echo ok && uname -a'

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "SUCCESS: passwordless SSH is working." -ForegroundColor Green
    Write-Host "You can now use all the other scripts (Block-Now, Override-Allow, etc.)."
} else {
    Write-Host ""
    Write-Host "FAILED: key-based login did not work." -ForegroundColor Red
    Write-Host "Common causes:"
    Write-Host "  - The 'set system login user ... public-keys' commands weren't committed."
    Write-Host "  - You're connected to a different network / wrong router IP ($script:KB_RouterHost)."
    Write-Host "  - The username '$($script:KB_RouterUser)' is wrong."
    Write-Host "Re-run this script to retry."
    exit 1
}
