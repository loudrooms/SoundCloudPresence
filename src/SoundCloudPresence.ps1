#Requires -Version 5.1
<#
    SoundCloud Presence - background service.
    Mirrors the Windows media session to Discord (title, artist, cover).
    Started by SoundCloudPresence.exe.
#>

[CmdletBinding()]
param(
    [string]$ClientId = '',
    [switch]$BrowserOnly,
    [string]$AssetKey = '',
    [ValidateSet(0, 2, 3)]
    [int]$ActivityType = 2,
    [switch]$NoCover,
    [double]$PollSeconds = 2,
    [switch]$HideWhenPaused,
    [switch]$VerbosePresence,
    [int]$CoverMaxSize = 256,
    [switch]$StopExisting,
    [switch]$Stop,
    [switch]$Status,
    [switch]$Version
)

$script:AppVersion = '2.0.0'
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

#region Paths and logging

$script:Root        = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$script:ConfigPath  = Join-Path $script:Root 'config.txt'
$script:PidFile     = Join-Path ([System.IO.Path]::GetTempPath()) 'SoundCloudPresence.pid'
$script:LogPath     = Join-Path ([System.IO.Path]::GetTempPath()) 'SoundCloudPresence.log'
$script:StateFile   = Join-Path ([System.IO.Path]::GetTempPath()) 'SoundCloudPresence.state'
$script:CoverFile   = Join-Path ([System.IO.Path]::GetTempPath()) 'SoundCloudPresence.cover.png'

function Write-Log {
    param([string]$Message)
    try {
        # cap it at ~256 KB
        $fi = New-Object System.IO.FileInfo $script:LogPath
        if ($fi.Exists -and $fi.Length -gt 262144) {
            Set-Content -LiteralPath $script:LogPath -Value @(Get-Content -LiteralPath $script:LogPath -Tail 200) -Encoding UTF8
        }
        Add-Content -Path $script:LogPath -Value "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $Message" -Encoding UTF8
    } catch { }
}

# simple key=value file the GUI reads.
function Write-State {
    param([string]$Title, [string]$Artist, [bool]$Playing, [string]$AppId, [bool]$Discord)
    try {
        $Title  = ($Title  -replace "`r|`n", ' ').Trim()
        $Artist = ($Artist -replace "`r|`n", ' ').Trim()
        $cover  = if (Test-Path -LiteralPath $script:CoverFile) { $script:CoverFile } else { '' }
        $text  = "Title=$Title`r`n"
        $text += "Artist=$Artist`r`n"
        $text += "Playing=$Playing`r`n"
        $text += "AppId=$AppId`r`n"
        $text += "Discord=$(if ($Discord) { 'connected' } else { '' })`r`n"
        $text += "Clients=$(@($script:Clients).Count)`r`n"
        $text += "Cover=$cover`r`n"
        $text += "Updated=$(Get-Date -Format 'HH:mm:ss')`r`n"
        Set-Content -LiteralPath $script:StateFile -Value $text -Encoding UTF8
    } catch { }
}

function Test-Truthy {
    param([string]$Value)
    return ($Value -match '^(1|true|yes|on)$')
}

#endregion

#region Configuration

if (Test-Path -LiteralPath $script:ConfigPath) {
    foreach ($raw in @(Get-Content -LiteralPath $script:ConfigPath)) {
        $line = ([string]$raw).Trim()
        if (-not $line -or $line.StartsWith('#')) { continue }
        $split = $line.IndexOf('=')
        if ($split -lt 1) { continue }
        $key = $line.Substring(0, $split).Trim().ToUpperInvariant()
        $val = $line.Substring($split + 1).Trim().Trim('"')
        switch ($key) {
            'CLIENT_ID'        { if (-not $ClientId) { $ClientId = $val } }
            'ASSET_KEY'        { if (-not $AssetKey) { $AssetKey = $val } }
            'ACTIVITY_TYPE'    { if ($val -match '^\d+$') { $ActivityType = [int]$val } }
            'POLL_SECONDS'     { if ($val -match '^\d+(\.\d+)?$') { $PollSeconds = [double]$val } }
            'COVER_MAX_SIZE'   { if ($val -match '^\d+$') { $CoverMaxSize = [int]$val } }
            'BROWSER_ONLY'     { if (Test-Truthy $val) { $BrowserOnly = $true } }
            'NO_COVER'         { if (Test-Truthy $val) { $NoCover = $true } }
            'HIDE_WHEN_PAUSED' { if (Test-Truthy $val) { $HideWhenPaused = $true } }
            'VERBOSE'          { if (Test-Truthy $val) { $VerbosePresence = $true } }
        }
    }
}

