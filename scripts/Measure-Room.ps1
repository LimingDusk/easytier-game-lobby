# Run in an elevated Windows PowerShell after joining a room.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$LobbyProcessId,
    [ValidateRange(10, 3600)][int]$Seconds = 60
)
$ErrorActionPreference = 'Stop'
$lobby = Get-Process -Id $LobbyProcessId
$coreRow = Get-CimInstance Win32_Process -Filter "ParentProcessId = $LobbyProcessId AND Name = 'easytier-core.exe'"
if (@($coreRow).Count -ne 1 -or !$coreRow) { throw 'Join a room before sampling.' }
$core = Get-Process -Id $coreRow.ProcessId
$source = "GameLobby-CLI-$([guid]::NewGuid())"
$query = "SELECT * FROM Win32_ProcessStartTrace WHERE ParentProcessID = $LobbyProcessId AND ProcessName = 'easytier-cli.exe'"
Register-CimIndicationEvent -Query $query -SourceIdentifier $source | Out-Null
try {
    $lobbyCpu = $lobby.TotalProcessorTime
    $coreCpu = $core.TotalProcessorTime
    $clock = [Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds $Seconds
    $clock.Stop()
    $lobby.Refresh()
    $core.Refresh()
    if ($lobby.HasExited -or $core.HasExited) { throw 'Room ended during sampling.' }
    $events = @(Get-Event -SourceIdentifier $source -ErrorAction SilentlyContinue)
    [pscustomobject]@{
        Seconds = [math]::Round($clock.Elapsed.TotalSeconds, 2)
        CliStarts = $events.Count
        CliStartsPerMinute = [math]::Round($events.Count * 60 / $clock.Elapsed.TotalSeconds, 1)
        LobbyCpuSeconds = ($lobby.TotalProcessorTime - $lobbyCpu).TotalSeconds
        CoreCpuSeconds = ($core.TotalProcessorTime - $coreCpu).TotalSeconds
        LobbyWorkingSetMiB = [math]::Round($lobby.WorkingSet64 / 1MB, 1)
        CoreWorkingSetMiB = [math]::Round($core.WorkingSet64 / 1MB, 1)
    }
}
finally {
    Unregister-Event -SourceIdentifier $source
    Remove-Event -SourceIdentifier $source -ErrorAction SilentlyContinue
}
