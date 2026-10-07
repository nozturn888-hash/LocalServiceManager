[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$InstallRoot,
    [Parameter(Mandatory=$true)][ValidateSet('Start','Stop','Restart','Status')][string]$Action,
    [string]$PythonRuntime = '',
    [int]$ExpectedPid = 0,
    [long]$ExpectedStartTicks = 0,
    [ValidateRange(1,30)][int]$DrainSeconds = 30
)
$ErrorActionPreference = 'Stop'
# This opt-in example requires the compatible Windows layout documented in README.md.
# Parameters are explicit; no installation discovery or private-machine defaults are used.
if(-not [IO.Path]::IsPathRooted($InstallRoot)) { throw 'InstallRoot must be an absolute installation directory.' }
$root = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
if($root -eq [IO.Path]::GetPathRoot($root).TrimEnd('\')) { throw 'InstallRoot must name an installation directory, not a filesystem root.' }
$gatewayHome = Join-Path $root 'home'
$hermesExe = Join-Path $gatewayHome 'hermes-agent\venv\Scripts\hermes.exe'
if([string]::IsNullOrWhiteSpace($PythonRuntime)) {
    $pythonExe = Join-Path $gatewayHome 'runtimes\python-3.11.15\python.exe'
} else {
    $pythonExe = [IO.Path]::GetFullPath($(if([IO.Path]::IsPathRooted($PythonRuntime)) { $PythonRuntime } else { Join-Path $root $PythonRuntime }))
}
if(-not $pythonExe.StartsWith(($root + '\'),[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($pythonExe) -ine 'python.exe') {
    throw 'PythonRuntime must identify python.exe inside InstallRoot.'
}
if(-not (Test-Path -LiteralPath $gatewayHome -PathType Container) -or -not (Test-Path -LiteralPath $hermesExe -PathType Leaf) -or -not (Test-Path -LiteralPath $pythonExe -PathType Leaf)) {
    throw 'Compatible Hermes Windows layout or Python runtime is missing; no action was performed.'
}
$receiptPath = Join-Path $gatewayHome 'gateway.pid'
$env:HERMES_HOME = $gatewayHome
$env:PYTHONIOENCODING = 'utf-8'
$env:PYTHONUNBUFFERED = '1'

# No imports of Hermes config/auth and no process-name or window-title termination.
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
public sealed class LsmGatewayProcess : IDisposable {
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint a, bool i, int p);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetProcessTimes(IntPtr h, out long c, out long e, out long k, out long u);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool QueryFullProcessImageName(IntPtr h, int f, StringBuilder b, ref int n);
    [DllImport("kernel32.dll", SetLastError=true)] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("shell32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CommandLineToArgvW(string s, out int n);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
    IntPtr handle;
    public readonly int Pid;
    public readonly long StartTicks;
    public readonly string Executable;
    public LsmGatewayProcess(int pid) {
        Pid=pid; handle=OpenProcess(0x101000u, false, pid);
        if(handle==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            long c,e,k,u; var b=new StringBuilder(32768); int n=b.Capacity;
            if(!GetProcessTimes(handle,out c,out e,out k,out u) || !QueryFullProcessImageName(handle,0,b,ref n)) throw new Win32Exception(Marshal.GetLastWin32Error());
            StartTicks=DateTime.FromFileTimeUtc(c).Ticks; Executable=b.ToString();
        } catch { Dispose(); throw; }
    }
    public bool Wait(int ms) { uint r=WaitForSingleObject(handle,(uint)ms); if(r==0xffffffff) throw new Win32Exception(Marshal.GetLastWin32Error()); return r==0; }
    public void Dispose() { if(handle!=IntPtr.Zero) { CloseHandle(handle); handle=IntPtr.Zero; } }
    public static string[] Split(string command) {
        int n; IntPtr p=CommandLineToArgvW(command,out n); if(p==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try { var a=new string[n]; for(int i=0;i<n;i++) a[i]=Marshal.PtrToStringUni(Marshal.ReadIntPtr(p,i*IntPtr.Size)); return a; } finally { LocalFree(p); }
    }
}
'@
function Same-Path([string]$a, [string]$b) {
    if([string]::IsNullOrWhiteSpace($a) -or [string]::IsNullOrWhiteSpace($b)) { return $false }
    return [string]::Equals([IO.Path]::GetFullPath($a).TrimEnd('\'),[IO.Path]::GetFullPath($b).TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)
}
function Valid-Command([string[]]$argv) {
    return ($argv.Count -eq 4 -or ($argv.Count -eq 5 -and $argv[4] -ceq '--replace')) -and
        (Same-Path $argv[0] $pythonExe) -and (Same-Path $argv[1] $hermesExe) -and
        $argv[2] -ceq 'gateway' -and $argv[3] -ceq 'run'
}
function Get-Gateway {
    # Search is observation only. Every matching process must have this exact executable/argv.
    $matches = @()
    foreach($row in @(Get-CimInstance Win32_Process -Filter "Name='python.exe'")) {
        if(-not (Same-Path $row.ExecutablePath $pythonExe)) { continue }
        if([string]::IsNullOrWhiteSpace($row.CommandLine)) { throw 'Cannot inspect an installation process; control refused.' }
        $argv = [LsmGatewayProcess]::Split([string]$row.CommandLine)
        if($argv.Count -ge 4 -and (Same-Path $argv[1] $hermesExe) -and $argv[2] -ceq 'gateway' -and $argv[3] -ceq 'run') {
            if(-not (Valid-Command $argv)) { throw 'Unsupported Gateway arguments; control refused.' }
            $matches += $row
        }
    }
    if($matches.Count -gt 1) { throw 'More than one exact Gateway is running; control refused.' }
    if($matches.Count -eq 0) { return $null }
    if(-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) { throw 'Gateway is present but gateway.pid is missing; control refused.' }
    if((Get-Item -LiteralPath $receiptPath).Length -gt 1048576) { throw 'Gateway receipt exceeds 1 MiB; control refused.' }
    $record = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $row = $matches[0]
    $pinned = [LsmGatewayProcess]::new([int]$row.ProcessId)
    try {
        $centis = [long][Math]::Round(($pinned.StartTicks - 621355968000000000L) / 100000.0)
        $args = @($record.argv)
        $recordExe = [IO.Path]::ChangeExtension($hermesExe,$null)
        $validArgs = ($args.Count -eq 3 -or ($args.Count -eq 4 -and $args[3] -ceq '--replace')) -and
            ((Same-Path $args[0] $recordExe) -or (Same-Path $args[0] $hermesExe)) -and $args[1] -ceq 'gateway' -and $args[2] -ceq 'run'
        if($record.kind -cne 'hermes-gateway' -or [int]$record.pid -ne $pinned.Pid -or
            [long]$record.start_time -ne $centis -or -not (Same-Path $record.hermes_home $gatewayHome) -or
            -not (Same-Path $pinned.Executable $pythonExe) -or -not $validArgs -or $pinned.Wait(0)) {
            throw 'Gateway receipt/process identity mismatch; control refused.'
        }
        return [PSCustomObject]@{Pid=$pinned.Pid;StartTicks=$pinned.StartTicks;StartCentis=$centis;CommandLine=[string]$row.CommandLine}
    } finally { $pinned.Dispose() }
}
function Stop-Gateway {
    $target = Get-Gateway
    if($null -eq $target) { Write-Output 'Gateway is already stopped.'; return }
    if(($ExpectedPid -ne 0 -and $ExpectedPid -ne $target.Pid) -or
        ($ExpectedStartTicks -ne 0 -and $ExpectedStartTicks -ne $target.StartTicks)) {
        throw 'Gateway changed since the manager check; stop refused.'
    }
    # Hold the original OS process handle through the drain; PID recycling cannot change its target.
    $pinned = [LsmGatewayProcess]::new($target.Pid)
    try {
        if($pinned.StartTicks -ne $target.StartTicks -or -not (Same-Path $pinned.Executable $pythonExe)) { throw 'Gateway identity changed before stop.' }
        $again = Get-Gateway
        if($null -eq $again) { return }
        if($again.Pid -ne $target.Pid -or $again.StartTicks -ne $target.StartTicks -or $again.CommandLine -cne $target.CommandLine) { throw 'Gateway identity changed before stop.' }
        # Same protocol as gateway.status.write_planned_stop_marker(), with an extra home scope.
        $marker = Join-Path $gatewayHome '.gateway-planned-stop.json'
        $tmp = "$marker.$([Guid]::NewGuid().ToString('N')).tmp"
        $payload = @{target_pid=$target.Pid;target_start_time=$target.StartCentis;target_hermes_home=$gatewayHome;stopper_pid=$PID;written_at=[DateTimeOffset]::UtcNow.ToString('o')} | ConvertTo-Json -Compress
        try {
            [IO.File]::WriteAllText($tmp,$payload,[Text.UTF8Encoding]::new($false))
            if([IO.File]::Exists($marker)) { [IO.File]::Replace($tmp,$marker,$null) } else { [IO.File]::Move($tmp,$marker) }
        } finally { if([IO.File]::Exists($tmp)) { [IO.File]::Delete($tmp) } }
        Write-Output "Requested Gateway drain: PID $($target.Pid)."
        if(-not $pinned.Wait($DrainSeconds * 1000)) {
            $again = Get-Gateway
            if($null -eq $again -or $again.Pid -ne $target.Pid -or $again.StartTicks -ne $target.StartTicks -or $again.CommandLine -cne $target.CommandLine) { throw 'Identity changed during drain; restart refused.' }
            throw 'Planned-stop drain timed out; Gateway was not forcibly terminated and restart was refused.'
        }
        if($null -ne (Get-Gateway)) { throw 'A replacement Gateway appeared; restart refused.' }
        Write-Output 'Verified Gateway stopped. Other CLI/MCP/tasks were not targeted.'
    } finally { $pinned.Dispose() }
}
$mutex = [Threading.Mutex]::new($false, ('Local\LsmHermesGateway-' + [Convert]::ToBase64String([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($root.ToLowerInvariant()))).Replace('/','_').Replace('+','-')))
$locked = $false
try {
    if($Action -eq 'Status') {
        $s = Get-Gateway
        if($null -eq $s) { Write-Output '{"state":"stopped"}' } else { @{state='process-running';pid=$s.Pid;startTicks=$s.StartTicks;identityVerified=$true;platformReadyVerified=$false} | ConvertTo-Json -Compress }
        exit 0
    }
    $locked = $mutex.WaitOne(0)
    if(-not $locked) { throw 'Another Gateway control operation is in progress.' }
    if($Action -eq 'Stop' -or $Action -eq 'Restart') { Stop-Gateway }
    if($Action -eq 'Start' -or $Action -eq 'Restart') {
        if($null -ne (Get-Gateway)) { Write-Output 'Gateway is already running; start is a no-op.'; exit 0 }
        if(-not (Test-Path -LiteralPath $hermesExe -PathType Leaf)) { throw 'Hermes executable is missing.' }
        Set-Location -LiteralPath $gatewayHome
        # Original entry uses gateway run --replace. After verified stop, run without takeover:
        # a racing external gateway must be refused by Hermes, never replaced automatically.
        Write-Output 'Starting this installation: hermes.exe gateway run (no takeover).'
        $mutex.ReleaseMutex(); $locked = $false
        & $hermesExe gateway run
        exit $LASTEXITCODE
    }
    exit 0
} catch {
    [Console]::Error.WriteLine('Gateway control failed: ' + $_.Exception.Message)
    exit 1
} finally { if($locked) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
