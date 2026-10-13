# Claude Code PostToolUse hook: compiler diagnostics of the .cs file that was just edited, from a running Balalaika
# server, returned as additional context. The hook event arrives on stdin as JSON; the edited path is
# tool_input.file_path. Prints nothing (and exits 0) when the file is not C#, is clean, is unknown to the server,
# or the server is not running: an edit is never blocked or slowed by a missing server.
#
# Register in .claude/settings.json (project) or ~/.claude/settings.json (user):
#   "hooks": { "PostToolUse": [ { "matcher": "Edit|Write|MultiEdit", "hooks": [ { "type": "command",
#     "command": "pwsh", "args": [ "-NoProfile", "-NonInteractive", "-File", "<path>/post-edit-diagnostics.ps1" ],
#     "timeout": 45 } ] } ] }
param(
    [string]$Url = $(if ($env:BALALAIKA_URL) { $env:BALALAIKA_URL } else { "http://localhost:5301/mcp" }),
    [string]$MinSeverity = "error",
    [int]$Limit = 30
)

$ErrorActionPreference = "Stop"
try {
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }
    $hookEvent = $raw | ConvertFrom-Json
    $path = [string]$hookEvent.tool_input.file_path
    if (-not $path -or [IO.Path]::GetExtension($path) -ne ".cs") { exit 0 }
    if (-not [IO.Path]::IsPathRooted($path) -and $hookEvent.cwd) { $path = Join-Path $hookEvent.cwd $path }
    $path = [IO.Path]::GetFullPath($path)

    $arguments = @{ target = $path; minSeverity = $MinSeverity; limit = $Limit }
    $payload = @{ jsonrpc = "2.0"; id = 1; method = "tools/call"; params = @{ name = "get_diagnostics"; arguments = $arguments } } | ConvertTo-Json -Depth 6 -Compress
    $response = Invoke-WebRequest -Uri $Url -Method Post -Body $payload -ContentType "application/json" -Headers @{ Accept = "application/json, text/event-stream" } -UseBasicParsing -TimeoutSec 30
    $text = $response.Content
    if ($response.Headers["Content-Type"] -like "text/event-stream*") {
        $text = ($text -split "`n" | Where-Object { $_ -like "data:*" } | ForEach-Object { $_.Substring(5).Trim() }) -join ""
    }

    $json = $text | ConvertFrom-Json
    if ($json.error -or $json.result.isError) { exit 0 }
    $result = (($json.result.content | ForEach-Object { $_.text }) -join "`n").TrimEnd()

    # A real answer starts "Diagnostics for <path>: N errors, M warnings"; anything else ("still loading",
    # "No known solution contains", "is not part of") is not worth the agent's attention after an edit.
    if ($result -notmatch '^Diagnostics for .*: (\d+) errors, (\d+) warnings') { exit 0 }
    if ([int]$Matches[1] -eq 0 -and [int]$Matches[2] -eq 0) { exit 0 }

    $context = "Balalaika get_diagnostics on the file just edited (compiler only):`n$result"
    @{ hookSpecificOutput = @{ hookEventName = "PostToolUse"; additionalContext = $context } } | ConvertTo-Json -Depth 4 -Compress
    exit 0
}
catch {
    exit 0
}