#endregion

#region Process control

function Get-RunningPid {
    if (-not (Test-Path -LiteralPath $script:PidFile)) { return $null }
    $raw = Get-Content -LiteralPath $script:PidFile -ErrorAction SilentlyContinue | Select-Object -First 1
    $procId = 0
    if ([int]::TryParse([string]$raw, [ref]$procId) -and $procId -gt 0) { return $procId }
    return $null
}

function Stop-RunningInstance {
    $procId = Get-RunningPid
    if ($procId) {
        $proc = Get-Process -Id $procId -ErrorAction SilentlyContinue
        if ($proc) { Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue }
    }
    Remove-Item -LiteralPath $script:PidFile -Force -ErrorAction SilentlyContinue
}

if ($Version) { Write-Host "SoundCloud Presence $script:AppVersion"; exit 0 }

if ($Stop) {
    $procId = Get-RunningPid
    Stop-RunningInstance
    if ($procId) { Write-Host "Stopped SoundCloud Presence (PID $procId)." }
    else { Write-Host 'SoundCloud Presence is not running.' }
    exit 0
}

if ($Status) {
    $procId = Get-RunningPid
    if ($procId -and (Get-Process -Id $procId -ErrorAction SilentlyContinue)) {
        Write-Host "SoundCloud Presence is running (PID $procId)."
    } else {
        Remove-Item -LiteralPath $script:PidFile -Force -ErrorAction SilentlyContinue
        Write-Host 'SoundCloud Presence is not running.'
    }
    exit 0
}

if ($StopExisting) { Stop-RunningInstance }

if (-not $ClientId -or $ClientId -eq 'PASTE_YOUR_APP_ID') {
    Write-Host 'No Discord application id set.' -ForegroundColor Red
    exit 1
}

try { Set-Content -LiteralPath $script:PidFile -Value $PID -Encoding ASCII -Force } catch { }

#endregion

#region Windows media session

Add-Type -AssemblyName System.Runtime.WindowsRuntime

$script:AsTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and
    $_.GetParameters().Count -eq 1 -and
    $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
})[0]

function Wait-WinRt {
    param($Operation, [Type]$ResultType)
    $asTask  = $script:AsTaskGeneric.MakeGenericMethod($ResultType)
    $netTask = $asTask.Invoke($null, @($Operation))
    $netTask.Wait(-1) | Out-Null
    $netTask.Result
}

$null = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType = WindowsRuntime]
$null = [Windows.Media.Control.GlobalSystemMediaTransportControlsSession, Windows.Media.Control, ContentType = WindowsRuntime]
$null = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties, Windows.Media.Control, ContentType = WindowsRuntime]
$null = [Windows.Storage.Streams.IRandomAccessStreamWithContentType, Windows.Storage.Streams, ContentType = WindowsRuntime]
$null = [Windows.Storage.Streams.IInputStream, Windows.Storage.Streams, ContentType = WindowsRuntime]

$script:AsStreamForRead = [System.IO.WindowsRuntimeStreamExtensions].GetMethods() |
    Where-Object { $_.Name -eq 'AsStreamForRead' -and $_.GetParameters().Count -eq 1 } |
    Select-Object -First 1

$script:StatusPlaying = 4
$script:StatusPaused  = 5
$script:BrowserHints  = @('chrome', 'msedge', 'firefox', 'brave', 'opera', 'vivaldi', 'chromium', 'iexplore', 'waterfox', 'librewolf')

