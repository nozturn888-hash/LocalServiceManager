using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace LocalServiceManager
{
    public sealed class ServiceConfig
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string Name = "新服务";
        public string Icon = "builtin:server";
        public string StartPath = "", StartArgs = "", StopPath = "", StopArgs = "", RestartPath = "", RestartArgs = "";
        public string WorkingDirectory = "", ExpectedExecutable = "", CommandContains = "";
        public int Port = 0;
        public string HealthUrl = "", LogPath = "", EncodingName = "utf-8", Adapter = "generic";
        public int TimeoutSeconds = 90;
        public bool AutoStart = false, Reviewed = true, ClientManaged = false;
        public string ReceiptPath = "";
        public string OpenType = "none", OpenTarget = "", OpenArgs = "", OpenWorkingDirectory = "";
        public bool OpenFollowWorkbenchPort = false;
        public string ClientOwner = "", ClientNotes = "";
        public ServiceConfig Clone() { return Json.Read<ServiceConfig>(Json.Write(this)); }
    }
    public sealed class AppConfig
    {
        public int Version = 1;
        public bool LoginStartup = false;
        public string Language = "zh-CN";
        public List<ServiceConfig> Services = new List<ServiceConfig>();
    }
    public sealed class ProcessIdentity
    {
        public int Pid, ParentPid;
        public long StartTicks, ExitTicks;
        public string Executable = "", CommandLine = "";
    }
    public sealed class OwnershipStore
    {
        public Dictionary<string, List<ProcessIdentity>> Services = new Dictionary<string, List<ProcessIdentity>>();
    }
    public enum ServiceState { Stopped, Running, Transition, Unknown, Error }
    public sealed class ServiceSnapshot
    {
        public ServiceState State;
        public string Detail = L.Text("尚未检查"), Owner = "—";
        public bool Owned, Busy, ControllerValid, TasksActive;
        public List<ProcessIdentity> Processes = new List<ProcessIdentity>();
        public string Label { get { return State == ServiceState.Running ? L.Text("运行") : State == ServiceState.Stopped ? L.Text("停止") : State == ServiceState.Transition ? L.Text("处理中") : State == ServiceState.Error ? L.Text("错误") : L.Text("未知"); } }
    }
    public static class Json
    {
        static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 1024 * 1024, RecursionLimit = 32 }; }
        public static string Write(object value) { return Serializer().Serialize(value); }
        public static T Read<T>(string value) { return Serializer().Deserialize<T>(value); }
        public static T FileRead<T>(string path) { if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException(L.Text("配置文件超过 1 MiB")); return Read<T>(File.ReadAllText(path, Encoding.UTF8)); }
        public static void AtomicWrite(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, Write(value), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak"); else File.Move(tmp, path);
        }
    }
    public sealed class ProjectPaths
    {
        public readonly string Root, Config, Logs, Icons, Owned;
        public ProjectPaths(string root)
        {
            Root = Path.GetFullPath(root); Config = Path.Combine(Root, "config", "services.json");
            Logs = Path.Combine(Root, "logs"); Icons = Path.Combine(Root, "icons"); Owned = Path.Combine(Root, "config", "owned.json");
            Directory.CreateDirectory(Path.GetDirectoryName(Config)); Directory.CreateDirectory(Logs); Directory.CreateDirectory(Icons);
        }
        public static void CheckPathText(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return;
            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.IndexOfAny(new[] { '*', '?', '"' }) >= 0) throw new ArgumentException(L.Text("普通路径不能含引号或通配符；日志的 * / ? 只能写在文件名中"));
            if (path.Length >= 2 && path[1] == ':' && (path.Length < 3 || (path[2] != '\\' && path[2] != '/'))) throw new ArgumentException(L.Text("盘符后需要斜杠，请使用 F:\\目录 形式或项目相对路径"));
            try { Path.GetFullPath(path); }
            catch (ArgumentException) { throw new ArgumentException(L.Text("路径格式无效，请检查引号、冒号或控制字符")); }
            catch (NotSupportedException) { throw new ArgumentException(L.Text("路径格式不受支持，请填写本机文件路径")); }
        }
        public string Resolve(string path) { if (String.IsNullOrWhiteSpace(path)) return ""; CheckPathText(path); return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Root, path)); }
    }
    public sealed class ConfigRepository
    {
        public readonly ProjectPaths Paths;
        public AppConfig Current;
        public ConfigRepository(ProjectPaths paths, bool defaults)
        {
            Paths = paths;
            if (File.Exists(paths.Config)) Current = Json.FileRead<AppConfig>(paths.Config);
            else { Current = new AppConfig(); if (defaults) AddExamples(); Save(); }
            Validate(Current);
        }
        // Public packages start empty and never discover private installations.
        void AddExamples() { }
        public void Save() { Validate(Current); Json.AtomicWrite(Paths.Config, Current); }
        public static void Validate(AppConfig config)
        {
            if (config == null || config.Version != 1 || config.Services == null || config.Services.Count > 100) throw new InvalidDataException(L.Text("配置版本或服务数量不受支持"));
            if (config.Language != "zh-CN" && config.Language != "en-US") throw new InvalidDataException(L.Text("界面语言无效；只支持中文或 English"));
            var ids = new HashSet<string>();
            foreach (var s in config.Services)
            {
                Guid id;
                if (s == null || !Guid.TryParseExact(s.Id, "N", out id) || !ids.Add(s.Id)) throw new InvalidDataException(L.Text("服务 ID 无效或重复"));
                if (s.Adapter == "hermes-gateway" || s.Adapter == LocalBridge.Adapter || s.Adapter == ImageWorkbench.Adapter) throw new InvalidDataException(PublicAdapterBoundary.Message);
                if (s.Adapter != "generic" && s.Adapter != "comfy" && s.Adapter != "stdio") throw new InvalidDataException(L.Text("适配模式无效"));
                if (String.IsNullOrWhiteSpace(s.Name) || s.Name.Length > 100) throw new InvalidDataException(L.Text("名称应为 1–100 个字符"));
                if (s.Port < 0 || s.Port > 65535 || s.TimeoutSeconds < 3 || s.TimeoutSeconds > 600) throw new InvalidDataException(L.Text("端口或超时无效（超时 3–600 秒）"));
                if (!String.IsNullOrWhiteSpace(s.HealthUrl)) LocalHttp.Validate(s.HealthUrl);
                if (s.Adapter == "comfy")
                {
                    if (s.ClientManaged) throw new InvalidDataException(L.Text("适配模式无效"));
                    if (String.IsNullOrWhiteSpace(s.HealthUrl)) throw new InvalidDataException(L.Text("Comfy 队列保护需要本机健康地址"));
                    var health = new Uri(s.HealthUrl);
                    if (s.Port < 1 || health.Port != s.Port) throw new InvalidDataException(L.Text("ComfyUI 队列保护要求本机健康地址的端口与监听端口一致。"));
                }
                foreach (string a in new[] { s.StartArgs, s.StopArgs, s.RestartArgs }) if (a == null || a.Length > 8192 || a.Contains('\r') || a.Contains('\n')) throw new InvalidDataException(L.Text("参数过长或含换行"));
                foreach (string p in new[] { s.StartPath, s.StopPath, s.RestartPath, s.WorkingDirectory, s.ExpectedExecutable, s.ReceiptPath, s.LogPath }) if (p == null || p.Length > 2048 || p.Contains('\r') || p.Contains('\n')) throw new InvalidDataException(L.Text("路径无效"));
                string[] pathValues = { s.StartPath, s.StopPath, s.RestartPath, s.WorkingDirectory, s.ExpectedExecutable, s.ReceiptPath };
                string[] pathNames = { L.Text("启动入口"), L.Text("停止入口"), L.Text("重启入口"), L.Text("工作目录"), L.Text("服务 EXE"), L.Text("控制器进程凭据") };
                for (int i = 0; i < pathValues.Length; i++) try { ProjectPaths.CheckPathText(pathValues[i]); } catch (Exception ex) { throw new InvalidDataException(L.Format("{0} · {1}：{2}", s.Name, pathNames[i], ex.Message)); }
                try { TailReader.ValidateExpression(s.LogPath); } catch (Exception ex) { throw new InvalidDataException(L.Format("{0} · 日志路径：{1}", s.Name, ex.Message)); }
                if (s.Icon == null || s.Icon.Length > 2048 || (!s.Icon.StartsWith("builtin:") && (Path.IsPathRooted(s.Icon) || s.Icon.Contains("..") || !s.Icon.StartsWith("icons/")))) throw new InvalidDataException(L.Text("图标必须是内置图标或项目内 icons/ 文件"));
                if (s.Adapter == "stdio") s.ClientManaged = true;
                if (s.ClientManaged) s.AutoStart = false;
                ServiceOpener.Validate(s);
                try { Encoding.GetEncoding(s.EncodingName); } catch (ArgumentException) { throw new InvalidDataException(L.Format("日志编码无效：{0}", s.EncodingName)); }
            }
        }
        public string Export()
        {
            var safe = new AppConfig { Language = Current.Language };
            foreach (var original in Current.Services)
            {
                var s = original.Clone();
                // Arguments may contain secrets. Export deliberately excludes all arguments and runtime receipts.
                s.StartArgs = s.StopArgs = s.RestartArgs = ""; s.CommandContains = ""; s.ReceiptPath = ""; s.AutoStart = false; s.Reviewed = false;
                s.OpenArgs = ""; s.ClientNotes = "";
                if (s.OpenType == "url" && !String.IsNullOrWhiteSpace(s.OpenTarget)) s.OpenTarget = ServiceOpener.WebUri(s.OpenTarget).GetLeftPart(UriPartial.Path);
                if (!String.IsNullOrEmpty(s.HealthUrl)) s.HealthUrl = new Uri(s.HealthUrl).GetLeftPart(UriPartial.Path);
                // A private icon is not embedded or read by import/export.
                if (!s.Icon.StartsWith("builtin:")) s.Icon = "builtin:server";
                safe.Services.Add(s);
            }
            return Json.Write(safe);
        }
        public List<ServiceConfig> ReadImport(string path)
        {
            var incoming = Json.FileRead<AppConfig>(path); Validate(incoming);
            foreach (var s in incoming.Services)
            {
                s.Id = Guid.NewGuid().ToString("N"); s.AutoStart = false; s.Reviewed = false; s.ReceiptPath = "";
                if (!s.Icon.StartsWith("builtin:")) s.Icon = "builtin:server";
            }
            return incoming.Services;
        }
    }
    public interface IStartupStore { void Set(bool enabled, string executable); bool IsEnabled(string executable); }
    public sealed class WindowsStartupStore : IStartupStore
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "LocalServiceManager";
        public void Set(bool enabled, string executable)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(Key))
            {
                string existing = key.GetValue(Name) as string;
                if (existing != null && !existing.StartsWith(CommandLine.Quote(executable), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(L.Text("同名登录项属于其他路径，未修改"));
                if (enabled) key.SetValue(Name, CommandLine.Quote(executable) + " --tray", RegistryValueKind.String);
                else
                {
                    key.DeleteValue(Name, false);
                }
            }
        }
        public bool IsEnabled(string executable)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(Key)) return key != null && String.Equals(key.GetValue(Name) as string, CommandLine.Quote(executable) + " --tray", StringComparison.OrdinalIgnoreCase);
        }
    }
}
