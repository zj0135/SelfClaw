$pluginRoot = if ($env:SELFCLAW_PLUGIN_ROOT) { $env:SELFCLAW_PLUGIN_ROOT } else { Split-Path -Parent $PSScriptRoot }
. (Join-Path -Path $pluginRoot -ChildPath 'lib/common.ps1')

$payload = Read-HookPayload

# Asynchronous httpResponseReceived: response headers are visible, the body never is.
Add-HookLog 'http.jsonl' ([ordered]@{
    type            = 'response'
    turnId          = $payload.turnId
    origin          = $payload.origin
    requestSequence = $payload.requestSequence
    statusCode      = $payload.statusCode
    reasonPhrase    = $payload.reasonPhrase
    elapsedMs       = $payload.elapsedMs
    error           = $payload.error
}) | Out-Null

exit 0
