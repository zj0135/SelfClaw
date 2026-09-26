[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

$payload = [Console]::In.ReadToEnd() | ConvertFrom-Json

$entry = [ordered]@{
    turnId         = $payload.turnId
    conversationId = $payload.conversationId
    origin         = $payload.origin
    inherited      = $payload.inherited
    agentId        = $payload.agentId
    status         = $payload.status
    toolCallCount  = $payload.toolCallCount
    durationMs     = $payload.durationMs
    timestampUtc   = $payload.timestampUtc
} | ConvertTo-Json -Compress

$directory = Join-Path $env:LOCALAPPDATA 'SelfClaw\hook-examples'
$path = Join-Path $directory 'audit.jsonl'

$lastError = $null
for ($attempt = 0; $attempt -lt 3; $attempt++) {
    try {
        if (-not (Test-Path -LiteralPath $directory)) {
            New-Item -ItemType Directory -Force -Path $directory | Out-Null
        }

        Add-Content -LiteralPath $path -Value $entry -Encoding utf8
        exit 0
    }
    catch {
        $lastError = $_
        Start-Sleep -Milliseconds 100
    }
}

[Console]::Error.Write("audit-run: failed to append the audit entry: $($lastError.Exception.Message)")
exit 1
