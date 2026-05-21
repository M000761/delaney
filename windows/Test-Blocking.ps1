# Test-Blocking.ps1 - Verify kidblock domain blocking is working.
# Run this ON THE BLOCKED DESKTOP (via RDP).
# For each domain it does a DNS lookup + a TCP 443 connect with a short timeout.
# DNS resolving but TCP failing = block is working correctly.

$blockedDomains = @(
    # YouTube
    'youtube.com','youtu.be','youtubei.googleapis.com','youtube-nocookie.com','ytimg.com','googlevideo.com',
    # TikTok
    'tiktok.com','tiktokcdn.com','tiktokv.com','musical.ly','byteoversea.com','bytedance.com',
    # Twitch
    'twitch.tv','ttvnw.net','jtvnw.net',
    # Discord
    'discord.com','discord.gg','discordapp.com','discordapp.net','discord.media',
    # Instagram, Snapchat, FB, Reddit, Roblox
    'instagram.com','cdninstagram.com',
    'snapchat.com','sc-cdn.net','snap-dev.net',
    'facebook.com','fbcdn.net','messenger.com',
    'reddit.com','redd.it','redditmedia.com','redditstatic.com',
    'roblox.com','rbxcdn.com',
    # Anonymous chat
    'omegle.com','emeraldchat.com','chatroulette.com','ome.tv','chathub.cam',
    # Image boards
    '4chan.org','4channel.org','4cdn.org','8kun.top','kiwifarms.net',
    # VPN / proxy
    'nordvpn.com','expressvpn.com','protonvpn.com','proton.me','mullvad.net',
    'windscribe.com','hide.me','tunnelbear.com','surfshark.com','purevpn.com',
    'cyberghostvpn.com','ivpn.net','torproject.org','psiphon.ca','hidemyass.com',
    # Adult
    'pornhub.com','xvideos.com','xnxx.com','xhamster.com','redtube.com',
    'youporn.com','spankbang.com','chaturbate.com','onlyfans.com','stripchat.com','tube8.com',
    # Gambling
    'bet365.com','draftkings.com','fanduel.com','pokerstars.com','bovada.lv','betway.com','unibet.com'
)

# These should NOT be blocked - they verify the test itself works.
$positiveControls = @('example.com','wikipedia.org','microsoft.com','cloudflare.com')

function Test-Domain {
    param([string]$Domain, [int]$TimeoutMs = 2500)

    $ip = $null
    try {
        $r = Resolve-DnsName -Name $Domain -Type A -DnsOnly -ErrorAction Stop |
             Where-Object { $_.QueryType -eq 'A' } |
             Select-Object -First 1
        if ($r) { $ip = $r.IPAddress }
    } catch {
        return [PSCustomObject]@{ Domain=$Domain; IP='(DNS fail)'; TCP='-'; Result='DNS-FAIL' }
    }
    if (-not $ip) {
        return [PSCustomObject]@{ Domain=$Domain; IP='(no A record)'; TCP='-'; Result='NO-IP' }
    }

    $tcp = New-Object System.Net.Sockets.TcpClient
    $connected = $false
    try {
        $task = $tcp.ConnectAsync($ip, 443)
        $connected = $task.Wait($TimeoutMs) -and $task.IsCompleted -and -not $task.IsFaulted -and $tcp.Connected
    } catch {
        $connected = $false
    } finally {
        $tcp.Close()
    }

    [PSCustomObject]@{
        Domain  = $Domain
        IP      = $ip
        TCP     = if ($connected) { 'open' }    else { 'blocked' }
        Result  = if ($connected) { 'LEAKING!' } else { 'OK (blocked)' }
    }
}

# Show what DNS the device is using
Write-Host ""
Write-Host "=== DNS servers in use ===" -ForegroundColor Cyan
(Get-DnsClientServerAddress -AddressFamily IPv4 |
    Where-Object { $_.ServerAddresses -and $_.InterfaceAlias -notmatch 'Loopback|isatap' }) |
    Format-Table InterfaceAlias, ServerAddresses -AutoSize

Write-Host ""
Write-Host "=== Testing $($blockedDomains.Count) blocked domains ===" -ForegroundColor Cyan
Write-Host "(this takes ~30-60 seconds)"
Write-Host ""
$blockedResults = $blockedDomains | ForEach-Object { Test-Domain $_ }
$blockedResults | Format-Table -AutoSize

Write-Host ""
Write-Host "=== Positive controls (should all be 'LEAKING' = expected open) ===" -ForegroundColor Cyan
$controlResults = $positiveControls | ForEach-Object { Test-Domain $_ }
$controlResults | Format-Table -AutoSize

$blockedOK   = ($blockedResults | Where-Object Result -eq 'OK (blocked)').Count
$leaks       = $blockedResults | Where-Object Result -eq 'LEAKING!'
$dnsFails    = $blockedResults | Where-Object Result -eq 'DNS-FAIL'
$controlsOK  = ($controlResults | Where-Object Result -eq 'LEAKING!').Count

Write-Host ""
Write-Host "=== Summary ===" -ForegroundColor Cyan
Write-Host "Blocked correctly        : $blockedOK / $($blockedDomains.Count)"
Write-Host "DNS lookup failures      : $($dnsFails.Count)"
Write-Host "Leaked (reachable)       : $($leaks.Count)"
Write-Host "Controls reachable       : $controlsOK / $($positiveControls.Count) (expect $($positiveControls.Count))"
Write-Host ""

if ($leaks.Count -eq 0 -and $controlsOK -eq $positiveControls.Count -and $dnsFails.Count -eq 0) {
    Write-Host "[OK] All blocks working correctly." -ForegroundColor Green
} else {
    if ($leaks.Count -gt 0) {
        Write-Host "[LEAKS] The following blocked domains are still reachable:" -ForegroundColor Red
        $leaks | ForEach-Object { Write-Host ("   {0,-30}  via IP {1}" -f $_.Domain, $_.IP) -ForegroundColor Red }
        Write-Host ""
        Write-Host "Likely cause: browser DNS-over-HTTPS (DoH) is bypassing the router's DNS." -ForegroundColor Yellow
        Write-Host "Fix in Chrome  : chrome://settings/security  -> Use secure DNS -> Off"           -ForegroundColor Yellow
        Write-Host "Fix in Edge    : edge://settings/privacy     -> Use secure DNS -> Off"           -ForegroundColor Yellow
        Write-Host "Fix in Firefox : about:preferences#privacy   -> DNS over HTTPS -> Off"            -ForegroundColor Yellow
    }
    if ($controlsOK -ne $positiveControls.Count) {
        Write-Host "[WARN] Some positive controls failed - your internet may be disrupted." -ForegroundColor Yellow
    }
    if ($dnsFails.Count -gt 0) {
        Write-Host "[INFO] $($dnsFails.Count) domains failed DNS - likely they don't have an A record or DNS itself is broken." -ForegroundColor DarkYellow
    }
}

Write-Host ""
Read-Host "Press Enter to exit"
