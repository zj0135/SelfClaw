[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
$event = if ($env:SELFCLAW_HOOK_EVENT) { $env:SELFCLAW_HOOK_EVENT } else { "unset" }
$test = if ($env:SELFCLAW_TEST) { "set" } else { "unset" }
$other = if ($env:MY_TEST_ENV) { $env:MY_TEST_ENV } else { "unset" }
[Console]::Out.Write("event=$event;test=$test;other=$other;cwd=$([Environment]::CurrentDirectory)")
exit 0
