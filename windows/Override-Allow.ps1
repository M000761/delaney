# Allow controlled devices online for N minutes, then revert to the schedule.
. "$PSScriptRoot\config.ps1"
if (-not (Test-KbConfig)) { exit 1 }

$mins = Show-InputDialog `
    -Prompt 'How many minutes should the controlled devices be ALLOWED online?' `
    -Title  'kidblock - Override Allow' `
    -Default '30'

if ([string]::IsNullOrWhiteSpace($mins)) { exit }

$n = 0
if (-not [int]::TryParse($mins, [ref]$n) -or $n -lt 1 -or $n -gt 1440) {
    [System.Windows.Forms.MessageBox]::Show(
        'Please enter a whole number of minutes between 1 and 1440 (24 hours).',
        'kidblock - Invalid input', 'OK', 'Warning'
    ) | Out-Null
    exit 1
}

$result = Invoke-RouterCmd -Subcommand "override-allow $n"
Show-Result -Title 'kidblock - Override Allow' -Result $result