function Test-IsBrowser {
    param([string]$AppId)
    if ([string]::IsNullOrWhiteSpace($AppId)) { return $false }
    $lower = $AppId.ToLower()
    foreach ($hint in $script:BrowserHints) { if ($lower.Contains($hint)) { return $true } }
    return $false
}

# cached, RequestAsync is slow
$script:SessionManager = $null

function Get-SessionManager {
    if (-not $script:SessionManager) {
        try {
            $script:SessionManager = Wait-WinRt ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager])
        } catch {
            $script:SessionManager = $null
        }
    }
    return $script:SessionManager
}

function Get-CurrentSession {
    $manager = Get-SessionManager
    if (-not $manager) { return $null }
    try {
        $session = $manager.GetCurrentSession()
        if ($script:Watcher) { $script:Watcher.AttachSession($session) }
        return $session
    } catch {
        $script:SessionManager = $null
        return $null
    }
}

function Get-PlaybackState {
    $session = Get-CurrentSession
    if (-not $session) { return $null }

    $appId = [string]$session.SourceAppUserModelId
    if ($BrowserOnly -and -not (Test-IsBrowser $appId)) { return $null }

    $playbackInfo = $session.GetPlaybackInfo()
    if (-not $playbackInfo) { return $null }
    $status = [int]$playbackInfo.PlaybackStatus
    if ($status -ne $script:StatusPlaying -and $status -ne $script:StatusPaused) { return $null }

    $props = Wait-WinRt ($session.TryGetMediaPropertiesAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties])
    if (-not $props) { return $null }

    $title  = [string]$props.Title
    $artist = [string]$props.Artist
    if ([string]::IsNullOrWhiteSpace($artist)) { $artist = [string]$props.AlbumArtist }
    if ([string]::IsNullOrWhiteSpace($title) -and [string]::IsNullOrWhiteSpace($artist)) { return $null }

    if ([string]::IsNullOrWhiteSpace($artist) -and $title -match '^(?<t>.+?)\s+[-\u2013]\s+(?<a>.+?)\s*(\||$)') {
        $title  = $Matches['t'].Trim()
        $artist = $Matches['a'].Trim()
    }
    $title  = $title  -replace '\s*[|\u2013-]\s*(Listen (online )?for free on )?SoundCloud.*$', ''
    $title  = $title  -replace '\s*\|\s*(YouTube|Spotify|Deezer|TIDAL|Apple Music).*$', ''
    $title  = ($title  -replace '\s+', ' ').Trim()
    $artist = ($artist -replace '\s+', ' ').Trim()

    [pscustomobject]@{
        Title     = $title
        Artist    = $artist
        AppId     = $appId
        IsPlaying = ($status -eq $script:StatusPlaying)
        Thumbnail = $props.Thumbnail
    }
}

function Limit-Text {
    param([string]$Text, [int]$Max = 128)
    if ([string]::IsNullOrWhiteSpace($Text)) { return '' }
    $Text = $Text.Trim()
    if ($Text.Length -gt $Max) { return $Text.Substring(0, $Max) }
    return $Text
}

# --- event watcher -----------------------------------------------------------
# PS can't hook WinRT events directly (Add-Type rejects winmd refs, reflection
# refuses WinRT events), so a tiny compiled shim subscribes via the DLR and
# signals one AutoResetEvent. If it fails the loop just polls instead.
$script:Watcher = $null
$watcherSource = @'
using System;
using System.Reflection;
using System.Threading;

public sealed class SmtcWatch : IDisposable
{
    public sealed class Emitter<TSender, TResult>
    {
        private readonly Action _cb;
        public Emitter(Action cb) { _cb = cb; }
        public void Handle(TSender sender, TResult result) { if (_cb != null) _cb(); }
    }

    private readonly AutoResetEvent _gate = new AutoResetEvent(false);
    private object _manager, _session;
    private EventInfo _playEvt, _propEvt;
    private dynamic _curH, _playH, _propH;

