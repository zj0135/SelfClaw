$pluginRoot = if ($env:SELFCLAW_PLUGIN_ROOT) { $env:SELFCLAW_PLUGIN_ROOT } else { Split-Path -Parent $PSScriptRoot }
. (Join-Path -Path $pluginRoot -ChildPath 'lib/common.ps1')

$payload = Read-HookPayload

# Synchronous toolExecuted: the feedback is persisted and replayed to the model.
if ($payload.status -ne 'completed') {
    $detail = if ($payload.error) { $payload.error } elseif ($payload.summary) { $payload.summary } else { 'no detail' }
    Write-HookDecision @{ feedback = "hook-all-events: $($payload.toolName) finished as $($payload.status) ($detail)." }
}

exit 0
