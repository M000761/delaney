# Cancel any active override, snap back to the schedule.
. "$PSScriptRoot\config.ps1"
if (-not (Test-KbConfig)) { exit 1 }

$result = Invoke-RouterCmd -Subcommand 'clear-override'
Show-Result -Title 'kidblock - Clear Override' -Result $result
