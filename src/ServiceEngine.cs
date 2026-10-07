using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LocalServiceManager
{
    public sealed class ServiceEngine : IDisposable
    {
        readonly ProjectPaths paths;
        readonly SemaphoreSlim operations = new SemaphoreSlim(1, 1);
        readonly object gate = new object();
        readonly Dictionary<string, ServiceSnapshot> snapshots = new Dictionary<string, ServiceSnapshot>();
        readonly Dictionary<string, List<ProcessIdentity>> owned = new Dictionary<string, List<ProcessIdentity>>();
        readonly Dictionary<string, List<string>> captures = new Dictionary<string, List<string>>();
        readonly HashSet<string> pending = new HashSet<string>();
        public readonly LogMaintenance Logs;
        public event Action Changed;
        public bool AnyBusy { get { lock (gate) return pending.Count > 0; } }
        public ServiceEngine(ProjectPaths paths)
        {
            this.paths = paths; Logs = new LogMaintenance(paths);
            try { if (File.Exists(paths.Owned)) foreach (var kv in Json.FileRead<OwnershipStore>(paths.Owned).Services) owned[kv.Key] = kv.Value; }
            catch { Logs.Event(L.Text("进程记录不可读，保持外部进程只读")); }
        }
        void Notify() { var cb = Changed; if (cb != null) cb(); }
        public ServiceSnapshot Snapshot(ServiceConfig s) { lock (gate) { ServiceSnapshot value; return snapshots.TryGetValue(s.Id, out value) ? value : new ServiceSnapshot { State = ServiceState.Unknown }; } }
        public static string ConfigurationSaveBlock(ServiceSnapshot value)
        {
            if (value == null) return "";
            if (value.Busy || value.State == ServiceState.Transition) return L.Text("服务正在启动、停止或重启，可查看和复制配置；操作完成并停止后可保存。");
            if (value.State == ServiceState.Running || value.Owned || value.Processes.Count > 0) return L.Text("服务正在运行，可查看和复制配置；服务停止后可编辑并保存。");
            return "";
        }
        public void CommitConfiguration(ServiceConfig current, Action commit)
        {
            // Inspect the existing binding again; serialize the commit against queued operations.
            var observed = Inspect(current);
            try
            {
                lock (gate)
                {
                    string blocked = ConfigurationSaveBlock(Snapshot(current));
                    if (pending.Contains(current.Id)) blocked = L.Text("服务有待执行操作，停止后可保存配置。");
                    if (blocked.Length > 0) throw new InvalidOperationException(blocked);
                    snapshots[current.Id] = observed;
                    blocked = ConfigurationSaveBlock(observed);
                    if (blocked.Length > 0) throw new InvalidOperationException(blocked);
                    commit();
                }
            }
            finally { Notify(); }
        }
        void Set(ServiceConfig s, ServiceSnapshot value) { lock (gate) snapshots[s.Id] = value; Notify(); }
        List<ProcessIdentity> Owned(string id) { lock (gate) { List<ProcessIdentity> value; return owned.TryGetValue(id, out value) ? value.ToList() : new List<ProcessIdentity>(); } }
        void Remember(string id, ProcessIdentity p)
        {
            lock (gate)
            {
                if (!owned.ContainsKey(id)) owned[id] = new List<ProcessIdentity>();
                if (!owned[id].Any(x => x.Pid == p.Pid && x.StartTicks == p.StartTicks)) owned[id].Add(p);
                var existing = owned[id].First(x => x.Pid == p.Pid && x.StartTicks == p.StartTicks); existing.ExitTicks = p.ExitTicks;
                if (owned[id].Count > 64) owned[id] = owned[id].Where(ProcessInspector.Alive).Concat(owned[id].Where(x => !ProcessInspector.Alive(x)).Reverse().Take(16)).Take(64).ToList();
                var store = new OwnershipStore(); foreach (var kv in owned) store.Services[kv.Key] = kv.Value;
                Json.AtomicWrite(paths.Owned, store);
            }
        }
        public List<string> CaptureFiles(ServiceConfig s)
        {
            lock (gate)
            {
                List<string> files; if (captures.TryGetValue(s.Id, out files)) return files.ToList();
            }
            return Directory.GetFiles(paths.Logs, s.Id + "-*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(6).ToList();
        }
        public ServiceSnapshot Inspect(ServiceConfig s)
        {
            if (s.ClientManaged) return new ServiceSnapshot { State = ServiceState.Unknown, Detail = L.Text("仅登记；所属客户端负责生命周期，不检测实际工具调用，不读取协议流。"), Owner = String.IsNullOrWhiteSpace(s.ClientOwner) ? L.Text("客户端托管") : L.Format("客户端托管 · {0}", s.ClientOwner) };
            var records = Owned(s.Id).Where(ProcessInspector.Alive).ToList();
            if (s.Adapter == "hermes-gateway") return HermesGateway.Inspect(s, paths, records);
            if (s.Adapter == LocalBridge.Adapter) return LocalBridge.Inspect(s, paths, records);
            if (s.Adapter == ImageWorkbench.Adapter)
            {
                var receipt = ImageWorkbench.Receipt(s, paths, 0);
                if (receipt != null && !records.Any(p => ProcessInspector.Same(p, receipt.Process))) { Remember(s.Id, receipt.Process); records = Owned(s.Id).Where(ProcessInspector.Alive).ToList(); }
                return ImageWorkbench.Inspect(s, paths, records);
            }
            bool hasIdentity = !String.IsNullOrWhiteSpace(s.ExpectedExecutable) && !String.IsNullOrWhiteSpace(s.CommandContains);
            var portIds = s.Port > 0 ? TcpOwners.Get(s.Port) : new List<int>();
            var matched = new List<ProcessIdentity>();
            if (s.Port > 0)
            {
                foreach (int pid in portIds)
                {
                    ProcessIdentity p;
                    try { p = ProcessInspector.Read(pid); }
                    catch { return new ServiceSnapshot { State = ServiceState.Unknown, Detail = L.Text("无法读取监听进程身份，拒绝操作") }; }
                    if (p != null && (ProcessInspector.Matches(p, s, paths) || (!hasIdentity && records.Any(x => ProcessInspector.Same(x, p))))) matched.Add(p);
                }
                if (portIds.Count > matched.Count) return new ServiceSnapshot { State = ServiceState.Error, Detail = L.Format("端口 {0} 被其他/无法确认的进程占用，拒绝启动和停止", s.Port), Owner = L.Text("端口冲突") };
            }
            else if (hasIdentity) matched = ProcessInspector.Find(s, paths);
            else matched = records;
            bool mine = matched.Count > 0 && matched.All(x => records.Any(r => ProcessInspector.Same(r, x)));
            var snapshot = new ServiceSnapshot { Processes = matched, Owned = mine, Owner = matched.Count == 0 ? "—" : mine ? L.Text("本工具管理") : L.Text("外部启动（只读）") };
            if (matched.Count > 1 && s.Port == 0) { snapshot.State = ServiceState.Unknown; snapshot.Detail = L.Text("身份匹配多个进程，请收紧命令特征"); return snapshot; }
            if (matched.Count == 0)
            {
                if (records.Count > 0) { snapshot.State = ServiceState.Error; snapshot.Detail = L.Text("启动进程仍存在，服务健康条件未满足"); snapshot.Owned = true; snapshot.Owner = L.Text("本工具管理"); snapshot.Processes = records; }
                else if (!hasIdentity && s.Port == 0) { snapshot.State = ServiceState.Unknown; snapshot.Detail = L.Text("请配置进程完整路径 + 命令特征；脚本退出不能证明服务停止"); }
                else { snapshot.State = ServiceState.Stopped; snapshot.Detail = L.Text("未检测到匹配进程"); }
                return snapshot;
            }
            snapshot.State = ServiceState.Running; snapshot.Detail = s.Port > 0 ? L.Format("进程身份已核实；端口 {0} 正在监听；PID {1}", s.Port, String.Join(",", matched.Select(x => x.Pid))) : L.Format("进程身份已核实；PID {0}", String.Join(",", matched.Select(x => x.Pid)));
            if (!String.IsNullOrWhiteSpace(s.HealthUrl))
            {
                try
                {
                    string body = LocalHttp.Get(s.HealthUrl);
                    if (s.Adapter == "comfy") { var data = Json.Read<Dictionary<string, object>>(body); if (data == null || !data.ContainsKey("system")) throw new InvalidDataException(L.Text("system_stats 格式无效")); }
                    snapshot.Detail = L.Format("{0}；HTTP 健康", snapshot.Detail);
                }
                catch { snapshot.State = ServiceState.Error; snapshot.Detail = L.Text("进程存在但 HTTP 未就绪/检查失败"); }
            }
            return snapshot;
        }
        public async Task RefreshAsync(IEnumerable<ServiceConfig> services)
        {
            await Task.Run(() =>
            {
                foreach (var service in services.ToArray())
                {
                    lock (gate) if (pending.Contains(service.Id)) continue;
                    ServiceSnapshot value;
                    try { value = Inspect(service); } catch { value = new ServiceSnapshot { State = ServiceState.Unknown, Detail = L.Text("检查失败：无法核实进程或端口，未执行任何控制操作") }; }
                    lock (gate) { if (pending.Contains(service.Id)) continue; snapshots[service.Id] = value; }
                }
                string[] files; lock (gate) files = captures.Values.SelectMany(x => x).ToArray();
                try { Logs.Maintain(files.Concat(ImageWorkbench.ActiveLogFiles(paths)).ToArray()); } catch (IOException) { }
                Notify();
            });
        }
        public async Task OperateAsync(ServiceConfig original, string action)
        {
            var s = original.Clone();
            lock (gate)
            {
                if (pending.Contains(s.Id)) throw new InvalidOperationException(L.Text("该服务已有操作，请等待"));
                pending.Add(s.Id); snapshots[s.Id] = new ServiceSnapshot { State = ServiceState.Transition, Detail = L.Format("等待串行执行：{0}", ActionLabel(action)), Busy = true, Owner = Snapshot(s).Owner };
            }
            Notify(); await operations.WaitAsync();
            try
            {
                await Task.Run(() =>
                {
                    ConfigRepository.Validate(new AppConfig { Services = new List<ServiceConfig> { s } });
                    if (!s.Reviewed) throw new InvalidOperationException(L.Text("导入条目须先编辑、核对并允许执行"));
                    if (s.ClientManaged) throw new InvalidOperationException(L.Text("stdio MCP 由客户端管理，不能独立运行或捕获协议"));
                    Logs.Event(L.Format("{0}：请求 {1}", s.Name, ActionLabel(action)));
                    if (action == "start") Start(s);
                    else if (action == "stop") Stop(s);
                    else if (action == "restart")
                    {
                        var before = Inspect(s);
                        if (!before.Owned && !before.ControllerValid) throw new InvalidOperationException(L.Text("只允许重启本工具管理或已核验专用控制器的服务"));
                        if (s.Adapter == ImageWorkbench.Adapter) ImageWorkbench.AssertRestartPossible(s, paths);
                        Stop(s); // Wait for process exit AND port release before any restart script.
                        if (!String.IsNullOrWhiteSpace(s.RestartPath)) Start(s, s.RestartPath, s.RestartArgs); else Start(s);
                    }
                    else throw new InvalidOperationException(L.Text("未知操作"));
                    var after = Inspect(s); Set(s, after); Logs.Event(L.Format("{0}：{1} / {2}", s.Name, after.Label, after.Detail));
                });
            }
            catch (Exception ex)
            {
                ServiceSnapshot current;
                try { current = Inspect(s); } catch { current = new ServiceSnapshot { State = ServiceState.Unknown }; }
                current.Detail = L.Format("{0}；当前：{1}", ex.Message, current.Label); Set(s, current); Logs.Event(L.Format("{0}：操作未完成 / {1}", s.Name, ex.Message)); throw;
            }
            finally { lock (gate) { pending.Remove(s.Id); var value = Snapshot(s); value.Busy = false; } operations.Release(); Notify(); }
        }
        static string ActionLabel(string action) { return action == "start" ? L.Text("启动") : action == "stop" ? L.Text("停止") : action == "restart" ? L.Text("重启") : action; }
        void Transition(ServiceConfig s, string detail) { Set(s, new ServiceSnapshot { State = ServiceState.Transition, Detail = detail, Busy = true, Owner = L.Text("本工具管理") }); }
        LaunchResult Launch(ServiceConfig s, string script, string args, string action)
        {
            if (s.Adapter == ImageWorkbench.Adapter) args += " -ServiceId " + s.Id;
            var launched = NativeLaunch.Start(s, paths, script, args, action);
            lock (gate) captures[s.Id] = new List<string> { launched.Stdout, launched.Stderr };
            return launched;
        }
        void Start(ServiceConfig s) { Start(s, s.StartPath, s.StartArgs); }
        void Start(ServiceConfig s, string path, string args)
        {
            var before = Inspect(s);
            if (before.State == ServiceState.Running) { Logs.Event(L.Format("{0}：已经运行，启动为无操作", s.Name)); return; }
            if (before.State != ServiceState.Stopped) throw new InvalidOperationException(L.Format("未证明服务已停止，拒绝重复启动：{0}", before.Detail));
            if (String.IsNullOrWhiteSpace(path)) throw new InvalidOperationException(L.Text("未设置启动入口"));
            if (String.IsNullOrWhiteSpace(s.ExpectedExecutable) || String.IsNullOrWhiteSpace(s.CommandContains)) throw new InvalidOperationException(L.Text("先配置服务进程的完整 EXE 路径和唯一命令特征"));
            Transition(s, L.Text("正在启动；等待进程、端口和 HTTP 就绪"));
            using (var launch = Launch(s, path, args, "start"))
            {
                Remember(s.Id, launch.Identity);
                DateTime deadline = DateTime.UtcNow.AddSeconds(s.TimeoutSeconds);
                DateTime externalSeen = DateTime.MinValue;
                while (DateTime.UtcNow < deadline)
                {
                    if (launch.Process.HasExited && launch.Identity.ExitTicks == 0) { launch.Identity.ExitTicks = launch.Process.ExitTime.ToUniversalTime().Ticks; Remember(s.Id, launch.Identity); }
                    TrackChildren(s);
                    TrackReceipt(s, launch.Identity.StartTicks);
                    var value = Inspect(s);
                    if (value.State == ServiceState.Running)
                    {
                        if (s.Adapter == ImageWorkbench.Adapter && ImageWorkbench.Receipt(s, paths, launch.Identity.StartTicks) == null) { Thread.Sleep(200); continue; }
                        if (!value.Owned && !value.ControllerValid)
                        {
                            if (externalSeen == DateTime.MinValue) externalSeen = DateTime.UtcNow;
                            if ((DateTime.UtcNow - externalSeen).TotalSeconds > 2) throw new InvalidOperationException(L.Text("出现外部服务，未接管；不会停止该进程"));
                        }
                        else return;
                    }
                    if (L.Canonical(value.Owner) == "端口冲突") throw new InvalidOperationException(value.Detail);
                    if (launch.Process.HasExited && launch.Process.ExitCode != 0) throw new InvalidOperationException(L.Format("启动脚本退出码 {0}；请查看 stderr 日志", launch.Process.ExitCode));
                    Thread.Sleep(400);
                }
                CancelScriptOnly(launch, s);
                throw new TimeoutException(L.Text("启动超时；不终止后台服务，请查看日志和当前状态。脚本退出不等于健康"));
            }
        }
        void TrackChildren(ServiceConfig s)
        {
            foreach (var child in ProcessInspector.Descendants(Owned(s.Id))) if (!Path.GetFileName(child.Executable).Equals("conhost.exe", StringComparison.OrdinalIgnoreCase)) Remember(s.Id, child);
        }
        void TrackReceipt(ServiceConfig s, long afterTicks)
        {
            if (s.Adapter == ImageWorkbench.Adapter) { var receipt = ImageWorkbench.Receipt(s, paths, afterTicks); if (receipt != null) Remember(s.Id, receipt.Process); return; }
            if (s.Adapter != "comfy" || String.IsNullOrEmpty(s.ReceiptPath) || !File.Exists(paths.Resolve(s.ReceiptPath))) return;
            try
            {
                var record = Json.FileRead<Dictionary<string, object>>(paths.Resolve(s.ReceiptPath));
                var p = ProcessInspector.Read(Convert.ToInt32(record["pid"]));
                if (p != null && p.StartTicks >= afterTicks && p.StartTicks == Convert.ToInt64(record["startTicks"]) && String.Equals(p.Executable, Convert.ToString(record["python"]), StringComparison.OrdinalIgnoreCase) && p.CommandLine == Convert.ToString(record["commandLine"]) && ProcessInspector.Matches(p, s, paths)) Remember(s.Id, p);
            }
            catch { /* Incomplete/stale receipt cannot authorize a stop. */ }
        }
        void ProtectQueue(ServiceConfig s)
        {
            if (s.Adapter != "comfy") return;
            if (String.IsNullOrWhiteSpace(s.HealthUrl)) throw new InvalidOperationException(L.Text("Comfy 队列保护需要本机健康地址"));
            try
            {
                string address = new Uri(s.HealthUrl).GetLeftPart(UriPartial.Authority) + "/queue";
                var data = Json.Read<Dictionary<string, object>>(LocalHttp.Get(address));
                if (data == null || !data.ContainsKey("queue_running") || !data.ContainsKey("queue_pending")) throw new InvalidDataException();
                var running = data["queue_running"] as ICollection; var waiting = data["queue_pending"] as ICollection;
                if (running == null || waiting == null) throw new InvalidDataException();
                if (running.Count + waiting.Count > 0) throw new InvalidOperationException(L.Text("ComfyUI 有运行/排队任务，已保护任务并取消停止或重启"));
            }
            catch (InvalidOperationException) { throw; }
            catch { throw new InvalidOperationException(L.Text("无法证明 ComfyUI 队列为空，已取消停止或重启")); }
        }
        void Stop(ServiceConfig s)
        {
            var before = Inspect(s);
            if (before.State == ServiceState.Stopped) return;
            if ((!before.Owned && !before.ControllerValid) || before.Processes.Count == 0 || L.Canonical(before.Owner) == "端口冲突" || ((s.Adapter == "hermes-gateway" || s.Adapter == LocalBridge.Adapter || s.Adapter == ImageWorkbench.Adapter) && !before.ControllerValid)) throw new InvalidOperationException(L.Format("进程来源或控制器身份不明确，拒绝停止：{0}", before.Detail));
            if (s.Adapter == ImageWorkbench.Adapter && before.TasksActive) throw new InvalidOperationException(L.Text("工作台有未完成任务，未停止或重启"));
            if (String.IsNullOrWhiteSpace(s.StopPath)) throw new InvalidOperationException(L.Text("未配置停止脚本；本工具不按进程名称批量结束进程"));
            ProtectQueue(s);
            foreach (var p in before.Processes) if (!ProcessInspector.Same(p, ProcessInspector.Read(p.Pid))) throw new InvalidOperationException(L.Text("进程身份已变化，取消停止"));
            if (s.Port > 0 && TcpOwners.Get(s.Port).Any(x => !before.Processes.Any(p => p.Pid == x))) throw new InvalidOperationException(L.Text("端口所有者已变化，取消停止"));
            Transition(s, L.Text("正在停止；等待身份匹配的进程退出并释放端口"));
            string stopArgs = s.StopArgs;
            if (s.Adapter == "hermes-gateway" || s.Adapter == LocalBridge.Adapter || s.Adapter == ImageWorkbench.Adapter) stopArgs += " -ExpectedPid " + before.Processes[0].Pid + " -ExpectedStartTicks " + before.Processes[0].StartTicks;
            using (var launch = Launch(s, s.StopPath, stopArgs, "stop"))
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(s.TimeoutSeconds);
                while (DateTime.UtcNow < deadline)
                {
                    bool stopped = before.Processes.All(x => !ProcessInspector.Alive(x)) && (s.Port == 0 || TcpOwners.Get(s.Port).Count == 0);
                    if (stopped && launch.Process.HasExited)
                    {
                        if (launch.Process.ExitCode != 0) throw new InvalidOperationException(L.Format("服务已停止，但停止脚本退出码 {0}", launch.Process.ExitCode));
                        // Also reject a replacement identity appearing before restart.
                        if (Inspect(s).State != ServiceState.Stopped) throw new InvalidOperationException(L.Text("服务出现替代进程，未继续重启"));
                        return;
                    }
                    if (launch.Process.HasExited && launch.Process.ExitCode != 0) throw new InvalidOperationException(L.Format("停止脚本退出码 {0}；服务可能仍在运行", launch.Process.ExitCode));
                    Thread.Sleep(300);
                }
                CancelScriptOnly(launch, s); throw new TimeoutException(L.Text("停止超时；未执行重启、未强杀服务"));
            }
        }
        void CancelScriptOnly(LaunchResult launch, ServiceConfig s)
        {
            // Cancel only the exact helper launched by this operation. No tree/name kill.
            try { if (!ProcessInspector.Matches(launch.Identity, s, paths) && !launch.Process.HasExited && ProcessInspector.Same(launch.Identity, ProcessInspector.Read(launch.Identity.Pid))) launch.Process.Kill(); } catch { }
        }
        public void Dispose() { /* Leaving services running is intentional; native file handles survive GUI exit. */ }
    }
}