    public string LastError { get; private set; }

    public bool Start(object manager, Type managerType, Type sessionType)
    {
        try
        {
            _manager = manager;
            _playEvt = sessionType.GetEvent("PlaybackInfoChanged");
            _propEvt = sessionType.GetEvent("MediaPropertiesChanged");
            _curH = MakeHandler(managerType.GetEvent("CurrentSessionChanged"));
            dynamic d = _manager;
            d.CurrentSessionChanged += _curH;
            _gate.Set();
            return true;
        }
        catch (Exception ex) { LastError = ex.ToString(); return false; }
    }

    private dynamic MakeHandler(EventInfo ev)
    {
        Type dt = ev.EventHandlerType;
        Type et = typeof(Emitter<,>).MakeGenericType(dt.GetGenericArguments());
        object inst = Activator.CreateInstance(et, new object[] { (Action)Signal });
        return Delegate.CreateDelegate(dt, inst, et.GetMethod("Handle"));
    }

    private void Signal() { _gate.Set(); }

    public void AttachSession(object session)
    {
        try
        {
            if (ReferenceEquals(session, _session)) return;
            DetachSession();
            _session = session;
            if (_session == null) return;
            _playH = MakeHandler(_playEvt);
            _propH = MakeHandler(_propEvt);
            dynamic s = _session;
            s.PlaybackInfoChanged += _playH;
            s.MediaPropertiesChanged += _propH;
        }
        catch (Exception ex) { LastError = "attach: " + ex.Message; }
    }

    private void DetachSession()
    {
        if (_session == null) return;
        try { dynamic s = _session; s.PlaybackInfoChanged -= _playH; s.MediaPropertiesChanged -= _propH; } catch { }
        _session = null; _playH = null; _propH = null;
    }

    public bool Wait(int milliseconds) { return _gate.WaitOne(milliseconds); }

    public void Dispose()
    {
        try { if (_curH != null && _manager != null) { dynamic d = _manager; d.CurrentSessionChanged -= _curH; } } catch { }
        DetachSession();
    }
}
'@
try {
    Add-Type -TypeDefinition $watcherSource -ReferencedAssemblies @('System', 'System.Core', 'Microsoft.CSharp') -ErrorAction Stop
    $watchManager = Get-SessionManager
    if ($watchManager) {
        $w = New-Object SmtcWatch
        $watchManagerType = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType = WindowsRuntime]
        $watchSessionType = [Windows.Media.Control.GlobalSystemMediaTransportControlsSession, Windows.Media.Control, ContentType = WindowsRuntime]
        if ($w.Start($watchManager, $watchManagerType, $watchSessionType)) {
            $script:Watcher = $w
        } else {
            Write-Log "Event watcher failed to start: $($w.LastError)"
        }
    }
} catch {
    Write-Log "Event watcher unavailable, falling back to polling: $($_.Exception.Message)"
}

#endregion

#region Cover art

function Get-ThumbnailBytes {
    param($Thumbnail)
    if (-not $Thumbnail) { return $null }
    try {
        $stream = Wait-WinRt ($Thumbnail.OpenReadAsync()) ([Windows.Storage.Streams.IRandomAccessStreamWithContentType])
        if (-not $stream) { return $null }
        $netStream = $script:AsStreamForRead.Invoke($null, @($stream))
        $memory    = New-Object System.IO.MemoryStream
        $netStream.CopyTo($memory)
        $bytes = $memory.ToArray()
        $memory.Dispose(); $netStream.Dispose()
        if (-not $bytes -or $bytes.Length -eq 0) { return $null }
        return ,$bytes
    } catch {
        Write-Log "Thumbnail error: $($_.Exception.Message)"
        return $null
    }
}

