$pluginRoot = if ($env:SELFCLAW_PLUGIN_ROOT) { $env:SELFCLAW_PLUGIN_ROOT } else { Split-Path -Parent $PSScriptRoot }
. (Join-Path -Path $pluginRoot -ChildPath 'lib/common.ps1')

$payload = Read-HookPayload

$bodyBytes = 0
if ($null -ne $payload.body) {
    $bodyBytes = [System.Text.Encoding]::UTF8.GetByteCount([string]$payload.body)
}

Add-HookLog 'http.jsonl' ([ordered]@{
    type                = 'request'
    turnId              = $payload.turnId
    origin              = $payload.origin
    requestSequence     = $payload.requestSequence
    method              = $payload.method
    url                 = $payload.url
    queryParameterNames = $payload.queryParameterNames
    bodyBytes           = $bodyBytes
    bodyTruncated       = $payload.bodyTruncated
}) | Out-Null

# Only whitelisted x-* / trace headers may be added; the host drops everything else.
Write-HookDecision @{
    addHeaders = @{
        'x-hook-all-events'  = 'request-sending'
        'x-hook-body-bytes'  = "$bodyBytes"
    }
}

exit 0
