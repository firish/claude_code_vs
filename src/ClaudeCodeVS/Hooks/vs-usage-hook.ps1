# Stop hook (auto-installed by the Claude Code VS extension). Reports the conversation transcript path
# to the VS bridge's /usage endpoint so the dockable panel can show token/cost stats. Observe-only:
# always exits 0 so the CLI's turn-end is never blocked.
$ErrorActionPreference = 'Stop'
try {
    # Read stdin as UTF-8 (default console input encoding garbles non-ASCII).
    $stdin = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), [System.Text.Encoding]::UTF8)
    $p = $stdin.ReadToEnd() | ConvertFrom-Json
    $transcript = $p.transcript_path
    if (-not $transcript) { exit 0 }

    # Find the Visual Studio bridge lockfile (prefer one whose workspace contains this cwd).
    $ideDir = Join-Path $env:USERPROFILE '.claude\ide'
    $port = $null; $token = $null
    foreach ($f in Get-ChildItem $ideDir -Filter *.lock -ErrorAction SilentlyContinue) {
        try {
            $j = Get-Content -Raw $f.FullName | ConvertFrom-Json
            if ($j.ideName -eq 'Visual Studio') {
                $port = [int]$f.BaseName; $token = $j.authToken
                if ($j.workspaceFolders -and $p.cwd -and ($p.cwd -like ($j.workspaceFolders[0] + '*'))) { break }
            }
        } catch { }
    }
    if (-not $port) { exit 0 }

    $body = @{ transcript_path = $transcript } | ConvertTo-Json -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($body)
    Invoke-RestMethod -Uri "http://127.0.0.1:$port/usage" -Method Post `
        -ContentType 'application/json; charset=utf-8' `
        -Headers @{ 'x-claude-code-ide-authorization' = $token } `
        -Body $bytes -TimeoutSec 5 | Out-Null
} catch { }
exit 0
