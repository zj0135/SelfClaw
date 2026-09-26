$pluginRoot = if ($env:SELFCLAW_PLUGIN_ROOT) { $env:SELFCLAW_PLUGIN_ROOT } else { Split-Path -Parent $PSScriptRoot }
. (Join-Path -Path $pluginRoot -ChildPath 'lib/common.ps1')

$payload = Read-HookPayload

# Asynchronous toolExecuted: runs on the host executor, stdout is ignored.
Add-HookLog 'tools.jsonl' ([ordered]@{
    type       = 'executed'
    turnId     = $payload.turnId
    callId     = $payload.callId
    toolName   = $payload.toolName
    status     = $payload.status
    deniedBy   = $payload.deniedBy
    durationMs = $payload.durationMs
    summary    = $payload.summary
}) | Out-Null

exit 0
