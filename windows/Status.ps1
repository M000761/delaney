# Show the current kidblock state on the router.
. "$PSScriptRoot\config.ps1"
if (-not (Test-KbConfig)) { exit 1 }

$result = Invoke-RouterCmd -Subcommand 'status'
Show-Result -Title 'kidblock - Status' -Result $result
