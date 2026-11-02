# Prints every TCP connection to 127.0.0.1:8765 and every UDP datagram to 127.0.0.1:8766: the
# addresses probe.html aims its requests, WebSocket, TURN and STUN at. Any line it prints is a leak.
param([int]$TcpPort = 8765, [int]$UdpPort = 8766)

$tcp = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $TcpPort)
$udp = [Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, $UdpPort))
$tcp.Start()
Write-Host "Listening on tcp/$TcpPort and udp/$UdpPort. Ctrl+C stops."
try {
    while ($true) {
        while ($tcp.Pending()) {
            $client = $tcp.AcceptTcpClient()
            $stream = $client.GetStream()
            $stream.ReadTimeout = 1000
            $buffer = New-Object byte[] 1024
            try { $read = $stream.Read($buffer, 0, $buffer.Length) } catch { $read = 0 }
            $first = ([Text.Encoding]::ASCII.GetString($buffer, 0, $read) -split "`r?`n")[0]
            Write-Host ("{0:HH:mm:ss} LEAK tcp {1}" -f (Get-Date), $first)
            $client.Close()
        }
        while ($udp.Available -gt 0) {
            $from = [Net.IPEndPoint]::new([Net.IPAddress]::Any, 0)
            $datagram = $udp.Receive([ref]$from)
            Write-Host ("{0:HH:mm:ss} LEAK udp {1} bytes from {2}" -f (Get-Date), $datagram.Length, $from)
        }
        Start-Sleep -Milliseconds 100
    }
} finally {
    $tcp.Stop()
    $udp.Close()
}
