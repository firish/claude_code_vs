# vs:auto-managed - the Claude Code VS extension (re)writes this file on Launch/connect so fixes ship automatically. Remove THIS LINE to take ownership and the extension will leave this file alone.
# statusLine command (auto-installed by the Claude Code VS extension). The CLI hands its status-line data
# to this script on stdin; it is the only place the CLI exposes the subscription usage windows
# (rate_limits.five_hour / seven_day / spend_limit) to a process outside it. Two jobs:
#   1. report rate_limits to the VS bridge's /statusline endpoint, so the panel can show them;
#   2. print the status line. If the user already had one, it is recorded in vs-statusline-inner.json
#      next to this file and run here with the same input, so their status line keeps working;
#      otherwise a short usage summary is printed.
# Never fails the CLI: every step is best-effort and the script always exits 0.
$ErrorActionPreference = 'Stop'
$raw = ''
$p = $null
try {
    $stdin = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), [System.Text.Encoding]::UTF8)
    $raw = $stdin.ReadToEnd()
    $p = $raw | ConvertFrom-Json
} catch { }

# ---- 1. report to the bridge ------------------------------------------------------------------------
try {
    if ($p -and $p.rate_limits) {
        $cwd = [string]$p.cwd
        if (-not $cwd -and $p.workspace) { $cwd = [string]$p.workspace.current_dir }

        # Same bridge discovery as the hooks: most-specific workspace match whose port is listening.
        function Test-Port([int]$pt) {
            try {
                $c = New-Object System.Net.Sockets.TcpClient
                $live = $c.BeginConnect('127.0.0.1', $pt, $null, $null).AsyncWaitHandle.WaitOne(300) -and $c.Connected
                $c.Close(); return $live
            } catch { return $false }
        }
        $ideDir = Join-Path $env:USERPROFILE '.claude\ide'
        $cands = @()
        foreach ($f in Get-ChildItem $ideDir -Filter *.lock -ErrorAction SilentlyContinue) {
            try {
                $j = Get-Content -Raw $f.FullName | ConvertFrom-Json
                if ($j.ideName -ne 'Visual Studio') { continue }
                $ws = if ($j.workspaceFolders) { [string]$j.workspaceFolders[0] } else { '' }
                $wsN = ($ws -replace '/', '\').TrimEnd('\'); $cwdN = ($cwd -replace '/', '\').TrimEnd('\')
                $rank = 0
                if ($wsN -and $cwdN) {
                    if     ($cwdN -eq $wsN)             { $rank = 3 }
                    elseif ($cwdN -like ($wsN + '\*'))  { $rank = 2 }
                    elseif ($wsN -like ($cwdN + '\*'))  { $rank = 1 }
                }
                $cands += [pscustomobject]@{ Port = [int]$f.BaseName; Token = $j.authToken; Score = ($rank * 1000000 + $ws.Length) }
            } catch { }
        }
        foreach ($cand in ($cands | Sort-Object Score -Descending)) {
            if (Test-Port $cand.Port) {
                # pid identifies the session for the bridge's ownership gate (it sits under the CLI's pid).
                $body = @{ rate_limits = $p.rate_limits; cwd = $cwd; transcript_path = [string]$p.transcript_path; pid = $PID; entrypoint = [string]$env:CLAUDE_CODE_ENTRYPOINT } | ConvertTo-Json -Compress -Depth 6
                Invoke-RestMethod -Uri "http://127.0.0.1:$($cand.Port)/statusline" -Method Post `
                    -ContentType 'application/json; charset=utf-8' `
                    -Headers @{ 'x-claude-code-ide-authorization' = $cand.Token } `
                    -Body ([System.Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 2 | Out-Null
                break
            }
        }
    }
} catch { }

# ---- 2. print the status line --------------------------------------------------------------------------
# Raw UTF-8 to stdout: PowerShell 5.1's own pipeline would re-encode through the OEM code page.
function Write-Utf8([string]$text) {
    if (-not $text) { return }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
    $out = [Console]::OpenStandardOutput()
    $out.Write($bytes, 0, $bytes.Length); $out.Flush()
}

$printed = $false
try {
    $innerFile = Join-Path $PSScriptRoot 'vs-statusline-inner.json'
    if (Test-Path $innerFile) {
        $inner = (Get-Content -Raw -Encoding UTF8 $innerFile | ConvertFrom-Json).command
        if ($inner) {
            # Run it the way the CLI runs status lines on Windows: through Git Bash when there is one.
            # The command travels in an environment variable, so no quoting of the user's text is needed.
            $bash = $env:CLAUDE_CODE_GIT_BASH_PATH
            if (-not $bash -or -not (Test-Path $bash)) {
                $cand = Join-Path $env:ProgramFiles 'Git\bin\bash.exe'
                $bash = if (Test-Path $cand) { $cand } else { (Get-Command bash.exe -ErrorAction SilentlyContinue).Source }
            }
            $psi = New-Object System.Diagnostics.ProcessStartInfo
            # The 5s limit is enforced INSIDE bash by coreutils `timeout`, which kills the command's whole
            # process group. Killing from Windows can't: MSYS emulates fork/exec, so a child such as
            # `sleep` is parented to an exec stub that is already gone, and taskkill /T never finds it.
            if ($bash) { $psi.FileName = $bash; $psi.Arguments = '-c "timeout -k 1 5 bash -c \"$VS_STATUSLINE_CMD\""' }
            else       { $psi.FileName = $env:ComSpec; $psi.Arguments = '/d /c %VS_STATUSLINE_CMD%' }
            $psi.EnvironmentVariables['VS_STATUSLINE_CMD'] = $inner
            $psi.UseShellExecute = $false
            $psi.RedirectStandardInput = $true
            $psi.RedirectStandardOutput = $true
            $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
            $psi.CreateNoWindow = $true
            $proc = [System.Diagnostics.Process]::Start($psi)
            # Read asynchronously: a synchronous ReadToEnd only returns when the process exits, so a hung
            # status-line command would hang this script too, and the 5s limit below would never apply.
            $reading = $proc.StandardOutput.ReadToEndAsync()
            $inBytes = [System.Text.Encoding]::UTF8.GetBytes($raw)
            $proc.StandardInput.BaseStream.Write($inBytes, 0, $inBytes.Length)
            $proc.StandardInput.Close()
            # A little longer than bash's own `timeout -k 1 5`, so that one normally fires first.
            if ($proc.WaitForExit(7000)) {
                $text = $reading.Result
                if ($text) { Write-Utf8 $text; $printed = $true }
            } else {
                # Last resort (the cmd.exe fallback has no `timeout`): end what Windows can still see.
                & taskkill.exe /T /F /PID $proc.Id 2>$null | Out-Null
            }
        }
    }
} catch { }

if (-not $printed) {
    try {
        $parts = @()
        $rl = if ($p) { $p.rate_limits } else { $null }
        if ($rl) {
            if ($rl.five_hour -and $rl.five_hour.used_percentage -ne $null) { $parts += ('5h ' + [math]::Round([double]$rl.five_hour.used_percentage) + '%') }
            if ($rl.seven_day -and $rl.seven_day.used_percentage -ne $null) { $parts += ('7d ' + [math]::Round([double]$rl.seven_day.used_percentage) + '%') }
            if ($rl.spend_limit -and $rl.spend_limit.used_percentage -ne $null) { $parts += ('spend ' + [math]::Round([double]$rl.spend_limit.used_percentage) + '%') }
        }
        # [char] rather than a literal: PowerShell 5.1 reads a BOM-less script as ANSI.
        if ($parts.Count -gt 0) { Write-Utf8 ($parts -join (' ' + [char]0x00B7 + ' ')) }
    } catch { }
}
exit 0
