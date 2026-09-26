$pluginRoot = if ($env:SELFCLAW_PLUGIN_ROOT) { $env:SELFCLAW_PLUGIN_ROOT } else { Split-Path -Parent $PSScriptRoot }
. (Join-Path -Path $pluginRoot -ChildPath 'lib/common.ps1')

$payload = Read-HookPayload
$usage = $payload.usage

Add-HookLog 'runs.jsonl' ([ordered]@{
    type            = 'completed'
    turnId          = $payload.turnId
    origin          = $payload.origin
    inherited       = $payload.inherited
    status          = $payload.status
    toolCallCount   = $payload.toolCallCount
    durationMs      = $payload.durationMs
    inputTokens     = $usage.inputTokens
    outputTokens    = $usage.outputTokens
    finalTextLength = ([string]$payload.finalText).Length
    error           = $payload.errorMessage
}) | Out-Null

# runCompleted is observation-only; stdout is ignored by the host.
exit 0
