<#
.SYNOPSIS
Verifies that concurrent WSJT-X / JTDX / WSJT-Z instances emit UDP reporting
traffic on their assigned per-slice ports (issue #28 multi-instance binding).

.DESCRIPTION
Binds a UDP listener on each given port and decodes the WSJT-X message header
(big-endian: uint32 magic 0xADBCCBDA, uint32 schema, uint32 message type, then
the sender's instance ID as a length-prefixed UTF-8 string). The instance ID
is the --rig-name profile (for example "WSJT-X - SliceA"), so the output shows
exactly which instance is transmitting on which port.

The invariant under test: each port receives messages from exactly one
instance, and each instance appears on exactly one port. Any violation is
printed in red as CROSS-TALK.

WSJT-X sends a heartbeat every ~15 seconds even with no decodes, so the test
works on a dead band. Run this INSTEAD of JTAlert/GridTracker (they would hold
the ports), start the digital instances from SmartStreamer4, and watch the
attribution lines. Stop with Ctrl+C.

.EXAMPLE
.\Test-WsjtxUdpPorts.ps1
Listens on the SmartStreamer4 defaults, 2237 (Slice A) and 2238 (Slice B).

.EXAMPLE
.\Test-WsjtxUdpPorts.ps1 -Ports 2237,2238,2239,2240
All four slices on a FLEX-6600.
#>
[CmdletBinding()]
param(
    [int[]]$Ports = @(2237, 2238)
)

Set-StrictMode -Version Latest

# WSJT-X UDP protocol constants (NetworkMessage.hpp in the WSJT-X source).
# The L suffix matters: a bare 32-bit hex literal parses as a negative Int32
# in PowerShell, which silently breaks the -eq against the parsed header.
$WsjtxMagic = 0xADBCCBDAL
$NullStringLength = 0xFFFFFFFFL
$MessageTypeNames = @{
    0  = 'Heartbeat'
    1  = 'Status'
    2  = 'Decode'
    3  = 'Clear'
    4  = 'Reply'
    5  = 'QsoLogged'
    6  = 'Close'
    7  = 'Replay'
    8  = 'HaltTx'
    9  = 'FreeText'
    10 = 'WsprDecode'
    11 = 'Location'
    12 = 'LoggedAdif'
    13 = 'HighlightCallsign'
    14 = 'SwitchConfiguration'
    15 = 'Configure'
}

function Read-UInt32BE([byte[]]$Buffer, [int]$Offset) {
    if ($Offset + 4 -gt $Buffer.Length) { return $null }
    return ([int64]$Buffer[$Offset] -shl 24) -bor
           ([int64]$Buffer[$Offset + 1] -shl 16) -bor
           ([int64]$Buffer[$Offset + 2] -shl 8) -bor
           ([int64]$Buffer[$Offset + 3])
}

function ConvertFrom-WsjtxDatagram([byte[]]$Buffer) {
    $magic = Read-UInt32BE $Buffer 0
    if ($null -eq $magic -or $magic -ne $WsjtxMagic) { return $null }

    $type = Read-UInt32BE $Buffer 8
    $idLength = Read-UInt32BE $Buffer 12
    $id = '<none>'
    if ($null -ne $idLength -and $idLength -ne $NullStringLength -and
        16 + $idLength -le $Buffer.Length) {
        $id = [System.Text.Encoding]::UTF8.GetString($Buffer, 16, $idLength)
    }

    $typeName = $MessageTypeNames[[int]$type] ?? "Type$type"
    return [pscustomobject]@{ TypeName = $typeName; Id = $id }
}

$clients = @{}
foreach ($port in $Ports) {
    try {
        $clients[$port] = [System.Net.Sockets.UdpClient]::new($port)
    }
    catch {
        Write-Host "Cannot bind UDP port $port. Close any app holding it (JTAlert, GridTracker, another listener) and retry." -ForegroundColor Red
        foreach ($c in $clients.Values) { $c.Dispose() }
        exit 1
    }
}

Write-Host "Listening on UDP port(s): $($Ports -join ', ')"
Write-Host 'Start the digital instances now. WSJT-X heartbeats arrive every ~15 s; decode bursts follow each FT8 cycle. Ctrl+C to stop.'
Write-Host ''

$idsSeenByPort = @{}
$portsSeenById = @{}
$taskByPort = @{}
foreach ($port in $Ports) { $taskByPort[$port] = $clients[$port].ReceiveAsync() }

try {
    while ($true) {
        $taskArray = [System.Threading.Tasks.Task[]]@(foreach ($p in $Ports) { $taskByPort[$p] })
        $index = [System.Threading.Tasks.Task]::WaitAny($taskArray, 500)
        if ($index -lt 0) { continue }

        $port = $Ports[$index]
        try {
            $datagram = $taskArray[$index].GetAwaiter().GetResult()
        }
        catch [System.Net.Sockets.SocketException] {
            $taskByPort[$port] = $clients[$port].ReceiveAsync()
            continue
        }
        $taskByPort[$port] = $clients[$port].ReceiveAsync()

        $message = ConvertFrom-WsjtxDatagram $datagram.Buffer
        if ($null -eq $message) {
            Write-Host ("{0:HH:mm:ss}  port {1}  non-WSJT-X datagram from {2} ({3} bytes)" -f (Get-Date), $port, $datagram.RemoteEndPoint, $datagram.Buffer.Length) -ForegroundColor Yellow
            continue
        }

        Write-Host ("{0:HH:mm:ss}  port {1}  {2,-12} id='{3}'" -f (Get-Date), $port, $message.TypeName, $message.Id)

        if (-not $idsSeenByPort.ContainsKey($port)) {
            $idsSeenByPort[$port] = [System.Collections.Generic.HashSet[string]]::new()
        }
        if ($idsSeenByPort[$port].Add($message.Id)) {
            if ($idsSeenByPort[$port].Count -eq 1) {
                Write-Host ("           port {0} is owned by '{1}'" -f $port, $message.Id) -ForegroundColor Green
            }
            else {
                Write-Host ("           CROSS-TALK: port {0} has received from multiple instances: {1}" -f $port, ($idsSeenByPort[$port] -join ', ')) -ForegroundColor Red
            }
        }

        if (-not $portsSeenById.ContainsKey($message.Id)) {
            $portsSeenById[$message.Id] = [System.Collections.Generic.HashSet[int]]::new()
        }
        if ($portsSeenById[$message.Id].Add($port) -and $portsSeenById[$message.Id].Count -gt 1) {
            Write-Host ("           CROSS-TALK: instance '{0}' is sending to multiple ports: {1}" -f $message.Id, ($portsSeenById[$message.Id] -join ', ')) -ForegroundColor Red
        }
    }
}
finally {
    foreach ($c in $clients.Values) { $c.Dispose() }
    Write-Host ''
    Write-Host 'Summary:'
    foreach ($port in $Ports) {
        $ids = if ($idsSeenByPort.ContainsKey($port)) { $idsSeenByPort[$port] -join ', ' } else { '(no traffic)' }
        Write-Host ("  port {0}: {1}" -f $port, $ids)
    }
}