function Resize-Image {
    param([byte[]]$Bytes, [int]$Max = 256)
    if (-not $Bytes -or $Bytes.Length -eq 0) { return $null }
    try {
        Add-Type -AssemblyName System.Drawing -ErrorAction SilentlyContinue
        $input    = New-Object System.IO.MemoryStream (, $Bytes)
        $image    = [System.Drawing.Image]::FromStream($input)
        $scale    = [Math]::Min(1.0, $Max / [double]([Math]::Max($image.Width, $image.Height)))
        $width    = [Math]::Max(1, [int]($image.Width  * $scale))
        $height   = [Math]::Max(1, [int]($image.Height * $scale))
        $bitmap   = New-Object System.Drawing.Bitmap $width, $height
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.DrawImage($image, 0, 0, $width, $height)
        $output = New-Object System.IO.MemoryStream
        $bitmap.Save($output, [System.Drawing.Imaging.ImageFormat]::Png)
        $result = $output.ToArray()
        $graphics.Dispose(); $bitmap.Dispose(); $image.Dispose(); $input.Dispose(); $output.Dispose()
        if ($result -and $result.Length -gt 0) { return ,$result }
        return ,$Bytes
    } catch {
        return ,$Bytes
    }
}

# resized copy of the thumbnail for the GUI, cached per track
function Save-LocalCover {
    param($Thumbnail, [string]$TrackKey)
    if ($TrackKey -eq $script:CoverSourceKey -and (Test-Path -LiteralPath $script:CoverFile)) {
        return $script:CoverBytes
    }
    $script:CoverBytes = $null
    $bytes = Get-ThumbnailBytes $Thumbnail
    if (-not $bytes) {
        Remove-Item -LiteralPath $script:CoverFile -Force -ErrorAction SilentlyContinue
        $script:CoverSourceKey = $TrackKey
        return $null
    }
    $resized = Resize-Image $bytes $CoverMaxSize
    try { [System.IO.File]::WriteAllBytes($script:CoverFile, $resized) } catch { }
    $script:CoverBytes     = $resized
    $script:CoverSourceKey = $TrackKey
    return $resized
}

# upload so Discord can use the cover as an image url. one job at a time.
function Request-CoverUpload {
    param($ResizedBytes, [string]$TrackKey)
    if ($NoCover -or -not $ResizedBytes) { return }
    if ($script:CoverJob) { return }
    if ($script:CoverMap.ContainsKey($TrackKey)) { return }
    if ([int]$script:FailCount[$TrackKey] -ge 3) { $script:CoverMap[$TrackKey] = ''; return }

    $seed = Join-Path ([System.IO.Path]::GetTempPath()) ('sc_seed_' + [guid]::NewGuid().ToString('N') + '.png')
    try { [System.IO.File]::WriteAllBytes($seed, $ResizedBytes) } catch { return }

    try {
        $job = Start-Job -ArgumentList $seed -ScriptBlock {
            param($file)
            try {
                $r = (& curl.exe -s -A "Mozilla/5.0" --max-time 25 -F "files[]=@$file" "https://uguu.se/upload?output=text" 2>$null | Out-String).Trim()
                if ($r -match '^https?://') { return (($r -split '\s+')[0]) }
                $r = (& curl.exe -s -A "Mozilla/5.0" --max-time 25 -F "file=@$file" "https://tmpfiles.org/api/v1/upload" 2>$null | Out-String).Trim()
                if ($r -match '"url"\s*:\s*"(?<u>https?://[^"]+)"') { return ($Matches['u'] -replace 'tmpfiles\.org/', 'tmpfiles.org/dl/') }
                $r = (& curl.exe -s -A "Mozilla/5.0" --max-time 25 -F "reqtype=fileupload" -F "fileToUpload=@$file" "https://catbox.moe/user/api.php" 2>$null | Out-String).Trim()
                if ($r -match '^https?://') { return (($r -split '\s+')[0]) }
                return $null
            } finally {
                Remove-Item $file -ErrorAction SilentlyContinue
            }
        }
        $script:CoverJob = [pscustomobject]@{ Job = $job; Key = $TrackKey }
    } catch {
        Remove-Item $seed -Force -ErrorAction SilentlyContinue
    }
}

