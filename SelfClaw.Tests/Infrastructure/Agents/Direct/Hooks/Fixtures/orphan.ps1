[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
$child = Start-Process -WindowStyle Hidden -PassThru -FilePath "powershell.exe" -ArgumentList "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60"
[Console]::Error.WriteLine("child pid=$($child.Id)")
[Console]::Out.Write('{"decision":"continue"}')
exit 0
