$pluginRoot = if ($env:SELFCLAW_PLUGIN_ROOT) { $env:SELFCLAW_PLUGIN_ROOT } else { Split-Path -Parent $PSScriptRoot }
. (Join-Path -Path $pluginRoot -ChildPath 'lib/common.ps1')

$payload = Read-HookPayload

Add-HookLog 'runs.jsonl' ([ordered]@{
    type      = 'started'
    turnId    = $payload.turnId
    origin    = $payload.origin
    inherited = $payload.inherited
    agentId   = $payload.agentId
    provider  = $payload.provider
    model     = $payload.model
    toolCount = @($payload.tools).Count
}) | Out-Null

# Decisions are opt-in through sentinels so a normal run is never interrupted.
$prompt = [string]$payload.userPrompt
if ($prompt -match 'HOOK-BLOCK-RUN') {
    Write-HookDecision @{ decision = 'block'; reason = 'hook-all-events: HOOK-BLOCK-RUN sentinel found.' }
    exit 0
}

if ($prompt -match 'HOOK-ADD-CONTEXT') {
    Write-HookDecision @{
        decision          = 'continue'
        additionalContext = 'hook-all-events: after HOOK-ADD-CONTEXT, quote every path and never run recursive deletes.'
    }
}

exit 0
