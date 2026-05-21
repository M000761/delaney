# kidblock - central config for all Windows-side scripts.
# Edit the four values below to match your setup.

$script:KB_RouterHost  = '192.168.200.1'
$script:KB_RouterUser  = 'ubnt'
$script:KB_SSHKeyPath  = Join-Path $HOME '.ssh\kidblock_ed25519'
$script:KB_ScriptPath  = '/config/scripts/kidblock.sh'

# --- shared helpers (sourced by every other script) ---

function Test-KbConfig {
    if (-not (Test-Path $script:KB_SSHKeyPath)) {
        $msg = @"
SSH key not found at:
  $script:KB_SSHKeyPath

Run Setup-SSH-Key.ps1 once to generate it and install it on the router.
"@
        [System.Windows.Forms.MessageBox]::Show($msg, 'kidblock - setup needed', 'OK', 'Warning') | Out-Null
        return $false
    }
    return $true
}

function Invoke-RouterCmd {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Subcommand
    )
    $sshArgs = @(
        '-i', $script:KB_SSHKeyPath
        '-o', 'StrictHostKeyChecking=accept-new'
        '-o', 'ConnectTimeout=8'
        '-o', 'BatchMode=yes'
        "$($script:KB_RouterUser)@$($script:KB_RouterHost)"
        "sudo $script:KB_ScriptPath $Subcommand"
    )
    # Capture both stdout and stderr; surface stderr only on failure.
    $out = & ssh @sshArgs 2>&1
    return @{
        ExitCode = $LASTEXITCODE
        Output   = ($out | Out-String)
    }
}

function Show-Result {
    param(
        [string] $Title,
        [hashtable] $Result
    )
    Add-Type -AssemblyName System.Windows.Forms | Out-Null
    if ($Result.ExitCode -ne 0) {
        $msg = "Router command failed (exit $($Result.ExitCode)):`n`n$($Result.Output)"
        [System.Windows.Forms.MessageBox]::Show($msg, "$Title - error", 'OK', 'Error') | Out-Null
    } else {
        [System.Windows.Forms.MessageBox]::Show($Result.Output, $Title, 'OK', 'Information') | Out-Null
    }
}

function Show-InputDialog {
    param(
        [string] $Prompt,
        [string] $Title,
        [string] $Default = ''
    )
    Add-Type -AssemblyName Microsoft.VisualBasic | Out-Null
    return [Microsoft.VisualBasic.Interaction]::InputBox($Prompt, $Title, $Default)
}

# Load WinForms for any caller that uses message boxes.
Add-Type -AssemblyName System.Windows.Forms | Out-Null
