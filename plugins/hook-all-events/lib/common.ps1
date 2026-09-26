# Shared helpers for every hook in this sample. Each script dot-sources this file through
# $env:SELFCLAW_PLUGIN_ROOT, which the host sets to the plugin's version directory.
[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

function Read-HookPayload {
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) {
        return $null
    }

    return $raw | ConvertFrom-Json
}

function Write-HookDecision($value) {
    [Console]::Out.Write(($value | ConvertTo-Json -Compress -Depth 8))
}

function Get-HookLogDirectory {
    return Join-Path $env:LOCALAPPDATA 'SelfClaw\hook-all-events'
}

function Add-HookLog([string]$fileName, $value) {
    $directory = Get-HookLogDirectory
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $path = Join-Path $directory $fileName
    $line = $value | ConvertTo-Json -Compress -Depth 8

    # Concurrent hook processes may hold the file; retry briefly, then fail loudly on stderr.
    $lastError = $null
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        try {
            Add-Content -LiteralPath $path -Value $line -Encoding utf8
            return $true
        }
        catch {
            $lastError = $_
            Start-Sleep -Milliseconds 100
        }
    }

    [Console]::Error.Write("hook-all-events: failed to append $fileName : $($lastError.Exception.Message)")
    return $false
}
