# Force-block the controlled devices immediately.
# The next 1-minute tick on the router will revert to the schedule.
. "$PSScriptRoot\config.ps1"
if (-not (Test-KbConfig)) { exit 1 }

$confirm = [System.Windows.Forms.MessageBox]::Show(
    "Block all controlled devices NOW?`n`n(This will be reverted to the schedule within 1 minute. Use Override-Block if you want to block for a specific number of minutes.)",
    'kidblock - Block Now',
    'YesNo',
    'Question'
)
if ($confirm -ne 'Yes') { exit }

$result = Invoke-RouterCmd -Subcommand 'block'
Show-Result -Title 'kidblock - Block Now' -Result $result
