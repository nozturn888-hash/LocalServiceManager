using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LocalServiceManager
{
    public static class CommandLine
    {
        public static string Quote(string value)
        {
            var b = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { b.Append('\\', slashes * 2 + 1); b.Append(c); slashes = 0; continue; }
                b.Append('\\', slashes); slashes = 0; b.Append(c);
            }
            b.Append('\\', slashes * 2); return b.Append('"').ToString();
        }
        public static void Resolve(ServiceConfig s, ProjectPaths paths, string script, string args, out string exe, out string command, out string cwd)
        {
            script = paths.Resolve(script);
            if (!File.Exists(script)) throw new FileNotFoundException(L.Text("启动/停止入口不存在"), script);
            cwd = String.IsNullOrWhiteSpace(s.WorkingDirectory) ? Path.GetDirectoryName(script) : paths.Resolve(s.WorkingDirectory);
            if (!Directory.Exists(cwd)) throw new DirectoryNotFoundException(L.Text("工作目录不存在"));
            string ext = Path.GetExtension(script).ToLowerInvariant();
            if (ext == ".cmd" || ext == ".bat")
            {
                if (script.IndexOfAny(new[] { '%', '"', '\r', '\n' }) >= 0) throw new InvalidOperationException(L.Text("CMD 路径含不支持的 % 或引号，请改用 PS1/EXE 入口"));
                exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                command = Quote(exe) + " /D /S /C \"\"" + script + "\" " + args + "\"";
            }
            else if (ext == ".ps1")
            {
                exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
                command = Quote(exe) + " -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Quote(script) + " " + args;
            }
            else if (ext == ".exe") { exe = script; command = Quote(exe) + " " + args; }
            else throw new InvalidOperationException(L.Text("支持 CMD/BAT/PS1/EXE。Python 请选 python.exe 作为入口，并把脚本放入参数"));
        }
    }
    public sealed class LaunchResult : IDisposable
    {
        public Process Process;
        public ProcessIdentity Identity;
        public string Stdout, Stderr;
        public void Dispose() { if (Process != null) Process.Dispose(); }
    }
    public static class NativeLaunch
    {
        [StructLayout(LayoutKind.Sequential)] struct SECURITY_ATTRIBUTES { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct STARTUPINFO
        {
            public int cb; public string reserved, desktop, title; public int x, y, xSize, ySize, xCountChars, yCountChars, fillAttribute, flags; public short showWindow, reserved2; public IntPtr reservedPtr, stdin, stdout, stderr;
        }
        [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr process, thread; public int pid, tid; }
        [StructLayout(LayoutKind.Sequential)] struct STARTUPINFOEX { public STARTUPINFO Info; public IntPtr Attributes; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string file, uint access, uint share, ref SECURITY_ATTRIBUTES sa, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateProcess(string app, StringBuilder command, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string cwd, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
        sealed class HandleList : IDisposable
        {
            public IntPtr Attributes;
            IntPtr values;
            public HandleList(params IntPtr[] handles)
            {
                IntPtr size = IntPtr.Zero; InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size); Attributes = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(Attributes, 1, 0, ref size)) { Marshal.FreeHGlobal(Attributes); Attributes = IntPtr.Zero; throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
                values = Marshal.AllocHGlobal(handles.Length * IntPtr.Size);
                for (int i = 0; i < handles.Length; i++) Marshal.WriteIntPtr(values, i * IntPtr.Size, handles[i]);
                if (!UpdateProcThreadAttribute(Attributes, 0, new IntPtr(0x20002), values, new IntPtr(handles.Length * IntPtr.Size), IntPtr.Zero, IntPtr.Zero)) { int error = Marshal.GetLastWin32Error(); Dispose(); throw new System.ComponentModel.Win32Exception(error); }
            }
            public void Dispose() { if (Attributes != IntPtr.Zero) { DeleteProcThreadAttributeList(Attributes); Marshal.FreeHGlobal(Attributes); Attributes = IntPtr.Zero; } if (values != IntPtr.Zero) { Marshal.FreeHGlobal(values); values = IntPtr.Zero; } }
        }
        public static LaunchResult Start(ServiceConfig s, ProjectPaths paths, string script, string args, string action, IDictionary<string,string> environment = null)
        {
            string exe, command, cwd; CommandLine.Resolve(s, paths, script, args, out exe, out command, out cwd);
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            string prefix = Path.Combine(paths.Logs, s.Id + "-" + action + "-" + stamp);
            string output = prefix + ".stdout.log", error = prefix + ".stderr.log";
            var sa = new SECURITY_ATTRIBUTES { Length = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)), Inherit = true };
            // Native append-only file handles keep the child independent of the GUI's lifetime.
            // NUL stdin prevents a hidden PAUSE/Read-Host from waiting for an invisible console.
            using (var stdout = CreateFile(output, 0x4, 7, ref sa, 4, 0x80, IntPtr.Zero))
            using (var stderr = CreateFile(error, 0x4, 7, ref sa, 4, 0x80, IntPtr.Zero))
            using (var stdin = CreateFile("NUL", 0x80000000, 7, ref sa, 3, 0x80, IntPtr.Zero))
            {
                if (stdout.IsInvalid || stderr.IsInvalid || stdin.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                using (var block = new EnvironmentBlock(environment))
                using (var handles = new HandleList(stdin.DangerousGetHandle(), stdout.DangerousGetHandle(), stderr.DangerousGetHandle()))
                {
                var si = new STARTUPINFOEX { Attributes = handles.Attributes, Info = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFOEX)), flags = 0x101, showWindow = 0, stdin = stdin.DangerousGetHandle(), stdout = stdout.DangerousGetHandle(), stderr = stderr.DangerousGetHandle() } };
                PROCESS_INFORMATION pi;
                if (!CreateProcess(exe, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, true, 0x08080004 | (block.Pointer == IntPtr.Zero ? 0u : 0x400u), block.Pointer, cwd, ref si, out pi)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                bool resumed = false;
                try
                {
                    var p = Process.GetProcessById(pi.pid); var pinned = p.Handle;
                    var identity = ProcessInspector.Read(pi.pid);
                    if (identity == null) { p.Kill(); p.Dispose(); throw new InvalidOperationException(L.Text("无法核实新建进程身份，未运行入口")); }
                    if (ResumeThread(pi.thread) == UInt32.MaxValue) { p.Kill(); p.Dispose(); throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
                    resumed = true;
                    return new LaunchResult { Process = p, Identity = identity, Stdout = output, Stderr = error };
                }
                catch { if (!resumed) TerminateProcess(pi.process, 1); throw; }
                finally { CloseHandle(pi.thread); CloseHandle(pi.process); }
                }
            }
        }
    }
    sealed class EnvironmentBlock : IDisposable
    {
        public IntPtr Pointer;
        public EnvironmentBlock(IDictionary<string,string> overrides)
        {
            if(overrides==null)return;
            var values=new SortedDictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(System.Collections.DictionaryEntry item in Environment.GetEnvironmentVariables())values[Convert.ToString(item.Key)]=Convert.ToString(item.Value);
            foreach(var item in overrides)values[item.Key]=item.Value;
            Pointer=Marshal.StringToHGlobalUni(String.Join("\0",values.Select(x=>x.Key+"="+x.Value))+"\0\0");
        }
        public void Dispose(){if(Pointer!=IntPtr.Zero)Marshal.FreeHGlobal(Pointer);}
    }
    public static class ProcessInspector
    {
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        static ProcessIdentity Basics(int pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid); if (h == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var path = new StringBuilder(32768); int length = path.Capacity; long created, exited, kernel, user;
                if (!QueryFullProcessImageName(h, 0, path, ref length) || !GetProcessTimes(h, out created, out exited, out kernel, out user)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                return new ProcessIdentity { Pid = pid, Executable = path.ToString(), StartTicks = DateTime.FromFileTimeUtc(created).Ticks };
            }
            finally { CloseHandle(h); }
        }
        public static ProcessIdentity Read(int pid)
        {
            using (var search = new ManagementObjectSearcher("SELECT ProcessId,ParentProcessId,ExecutablePath,CommandLine FROM Win32_Process WHERE ProcessId=" + pid))
            using (var rows = search.Get())
            {
                foreach (ManagementObject row in rows)
                {
                    using (row)
                    {
                        var p = Basics(pid); p.ParentPid = Convert.ToInt32(row["ParentProcessId"]); p.CommandLine = Convert.ToString(row["CommandLine"]); return p;
                    }
                }
            }
            return null;
        }
        public static bool Same(ProcessIdentity a, ProcessIdentity b)
        {
            return a != null && b != null && a.Pid == b.Pid && a.StartTicks == b.StartTicks && String.Equals(a.Executable, b.Executable, StringComparison.OrdinalIgnoreCase) && a.CommandLine == b.CommandLine;
        }
        public static bool Alive(ProcessIdentity record)
        {
            try
            {
                var p = Basics(record.Pid); return p.StartTicks == record.StartTicks && String.Equals(p.Executable, record.Executable, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
        public static bool Matches(ProcessIdentity p, ServiceConfig s, ProjectPaths paths)
        {
            return p != null && !String.IsNullOrWhiteSpace(s.ExpectedExecutable) && !String.IsNullOrWhiteSpace(s.CommandContains) && String.Equals(p.Executable, paths.Resolve(s.ExpectedExecutable), StringComparison.OrdinalIgnoreCase) && p.CommandLine.IndexOf(s.CommandContains, StringComparison.OrdinalIgnoreCase) >= 0;
        }
        public static List<ProcessIdentity> Find(ServiceConfig s, ProjectPaths paths)
        {
            var result = new List<ProcessIdentity>();
            if (String.IsNullOrWhiteSpace(s.ExpectedExecutable) || String.IsNullOrWhiteSpace(s.CommandContains)) return result;
            string name = Path.GetFileName(paths.Resolve(s.ExpectedExecutable)).Replace("'", "''");
            using (var query = new ManagementObjectSearcher("SELECT ProcessId FROM Win32_Process WHERE Name='" + name + "'"))
            using (var rows = query.Get())
                foreach (ManagementObject row in rows) using (row)
                {
                    try { var p = Read(Convert.ToInt32(row["ProcessId"])); if (Matches(p, s, paths)) result.Add(p); } catch (ArgumentException) { }
                }
            return result;
        }
        public static List<ProcessIdentity> Descendants(List<ProcessIdentity> roots)
        {
            var found = new List<ProcessIdentity>();
            foreach (var root in roots.Where(x => Alive(x) || x.ExitTicks > x.StartTicks).ToArray())
            {
                using (var query = new ManagementObjectSearcher("SELECT ProcessId FROM Win32_Process WHERE ParentProcessId=" + root.Pid))
                using (var rows = query.Get()) foreach (ManagementObject row in rows) using (row)
                {
                    try { var child = Read(Convert.ToInt32(row["ProcessId"])); if (child != null && child.StartTicks >= root.StartTicks && ((root.ExitTicks > 0 && child.StartTicks <= root.ExitTicks) || Alive(root))) found.Add(child); } catch (ArgumentException) { }
                }
            }
            return found;
        }
    }
    public static class TcpOwners
    {
        [DllImport("iphlpapi.dll", SetLastError = true)] static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int type, uint reserved);
        public static List<int> ForProcess(int pid)
        {
            int size=0; uint code=GetExtendedTcpTable(IntPtr.Zero,ref size,false,2,3,0);
            if(code!=122&&code!=0)throw new InvalidOperationException(L.Text("无法查询工作台监听端口"));
            IntPtr buffer=Marshal.AllocHGlobal(Math.Max(size,4));
            try
            {
                if(GetExtendedTcpTable(buffer,ref size,false,2,3,0)!=0)throw new InvalidOperationException(L.Text("监听端口表变化，请重试"));
                var ports=new HashSet<int>(); int count=Marshal.ReadInt32(buffer);
                for(int i=0;i<count;i++) { IntPtr row=IntPtr.Add(buffer,4+i*24); if(Marshal.ReadInt32(row,20)==pid&&Marshal.ReadByte(row,4)==127&&Marshal.ReadByte(row,5)==0&&Marshal.ReadByte(row,6)==0&&Marshal.ReadByte(row,7)==1)ports.Add((Marshal.ReadByte(row,8)<<8)|Marshal.ReadByte(row,9)); }
                return ports.ToList();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        public static List<int> Get(int port)
        {
            var owners = new HashSet<int>();
            foreach (int family in new[] { 2, 23 })
            {
                int size = 0; uint code = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
                if (code != 122 && code != 0) throw new InvalidOperationException(L.Format("无法查询端口所有者（{0}）", code));
                IntPtr buffer = Marshal.AllocHGlobal(Math.Max(size, 4));
                try
                {
                    code = GetExtendedTcpTable(buffer, ref size, false, family, 3, 0);
                    if (code != 0) throw new InvalidOperationException(L.Format("端口表在变化，请稍后重试（{0}）", code));
                    int count = Marshal.ReadInt32(buffer), rowSize = family == 2 ? 24 : 56, portOffset = family == 2 ? 8 : 20, pidOffset = family == 2 ? 20 : 52;
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr row = IntPtr.Add(buffer, 4 + i * rowSize);
                        int number = (Marshal.ReadByte(row, portOffset) << 8) | Marshal.ReadByte(row, portOffset + 1);
                        if (number == port) owners.Add(Marshal.ReadInt32(row, pidOffset));
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return owners.ToList();
        }
    }
    public static class LocalHttp
    {
        public static Uri Validate(string address)
        {
            Uri u;
            if (!Uri.TryCreate(address, UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https") || !u.IsLoopback || !String.IsNullOrEmpty(u.UserInfo)) throw new InvalidDataException(L.Text("健康检查只支持无凭据的本机 HTTP/HTTPS 地址"));
            return u;
        }
        public static string Get(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(Validate(url)); req.Proxy = null; req.Timeout = 1500; req.ReadWriteTimeout = 1500; req.AllowAutoRedirect = false;
            using (var res = (HttpWebResponse)req.GetResponse())
            {
                if ((int)res.StatusCode < 200 || (int)res.StatusCode >= 300) throw new InvalidDataException(L.Format("HTTP 状态 {0}", (int)res.StatusCode));
                using (var stream = res.GetResponseStream()) using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    char[] data = new char[65537]; int offset = 0, read;
                    while (offset < data.Length && (read = reader.Read(data, offset, data.Length - offset)) > 0) offset += read;
                    if (offset > 65536) throw new InvalidDataException(L.Text("HTTP 响应过大"));
                    return new string(data, 0, offset);
                }
            }
        }
    }
}
