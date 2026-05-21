# Force-allow the controlled devices immediately.
# The next 1-minute tick on the router will revert to the schedule.
# To allow for a specific number of minutes, use Override-Allow.ps1 instead.
. "$PSScriptRoot\config.ps1"
if (-not (Test-KbConfig)) { exit 1 }

$confirm = [System.Windows.Forms.MessageBox]::Show(
    "Allow controlled devices online NOW?`n`n(This will be reverted to the schedule within 1 minute. Use Override-Allow for a timed exception.)",
    'kidblock - Allow Now',
    'YesNo',
    'Question'
)
if ($confirm -ne 'Yes') { exit }

$result = Invoke-RouterCmd -Subcommand 'allow'
Show-Result -Title 'kidblock - Allow Now' -Result $result
