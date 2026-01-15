### This was a first test, not suitable for sub 1s ping

$target = "1.1.1.1"
$interval = 100
$seq = 0
$lastTime = Get-Date

$tag = Get-Date -Format "yyyyMMdd_HHmmss"
$logFile = "PingLog_$tag.txt"
$csvFile = "PingLog_$tag.csv"

"Timestamp,Status,Sequence,Message,DelayMs" | Out-File $csvFile -Encoding utf8

Write-Host "`n🔄 Pinging $target every $interval ms... (Ctrl+C to stop)`n"
"[$(Get-Date -Format "yyyy-MM-dd HH:mm:ss")] Started" | Tee-Object -FilePath $logFile -Append

$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = @{
    FileName = "ping.exe"
    Arguments = "-t -w 1000 $target"
    RedirectStandardOutput = $true
    UseShellExecute = $false
    CreateNoWindow = $true
}
$proc.Start() | Out-Null
$reader = $proc.StandardOutput

try {
    while ($true) {
        if ($reader.Peek() -ge 0) {
            $line = $reader.ReadLine()
            $now = Get-Date
            $ts = $now.ToString("yyyy-MM-dd HH:mm:ss.fff")

            if ($line -match "^Reply from") {
                $delayMs = [math]::Round(($now - $lastTime).TotalMilliseconds)
                $msg = "[$ts] ✅ $line"

                Write-Host $msg
                $msg | Tee-Object -FilePath $logFile -Append
                "$ts,OK,$seq,""$line"",$delayMs" | Out-File $csvFile -Append

                $lastTime = $now
                $seq++
            }
            elseif ($line -match "Request timed out") {
                $delayMs = [math]::Round(($now - $lastTime).TotalMilliseconds)
                $msg = "[$ts] ❌ Timeout seq=$seq (~${delayMs}ms)"

                Write-Host $msg
                $msg | Tee-Object -FilePath $logFile -Append
                "$ts,TIMEOUT,$seq,,${delayMs}" | Out-File $csvFile -Append

                $lastTime = $now
                $seq++
            }
        }

        Start-Sleep -Milliseconds $interval
    }
}
finally {
    if (!$proc.HasExited) { $proc.Kill() }
    $proc.Dispose()
    $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $msg = "[$ts] 🛑 Ping test stopped."
    Write-Host $msg
    $msg | Tee-Object -FilePath $logFile -Append
}
