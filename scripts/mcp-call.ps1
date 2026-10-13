param(
    [Parameter(Mandatory = $true)][string]$Tool,
    [hashtable]$Arguments = @{},
    [string]$Url = "http://localhost:5301/mcp"
)

# Calls one MCP tool over streamable HTTP and prints the text content. Handles both JSON and SSE responses.
$payload = @{ jsonrpc = "2.0"; id = 1; method = "tools/call"; params = @{ name = $Tool; arguments = $Arguments } } | ConvertTo-Json -Depth 6 -Compress
$response = Invoke-WebRequest -Uri $Url -Method Post -Body $payload -ContentType "application/json" -Headers @{ Accept = "application/json, text/event-stream" } -UseBasicParsing
$text = $response.Content
if ($response.Headers["Content-Type"] -like "text/event-stream*") {
    $text = ($text -split "`n" | Where-Object { $_ -like "data:*" } | ForEach-Object { $_.Substring(5).Trim() }) -join ""
}

$json = $text | ConvertFrom-Json
if ($json.error) {
    Write-Output "ERROR: $($json.error.message)"
    exit 1
}

if ($json.result.isError) {
    Write-Output "TOOL ERROR:"
}

$json.result.content | ForEach-Object { $_.text }
