[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

$payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
$command = [string]$payload.arguments.command
if ([string]::IsNullOrWhiteSpace($command)) { exit 0 }

# Matching is case-insensitive; each rule carries the reason shown on the blocked tool card.
$rules = @(
    @{
        Pattern = '(?i)remove-item[^\r\n]*(?:-recurse[^\r\n]*-force|-force[^\r\n]*-recurse)'
        Reason  = 'Remove-Item -Recurse -Force deletes a tree irreversibly.'
    },
    @{
        Pattern = '(?i)(^|[\s;&|("''])rm\s+(?:-r(ecursive)?\s+-f(orce)?|-f(orce)?\s+-r(ecursive)?|-[a-z]*r[a-z]*f[a-z]*|-[a-z]*f[a-z]*r[a-z]*)'
        Reason  = 'rm with recursive and force flags deletes a tree irreversibly.'
    },
    @{
        Pattern = '(?i)(^|[\s;&|("''])r(m)?d(ir)?\s+/s'
        Reason  = 'rd /s removes a directory tree without confirmation.'
    },
    @{
        Pattern = '(?i)(^|[\s;&|("''])format\s'
        Reason  = 'format erases a volume.'
    },
    @{
        Pattern = '(?i)git\s+push[^\r\n]*(\s--force(\s|$)|\s-f(\s|$))'
        Reason  = 'git push --force can overwrite remote history.'
    },
    @{
        Pattern = '(?i)git\s+reset\s+--hard'
        Reason  = 'git reset --hard discards uncommitted work.'
    }
)

$reason = $null
foreach ($rule in $rules) {
    if ($command -match $rule.Pattern) {
        $reason = $rule.Reason
        break
    }
}

if ($reason) {
    [Console]::Out.Write((@{ decision = 'deny'; reason = $reason } | ConvertTo-Json -Compress))
}

exit 0
