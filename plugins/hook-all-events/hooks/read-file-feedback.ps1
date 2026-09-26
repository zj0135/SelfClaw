$pluginRoot = if ($env:SELFCLAW_PLUGIN_ROOT) { $env:SELFCLAW_PLUGIN_ROOT } else { Split-Path -Parent $PSScriptRoot }
. (Join-Path -Path $pluginRoot -ChildPath 'lib/common.ps1')

$payload = Read-HookPayload
if ($payload.toolName -ne 'read_file' -or $payload.status -ne 'completed') {
    exit 0
}

# Realistic use of toolExecuted feedback: the host owns the raw result, so a hook cannot replace
# it — it can only append guidance that the model reads alongside the result.
$notes = [System.Collections.Generic.List[string]]::new()

if ($payload.contentTruncated) {
    $notes.Add('The host truncated this tool result at 64 KiB; treat everything after the cut as missing and re-read a smaller range.')
}

$file = $payload.content
if ($null -ne $file -and $null -ne $file.truncated -and $file.truncated) {
    $next = [int]$file.endLine + 1
    $notes.Add("read_file returned lines $($file.startLine)-$($file.endLine) of $($file.totalLines); the file continues at line $next. Page with startLine=$next before concluding the file ends here.")
}

$text = if ($null -ne $file -and $null -ne $file.content) { [string]$file.content } else { '' }
if ($text -match '(-----BEGIN [A-Z ]*PRIVATE KEY-----|AKIA[0-9A-Z]{16}|ghp_[A-Za-z0-9]{20,}|sk-[A-Za-z0-9]{20,})') {
    $notes.Add('This file looks like it contains a credential or private key; do not quote it verbatim and never transmit it.')
}

if ($notes.Count -gt 0) {
    Write-HookDecision @{ feedback = ($notes -join ' ') }
}

exit 0
