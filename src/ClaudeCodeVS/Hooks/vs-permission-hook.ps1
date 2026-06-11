# Single-gate PreToolUse hook for Edit/Write/MultiEdit (auto-installed by the Claude Code VS extension).
# Reconstructs the proposed file content, posts it to the VS bridge's /permission endpoint, and lets
# the native VS diff (Accept/Reject) be the SOLE gate. Fail-open: any error -> allow (never block the CLI).
# Output contract: exit 0 + one JSON line on stdout with the PreToolUse permission decision.

$ErrorActionPreference = 'Stop'

function Emit([string]$decision, [string]$reason) {
    @{ hookSpecificOutput = @{ hookEventName = 'PreToolUse'; permissionDecision = $decision; permissionDecisionReason = $reason } } |
        ConvertTo-Json -Compress -Depth 8
    exit 0
}

function ApplyEdit([string]$content, [string]$old, [string]$new, [bool]$all) {
    if ([string]::IsNullOrEmpty($old)) { return $content }
    # Match the file's newline convention so the diff isn't a sea of line-ending changes.
    if ($content -match "`r`n") {
        $old = $old -replace "`n", "`r`n"
        $new = $new -replace "`n", "`r`n"
    }
    if ($all) { return $content.Replace($old, $new) }
    $idx = $content.IndexOf($old)
    if ($idx -lt 0) { return $content } # not found (e.g. newline mismatch) -> leave unchanged
    return $content.Substring(0, $idx) + $new + $content.Substring($idx + $old.Length)
}

try {
    # Read stdin as UTF-8: the default console input encoding garbles non-ASCII (em-dashes, smart
    # quotes) in the hook payload, which then fails to match the file content during reconstruction.
    $stdin = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), [System.Text.Encoding]::UTF8)
    $payload = $stdin.ReadToEnd()
    $p = $payload | ConvertFrom-Json
    $tool = $p.tool_name
    $ti = $p.tool_input
    $file = $ti.file_path

    # Read as UTF-8 (PS 5.1 Get-Content defaults to the ANSI codepage, which garbles em-dashes etc.).
    $cur = if ($file -and (Test-Path -LiteralPath $file)) { Get-Content -Raw -LiteralPath $file -Encoding UTF8 } else { '' }
    switch ($tool) {
        'Write'     { $new = [string]$ti.content }
        'Edit'      { $new = ApplyEdit $cur $ti.old_string $ti.new_string ([bool]$ti.replace_all) }
        'MultiEdit' { $new = $cur; foreach ($e in $ti.edits) { $new = ApplyEdit $new $e.old_string $e.new_string ([bool]$e.replace_all) } }
        default     { Emit 'allow' "unhandled tool $tool" }
    }

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
    if (-not $port) { Emit 'allow' 'no Visual Studio bridge lockfile found' }

    $body = @{ filePath = $file; newContents = $new } | ConvertTo-Json -Compress -Depth 8
    # Send the body as explicit UTF-8 bytes; Invoke-RestMethod's default string encoding mangles
    # non-ASCII content (em-dashes, smart quotes) into invalid JSON.
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($body)
    $resp = Invoke-RestMethod -Uri "http://127.0.0.1:$port/permission" -Method Post `
        -ContentType 'application/json; charset=utf-8' `
        -Headers @{ 'x-claude-code-ide-authorization' = $token } `
        -Body $bytes -TimeoutSec 86400

    if ($resp.allow) {
        Emit 'allow' 'Accepted in Visual Studio diff'
    }
    else {
        $why = if ($resp.reason) { [string]$resp.reason } else { 'Rejected in Visual Studio diff' }
        Emit 'deny' $why
    }
}
catch {
    Emit 'allow' ("hook error (allowing): " + $_.Exception.Message)
}