# picks up a finished upload job
function Receive-CoverJob {
    if (-not $script:CoverJob) { return $null }
    $state = $script:CoverJob.Job.State
    if ($state -ne 'Completed' -and $state -ne 'Failed' -and $state -ne 'Stopped') { return $null }

    $key    = $script:CoverJob.Key
    $job    = $script:CoverJob.Job
    $script:CoverJob = $null
    $result = $null
    try { $result = Receive-Job $job -ErrorAction SilentlyContinue | Select-Object -Last 1 } catch { }
    Remove-Job $job -Force -ErrorAction SilentlyContinue

    if ($result) {
        $script:CoverMap[$key] = $result
        return $result
    }
    $script:FailCount[$key] = [int]$script:FailCount[$key] + 1
    return $false
}

#endregion

#region Discord IPC

$script:Clients  = @()   # every connected Discord client: @{ Pipe; Name }
$script:CoolDown = @{}   # pipe name -> next allowed connect attempt
$script:CurPayload = ''  # last activity payload, for clients that join late
$script:Cleared    = $true

# every running Discord client (stable, PTB, canary, extra instances) owns one
# discord-ipc-N pipe - connect to all of them, broadcast to all of them.

function Send-Frame {
    param($Pipe, [int]$Op, [string]$Json)
    $data = [System.Text.Encoding]::UTF8.GetBytes($Json)
    $Pipe.Write([BitConverter]::GetBytes([int]$Op), 0, 4)
    $Pipe.Write([BitConverter]::GetBytes([int]$data.Length), 0, 4)
    if ($data.Length -gt 0) { $Pipe.Write($data, 0, $data.Length) }
    $Pipe.Flush()
}

function Read-Frame {
    param($Pipe)
    $header = New-Object byte[] 8
    $read   = 0
    while ($read -lt 8) {
        $n = $Pipe.Read($header, $read, 8 - $read)
        if ($n -le 0) { throw 'Discord pipe closed' }
        $read += $n
    }
    $op     = [BitConverter]::ToInt32($header, 0)
    $length = [BitConverter]::ToInt32($header, 4)
    $buffer = New-Object byte[] $length
    $read   = 0
    while ($read -lt $length) {
        $n = $Pipe.Read($buffer, $read, $length - $read)
        if ($n -le 0) { throw 'Discord pipe closed' }
        $read += $n
    }
    if ($op -eq 3) {
        $pong = [System.Text.Encoding]::UTF8.GetBytes('{}')
        $Pipe.Write([BitConverter]::GetBytes([int]4), 0, 4)
        $Pipe.Write([BitConverter]::GetBytes([int]$pong.Length), 0, 4)
        $Pipe.Write($pong, 0, $pong.Length)
        $Pipe.Flush()
        return (Read-Frame -Pipe $Pipe)
    }
    [pscustomobject]@{ Op = $op; Json = [System.Text.Encoding]::UTF8.GetString($buffer) }
}

