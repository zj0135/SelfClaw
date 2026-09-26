$pluginRoot = if ($env:SELFCLAW_PLUGIN_ROOT) { $env:SELFCLAW_PLUGIN_ROOT } else { Split-Path -Parent $PSScriptRoot }
. (Join-Path -Path $pluginRoot -ChildPath 'lib/common.ps1')

$payload = Read-HookPayload

Add-HookLog 'tools.jsonl' ([ordered]@{
    type             = 'executing'
    turnId           = $payload.turnId
    callId           = $payload.callId
    iteration        = $payload.iteration
    toolName         = $payload.toolName
    kind             = $payload.kind
    sourceKind       = $payload.sourceKind
    requiresApproval = $payload.requiresApproval
    permissionMode   = $payload.permissionMode
}) | Out-Null

$command = [string]$payload.arguments.command
if ([string]::IsNullOrWhiteSpace($command)) {
    exit 0
}

if ($command -match 'HOOK-DENY') {
    Write-HookDecision @{ decision = 'deny'; reason = 'hook-all-events: HOOK-DENY sentinel found.' }
    exit 0
}

if ($command -match 'HOOK-ASK') {
    Write-HookDecision @{ decision = 'ask'; reason = 'hook-all-events: HOOK-ASK sentinel found.' }
    exit 0
}

if ($command -match 'HOOK-REWRITE') {
    # The replacement must satisfy run_shell_command's schema (command + timeoutSeconds).
    Write-HookDecision @{
        decision         = 'continue'
        reason           = 'hook-all-events: HOOK-REWRITE sentinel found.'
        updatedArguments = @{ command = 'echo "hook-all-events replaced the command"'; timeoutSeconds = 60 }
    }
}

exit 0