function Get-DiscordPipeNames {
    try {
        return @([System.IO.Directory]::GetFiles('\\.\pipe\') |
            ForEach-Object { $_.Substring($_.LastIndexOf('\') + 1) } |
            Where-Object { $_ -match '^discord-ipc-\d+$' })
    } catch { return @() }
}

function Sync-DiscordClients {
    $found = @(Get-DiscordPipeNames)

    # drop clients whose pipe vanished or died
    $keep = @()
    foreach ($c in $script:Clients) {
        $alive = ($found -contains $c.Name)
        if ($alive) { try { $alive = $c.Pipe.IsConnected } catch { $alive = $false } }
        if ($alive) { $keep += $c }
        else {
            try { $c.Pipe.Dispose() } catch { }
            $script:CoolDown[$c.Name] = [DateTime]::Now.AddSeconds(3)
            Write-Log "Discord client on $($c.Name) disconnected"
        }
    }
    $script:Clients = $keep

    # try new pipes
    $known = @($script:Clients | ForEach-Object { $_.Name })
    foreach ($name in ($found | Sort-Object)) {
        if ($known -contains $name) { continue }
        $until = $script:CoolDown[$name]
        if ($until -and [DateTime]::Now -lt $until) { continue }
        $pipe = $null
        try {
            $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $name, [System.IO.Pipes.PipeDirection]::InOut)
            $pipe.Connect(150)
            Send-Frame -Pipe $pipe -Op 0 -Json (@{ v = 1; client_id = $ClientId } | ConvertTo-Json -Compress)
            $reply = Read-Frame -Pipe $pipe
            if ($reply.Op -eq 2) { throw "Discord rejected the connection: $($reply.Json)" }
            $script:Clients += @{ Pipe = $pipe; Name = $name }
            Write-Log "Discord client connected on $name ($($script:Clients.Count) total)"
            # send current activity to the newcomer
            if (-not $script:Cleared -and $script:CurPayload) {
                try { Send-Frame -Pipe $pipe -Op 1 -Json $script:CurPayload } catch { }
            }
        } catch {
            Write-Log "Handshake failed on ${name}: $($_.Exception.Message)"
            if ($pipe) { try { $pipe.Dispose() } catch { } }
            $script:CoolDown[$name] = [DateTime]::Now.AddSeconds(3)
        }
    }
    return $script:Clients.Count
}

function Broadcast-Discord {
    param([string]$Payload)
    $dead = @()
    foreach ($c in $script:Clients) {
        try { Send-Frame -Pipe $c.Pipe -Op 1 -Json $Payload } catch { $dead += $c.Name }
    }
    if (@($dead).Count -gt 0) {
        $script:Clients = @($script:Clients | Where-Object { $dead -notcontains $_.Name })
        foreach ($d in $dead) {
            $script:CoolDown[$d] = [DateTime]::Now.AddSeconds(3)
            Write-Log "Lost Discord client on $d"
        }
    }
}

function Set-Activity {
    param([string]$Title, [string]$Artist, [bool]$Paused, [string]$ImageUrl)
    $details = if ($Title) { $Title } else { 'Unknown title' }
    $state   = if ($Artist) { $Artist } else { 'Unknown artist' }
    if ($Paused) { $details = "Paused - $details" }

    $activity = @{
        type    = $ActivityType
        details = (Limit-Text $details)
        state   = (Limit-Text $state)
    }
    $image = $null
    if (-not [string]::IsNullOrWhiteSpace($ImageUrl)) { $image = $ImageUrl }
    elseif ($AssetKey) { $image = $AssetKey }
    if ($image) { $activity.assets = @{ large_image = $image; large_text = 'Now Playing' } }

    $payload = @{
        cmd   = 'SET_ACTIVITY'
        args  = @{ pid = $PID; activity = $activity }
        nonce = [guid]::NewGuid().ToString()
    } | ConvertTo-Json -Depth 10 -Compress

    $script:CurPayload = $payload
    $script:Cleared    = $false
    Broadcast-Discord -Payload $payload
}

function Clear-Activity {
    $payload = @{
        cmd   = 'SET_ACTIVITY'
        args  = @{ pid = $PID; activity = $null }
        nonce = [guid]::NewGuid().ToString()
    } | ConvertTo-Json -Depth 10 -Compress

    $script:CurPayload = ''
    $script:Cleared    = $true
    Broadcast-Discord -Payload $payload
}

#endregion

#region Main loop

Write-Log "SoundCloud Presence v$script:AppVersion starting (pid $PID)"
if ($script:Watcher) { Write-Log 'Event watcher active - track changes are detected instantly.' }
else { Write-Log "Event watcher unavailable - polling fallback active." }

$connected       = $false
$lastShown       = ''   # Title|Artist|Playing last sent to Discord
$lastTrackKey    = ''   # Title|Artist currently shown in the state file
$script:CoverMap      = @{}
$script:FailCount     = @{}
$script:CoverJob      = $null
$script:CoverBytes    = $null
$script:CoverSourceKey = ''
$script:NextSyncAt     = [DateTime]::MinValue

# fallback interval; with the watcher active this is only a safety net
$safetyMs = [int]([Math]::Max(500, [Math]::Min(5000, $PollSeconds * 1000)))

try {
    while ($true) {
        try {
            $state = $null
            try { $state = Get-PlaybackState } catch { Write-Log "Get-PlaybackState error: $($_.Exception.Message)"; $script:SessionManager = $null }

            # keep the client list fresh
            if ([DateTime]::Now -ge $script:NextSyncAt) {
                $null = Sync-DiscordClients
                $script:NextSyncAt = [DateTime]::Now.AddSeconds(2)
            }
            $connected = ($script:Clients.Count -gt 0)

            if ($state -and ($state.Title -or $state.Artist)) {
                $trackKey = "$($state.Title)|$($state.Artist)"
                $key      = "$trackKey|$($state.IsPlaying)"

                if ($trackKey -ne $lastTrackKey) {
                    $resized = Save-LocalCover -Thumbnail $state.Thumbnail -TrackKey $trackKey
                    if ($VerbosePresence) { Write-Log "Detected: '$($state.Artist) - $($state.Title)' via $($state.AppId)" }
                }

                Write-State -Title $state.Title -Artist $state.Artist -Playing $state.IsPlaying -AppId $state.AppId -Discord $connected

                # cover jobs
                $coverUrl = Receive-CoverJob
                if ($coverUrl) {
                    $cached = $script:CoverMap[$trackKey]
                    if ($cached -and $connected -and $key -eq $lastShown) {
                        Set-Activity -Title $state.Title -Artist $state.Artist -Paused (-not $state.IsPlaying) -ImageUrl $cached
                        Write-Log "Cover added: $cached"
                    }
                }
                if ($connected -and $key -ne $lastShown) {
                    $image = $script:CoverMap[$trackKey]
                    Set-Activity -Title $state.Title -Artist $state.Artist -Paused (-not $state.IsPlaying) -ImageUrl $image
                    $lastShown = $key
                    Write-Log "Activity: $($state.Artist) - $($state.Title)"
                    if (-not $script:CoverMap.ContainsKey($trackKey)) {
                        Request-CoverUpload -ResizedBytes (Save-LocalCover -Thumbnail $state.Thumbnail -TrackKey $trackKey) -TrackKey $trackKey
                    }
                }

                # retry when a job was still busy on the previous track
                if (-not $script:CoverJob -and -not $script:CoverMap.ContainsKey($trackKey) -and $key -eq $lastShown) {
                    Request-CoverUpload -ResizedBytes $script:CoverBytes -TrackKey $trackKey
                }
            }
            else {
                Remove-Item -LiteralPath $script:CoverFile -Force -ErrorAction SilentlyContinue
                Write-State -Title '' -Artist '' -Playing $false -AppId '' -Discord $connected
                if ($HideWhenPaused -and $connected -and $lastShown -ne '') {
                    try { Clear-Activity } catch { }
                }
                $lastShown    = ''
                $lastTrackKey = ''
                $script:CoverBytes = $null
                $script:CoverSourceKey = ''
            }
        }
        catch {
            Write-Log "ERROR: $($_.Exception.Message)"
            $script:NextSyncAt = [DateTime]::MinValue
        }

        # wait for a media event, or the fallback timeout
        if ($script:Watcher) {
            try { $null = $script:Watcher.Wait($safetyMs) } catch { Start-Sleep -Milliseconds $safetyMs }
        } else {
            Start-Sleep -Milliseconds $safetyMs
        }
    }
}
finally {
    if ($script:Watcher) { try { $script:Watcher.Dispose() } catch { } }
    if ($script:CoverJob) { try { Remove-Job $script:CoverJob.Job -Force -ErrorAction SilentlyContinue } catch { } }
    foreach ($c in $script:Clients) { try { $c.Pipe.Dispose() } catch { } }
    $script:Clients = @()
    Remove-Item -LiteralPath $script:PidFile -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $script:StateFile -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $script:CoverFile -ErrorAction SilentlyContinue
}

#endregion
