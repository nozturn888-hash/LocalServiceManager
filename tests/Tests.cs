using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LocalServiceManager
{
    public sealed class FakeStartupStore : IStartupStore
    {
        public bool Enabled; public int Writes;
        public void Set(bool enabled, string executable) { Enabled = enabled; Writes++; }
        public bool IsEnabled(string executable) { return Enabled; }
    }
    public static class Tests
    {
        static string run, fixture;
        static readonly List<string> results = new List<string>();
        static readonly List<ServiceConfig> cleanup = new List<ServiceConfig>();
        static void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL " + label); results.Add("PASS " + label); Console.WriteLine(results.Last()); }
        static void Expect(Action action, string label) { bool failed = false; try { action(); } catch { failed = true; } Check(failed, label); }
        static int Port() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
        static ServiceConfig Service(string name)
        {
            int port = Port(); string token = Guid.NewGuid().ToString("N"), queue = Path.Combine(run, name + "-queue.txt");
            var s = new ServiceConfig { Name = name, StartPath = fixture, StartArgs = "--serve --port " + port + " --token " + token + " --text " + CommandLine.Quote("中文 空格 参数") + " --queue-file " + CommandLine.Quote(queue) + " --stop-delay 350 --events " + CommandLine.Quote(Path.Combine(run, "events.txt")), StopPath = fixture, StopArgs = "--stop --port " + port + " --token " + token, WorkingDirectory = Path.GetDirectoryName(fixture), ExpectedExecutable = fixture, CommandContains = token, Port = port, HealthUrl = "http://127.0.0.1:" + port + "/health", TimeoutSeconds = 12 };
            cleanup.Add(s); return s;
        }
        static void Operate(ServiceEngine engine, ServiceConfig s, string action) { engine.OperateAsync(s, action).GetAwaiter().GetResult(); }
        [STAThread] public static int Main(string[] args)
        {
            run = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(run);
            string spaced = Path.Combine(run, "中文 路径"); Directory.CreateDirectory(spaced); fixture = Path.Combine(spaced, "测试 ServiceFixture.exe"); File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ServiceFixture.exe"), fixture);
            var paths = new ProjectPaths(run);
            using (var engine = new ServiceEngine(paths))
            {
                try
                {
                    if (Array.IndexOf(args, "--ui-only") < 0) Core(paths, engine); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException); Ui(paths, engine);
                    results.Add("All checks passed. Real ComfyUI/Hermes and Windows startup were untouched.");
                    File.WriteAllLines(Path.Combine(run, "results.txt"), results, Encoding.UTF8); Console.WriteLine("RESULT " + Path.Combine(run, "results.txt")); return 0;
                }
                catch (Exception ex) { Console.WriteLine(ex); results.Add(ex.ToString()); File.WriteAllLines(Path.Combine(run, "results.txt"), results, Encoding.UTF8); return 1; }
                finally
                {
                    foreach (var s in cleanup) try { if (TcpOwners.Get(s.Port).Count > 0) using (var launch = NativeLaunch.Start(s, paths, fixture, s.StopArgs, "cleanup")) launch.Process.WaitForExit(8000); } catch { }
                }
            }
        }
        static void Core(ProjectPaths paths, ServiceEngine engine)
        {
            var repo = new ConfigRepository(paths, false); Check(!repo.Current.LoginStartup && repo.Current.Services.Count == 0, "defaults: login/service startup disabled");
            var fresh = new ConfigRepository(new ProjectPaths(Path.Combine(run, "public-defaults")), true);
            Check(!fresh.Current.LoginStartup && fresh.Current.Services.Count == 0, "public first run stays empty with every startup switch off");
            foreach (string adapter in new[] { "hermes-gateway", "local-bridge", "image-workbench" })
            {
                string input = Path.Combine(run, "private-mode-" + adapter + ".json");
                File.WriteAllText(input, Json.Write(new AppConfig { Services = new List<ServiceConfig> { new ServiceConfig { Name = "private import", Adapter = adapter } } }));
                Expect(() => repo.ReadImport(input), "private adapter import rejected: " + adapter);
            }
            var draft = ServiceTemplates.Create("ComfyUI", paths);
            Check(!draft.AutoStart && !draft.Reviewed && draft.StartPath == "" && draft.StopPath == "" && draft.ExpectedExecutable == "", "public Comfy template prefill has no installation, execution or startup");
            draft.Port = Port(); draft.HealthUrl = "http://127.0.0.1:" + draft.Port + "/system_stats"; draft.StartPath = fixture; draft.StopPath = fixture; draft.ExpectedExecutable = fixture;
            ServiceTemplates.ValidateNewDraft(draft, paths);
            Check(true, "public Comfy template accepts user scripts and another local port");
            draft.HealthUrl = "http://127.0.0.1:" + (draft.Port == 65535 ? 65534 : draft.Port + 1) + "/system_stats";
            Expect(() => ServiceTemplates.ValidateNewDraft(draft, paths), "Comfy queue safety rejects mismatched health and listener ports");
            var s = Service("生命周期"); repo.Current.Services.Add(s); repo.Save();
            Check(engine.Inspect(s).State == ServiceState.Stopped, "stopped state before launch");
            Operate(engine, s, "start"); var first = engine.Inspect(s); Check(first.State == ServiceState.Running && first.Owned, "launch + exact process/port/HTTP identity");
            int pid = first.Processes[0].Pid; using (var p = Process.GetProcessById(pid)) Check(p.MainWindowHandle == IntPtr.Zero, "background launch without console window");
            Operate(engine, s, "start"); Check(engine.Inspect(s).Processes[0].Pid == pid, "duplicate start is no-op, never restart");
            var capture = engine.CaptureFiles(s); var tail = new TailReader(); tail.Read(capture[0], "utf-8"); Check(tail.Buffer.Text("").Contains("中文 空格 参数"), "Chinese + space path/argument stdout"); tail.Read(capture[1], "utf-8"); Check(tail.Buffer.Text("").Contains("stderr 中文"), "stderr capture");
            var impostor = s.Clone(); impostor.Id = Guid.NewGuid().ToString("N"); Check(engine.Inspect(impostor).State == ServiceState.Running && !engine.Inspect(impostor).Owned, "matching external process is read-only"); Expect(() => Operate(engine, impostor, "stop"), "external stop refused");
            var conflicting = s.Clone(); conflicting.Id = Guid.NewGuid().ToString("N"); conflicting.CommandContains = "different-command-token"; Check(engine.Inspect(conflicting).Owner == "端口冲突", "foreign port owner detected"); Expect(() => Operate(engine, conflicting, "start"), "port conflict launch refused");
            using (var restored = new ServiceEngine(paths)) Check(restored.Inspect(s).Owned, "ownership survives manager restart after exact validation");
            var store = Json.FileRead<OwnershipStore>(paths.Owned); foreach (var p in store.Services[s.Id]) p.StartTicks++; Json.AtomicWrite(paths.Owned, store);
            using (var stale = new ServiceEngine(paths)) { Check(!stale.Inspect(s).Owned, "stale PID/start-time record never authorizes ownership"); Expect(() => Operate(stale, s, "stop"), "stale identity stop refused"); }
            Operate(engine, s, "restart"); var next = engine.Inspect(s); Check(next.State == ServiceState.Running && next.Processes[0].Pid != pid, "restart waits exit and port release");
            string[] events = File.ReadAllLines(Path.Combine(run, "events.txt")); Check(events.Length >= 3 && events[0].StartsWith("start ") && events[1].StartsWith("stop ") && events[2].StartsWith("start "), "restart lifecycle event order");
            var noStop = s.Clone(); noStop.StopArgs = "--noop --token " + s.CommandContains; noStop.TimeoutSeconds = 3; int unchanged = next.Processes[0].Pid; Expect(() => Operate(engine, noStop, "restart"), "stop timeout prevents restart"); Check(engine.Inspect(s).Processes[0].Pid == unchanged, "timeout leaves service alive");
            string huge = engine.CaptureFiles(s).First(x => x.EndsWith("stdout.log")); // no-op capture, harmless inactive helper log
            File.AppendAllText(huge, new string('x', 3 * 1024 * 1024)); LogMaintenance.Rotate(huge); Check(new FileInfo(huge).Length == 0 && new FileInfo(huge + ".1").Length == LogMaintenance.Limit, "bounded log rotation + archive");
            Operate(engine, s, "stop"); Check(engine.Inspect(s).State == ServiceState.Stopped && TcpOwners.Get(s.Port).Count == 0, "stop verifies process exit and released port");
            var detached = Service("分离CMD"); string cmd = Path.Combine(spacedDirectory(), "快速分离 启动.cmd"); File.WriteAllText(cmd, "@echo off\r\nstart \"\" /b " + CommandLine.Quote(fixture) + " " + detached.StartArgs + "\r\nexit /b 0\r\n", Encoding.GetEncoding("gb18030")); detached.StartPath = cmd; detached.StartArgs = "";
            Operate(engine, detached, "start"); Check(engine.Inspect(detached).Owned, "fast detached CMD child validated by parent identity + lifetime"); Operate(engine, detached, "stop");
            var ps = Service("PowerShell"); string psPath = Path.Combine(spacedDirectory(), "后台 中文.ps1"); string psScript = "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)\r\n& '" + fixture.Replace("'", "''") + "' " + ps.StartArgs + "\r\nexit $LASTEXITCODE\r\n"; File.WriteAllText(psPath, psScript, new UTF8Encoding(true)); ps.StartPath = psPath; ps.StartArgs = ""; Operate(engine, ps, "start"); Check(engine.Inspect(ps).Owned, "PowerShell hidden launch with Unicode space path"); Operate(engine, ps, "stop");
            var failure = Service("脚本失败"); failure.StartArgs = "--fail --token " + failure.CommandContains; Expect(() => Operate(engine, failure, "start"), "nonzero script exit fails launch rather than marking healthy"); Check(engine.Inspect(failure).State == ServiceState.Stopped, "failed launcher never marked running");
            var comfy = Service("模拟Comfy"); comfy.Adapter = "comfy"; Operate(engine, comfy, "start"); string queue = Path.Combine(run, "模拟Comfy-queue.txt"); File.WriteAllText(queue, "busy"); Expect(() => Operate(engine, comfy, "stop"), "Comfy running queue protects stop"); Check(engine.Inspect(comfy).State == ServiceState.Running, "queue protection leaves tasks running"); File.WriteAllText(queue, "broken"); Expect(() => Operate(engine, comfy, "restart"), "unknown queue protects restart"); File.WriteAllText(queue, ""); Operate(engine, comfy, "stop");
            var other = Service("串行B"); var a = Service("串行A"); Task.WhenAll(engine.OperateAsync(a, "start"), engine.OperateAsync(other, "start")).GetAwaiter().GetResult(); Check(engine.Inspect(a).Owned && engine.Inspect(other).Owned, "two service operations serialize successfully"); var stopping = engine.OperateAsync(a, "stop"); Expect(() => Operate(engine, a, "start"), "same-service overlapping operation refused"); stopping.GetAwaiter().GetResult(); Operate(engine, other, "stop");
            var stdio = new ServiceConfig { Name = "stdio", Adapter = "stdio", ClientManaged = true, AutoStart = true }; Expect(() => Operate(engine, stdio, "start"), "stdio cannot start or capture protocol");
            var unknown = new ServiceConfig { Name = "缺少身份" }; Check(engine.Inspect(unknown).State == ServiceState.Unknown, "script exit is never a health proof"); Expect(() => Operate(engine, unknown, "start"), "unknown health prevents duplicate-risk launch");
            string chunks = Path.Combine(run, "分段 中文.log"); byte[] bytes = Encoding.UTF8.GetBytes("中文日志\n"); File.WriteAllBytes(chunks, bytes.Take(4).ToArray()); var split = new TailReader(); split.Read(chunks, "utf-8"); using (var output = new FileStream(chunks, FileMode.Append)) output.Write(bytes, 4, bytes.Length - 4); split.Read(chunks, "utf-8"); Check(split.Buffer.Text("").Contains("中文日志"), "UTF-8 multi-byte boundary decoder");
            string gb = Path.Combine(run, "GB中文.log"); File.WriteAllText(gb, "编码测试\n", Encoding.GetEncoding("gb18030")); var gbTail = new TailReader(); gbTail.Read(gb, "gb18030"); Check(gbTail.Buffer.Text("").Contains("编码测试"), "GB18030 Chinese file tail");
            var buffer = new LogBuffer(); buffer.Add(new string('z', 4 * 1024 * 1024)); Check(buffer.Text("").Length <= 512 * 1024, "log viewer cache bounded to 512 KiB");
            s.StartArgs = "--api-key FAKE-EXPORT-SECRET"; s.HealthUrl += "?token=FAKE-HTTP-SECRET"; s.AutoStart = true; repo.Current.LoginStartup = true; string exported = repo.Export(); Check(!exported.Contains("FAKE-EXPORT-SECRET") && !exported.Contains("FAKE-HTTP-SECRET"), "export excludes arguments and URL query secrets"); string exportPath = Path.Combine(run, "export.json"); File.WriteAllText(exportPath, exported); var imported = repo.ReadImport(exportPath); Check(imported.Count == 1 && !imported[0].AutoStart && !imported[0].Reviewed && imported[0].Id != s.Id, "import new identity, startup off and execution gated"); Expect(() => Operate(engine, imported[0], "start"), "import never auto-executes");
            var fake = new FakeStartupStore(); fake.Set(true, "fixture.exe"); Check(fake.Enabled && fake.Writes == 1 && !imported[0].AutoStart, "login startup mock independent of per-service startup"); fake.Set(false, "fixture.exe"); Check(!fake.Enabled, "startup removal mocked, real registry untouched");
            repo.Current.LoginStartup = false; repo.Current.Services.Clear(); repo.Save();
            using (var extracted = IconLibrary.AppIcon()) Check(extracted.Width > 0, "code-drawn built-in application icon");
            var icons = IconLibrary.Extract(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shell32.dll")); Check(icons.Count > 1, "multi-icon EXE/DLL resource extraction without execution"); foreach (var bitmap in icons) bitmap.Dispose();
        }
        static string spacedDirectory() { return Path.GetDirectoryName(fixture); }
        static IEnumerable<Control> All(Control c) { foreach (Control child in c.Controls) { yield return child; foreach (var nested in All(child)) yield return nested; } }
        static void Screenshot(Form form, string name)
        {
            using (var bitmap = new Bitmap(form.Width, form.Height)) { using (var g = Graphics.FromImage(bitmap)) g.CopyFromScreen(form.Location, Point.Empty, form.Size); bitmap.Save(Path.Combine(run, name + ".png")); }
        }
        static async Task Wait(Func<bool> condition)
        {
            DateTime end = DateTime.UtcNow.AddSeconds(15); while (!condition() && DateTime.UtcNow < end) await Task.Delay(150); if (!condition()) throw new Exception("UI wait timeout"); await Task.Delay(250);
        }
        static void Ui(ProjectPaths paths, ServiceEngine engine)
        {
            var repo = new ConfigRepository(paths, false); var s = Service("隔离服务 · 界面验收"); repo.Current.Services.Add(s); repo.Current.Services.Add(new ServiceConfig { Name = "Hermes MCP · 客户端管理", Adapter = "stdio", ClientManaged = true, Icon = "builtin:mcp" }); repo.Save(); var fake = new FakeStartupStore(); Exception failure = null; string capture = ""; long length = 0;
            using (var form = new MainForm(repo, engine, fake, false))
            {
                Console.WriteLine("UI form constructed");
                form.Shown += async (o, e) =>
                {
                    Console.WriteLine("UI form shown");
                    try
                    {
                        await form.RefreshNow(); await Task.Delay(700); Check(form.StartButton.Enabled && !form.StopButton.Enabled, "GUI initial action availability"); Screenshot(form, "ui-stopped");
                        using (var editor = new ServiceEditor(paths, ServiceEditor.Prefill(Path.Combine(spacedDirectory(), "拖入启动.cmd"))))
                        {
                            editor.Show(form); await Task.Delay(400); Check(engine.Inspect(s).State == ServiceState.Stopped && editor.Result == null, "drag prefill opens editor without execution"); Screenshot(editor, "ui-editor");
                            var name = All(editor).OfType<TextBox>().First(x => x.Text == "拖入启动"); name.Text = "手工条目"; All(editor).OfType<Button>().First(x => x.Text == "保存配置").PerformClick(); Check(editor.Result != null && editor.Result.Name == "手工条目" && !editor.Result.AutoStart, "editor save is configuration-only with startup off");
                        }
                        form.BuildTray(); var item = form.TrayMenu.Items.OfType<ToolStripMenuItem>().First(x => x.Text.StartsWith(s.Name)); var start = item.DropDownItems.OfType<ToolStripMenuItem>().First(x => x.Text == "启动"); start.PerformClick(); await Wait(() => !engine.AnyBusy && engine.Snapshot(s).State == ServiceState.Running); Check(form.StopButton.Enabled && !form.StartButton.Enabled, "tray start updates GUI health status"); Screenshot(form, "ui-running");
                        int pid = engine.Inspect(s).Processes[0].Pid; form.RestartButton.PerformClick(); await Wait(() => !engine.AnyBusy && engine.Snapshot(s).State == ServiceState.Running && engine.Snapshot(s).Processes[0].Pid != pid); Check(true, "GUI restart button waits full lifecycle");
                        var search = All(form).OfType<TextBox>().First(); var view = All(form).OfType<RichTextBox>().First(); var paused = All(form).OfType<CheckBox>().First(x => x.Text == "暂停滚动");
                        search.Text = "隔离服务"; Check(view.Text.Contains("隔离服务"), "GUI log search filters existing text"); All(form).OfType<Button>().First(x => x.Text == "复制日志").PerformClick(); Check(Clipboard.GetText().Contains("隔离服务"), "GUI log copy uses native clipboard");
                        paused.Checked = true; string frozen = view.Text; File.AppendAllText(Path.Combine(paths.Logs, "manager.log"), "隔离服务 PAUSE-MARKER\n", Encoding.UTF8); await Task.Delay(1100); Check(view.Text == frozen, "GUI pause holds log view"); paused.Checked = false; await Task.Delay(1100); Check(view.Text.Contains("PAUSE-MARKER"), "GUI resume catches file tail"); search.Text = "";
                        form.BuildTray(); form.TrayMenu.Show(form, new Point(660, 50)); await Task.Delay(350); Screenshot(form, "ui-tray-menu"); form.TrayMenu.Close();
                        form.Close(); await Task.Delay(300); Check(!form.Visible && !form.IsDisposed, "window close hides to live tray"); form.ShowMain(); await Task.Delay(300); Check(form.Visible, "tray restores main window");
                        form.BuildTray(); item = form.TrayMenu.Items.OfType<ToolStripMenuItem>().First(x => x.Text.StartsWith(s.Name)); item.DropDownItems.OfType<ToolStripMenuItem>().First(x => x.Text == "停止").PerformClick(); await Wait(() => !engine.AnyBusy && engine.Snapshot(s).State == ServiceState.Stopped); Check(true, "tray stop button verifies stopped state");
                        form.StartButton.PerformClick(); await Wait(() => !engine.AnyBusy && engine.Snapshot(s).State == ServiceState.Running); capture = engine.CaptureFiles(s).First(x => x.EndsWith("stdout.log")); length = new FileInfo(capture).Length;
                        Check(fake.Writes == 0, "GUI QA never touches login startup persistence"); await form.ExitAsync(false);
                    }
                    catch (Exception ex) { failure = ex; }
                    if (failure != null) await form.ExitAsync(false);
                };
                Application.Run(form);
            }
            if (failure != null) throw failure;
            System.Threading.Thread.Sleep(1100); Check(engine.Inspect(s).State == ServiceState.Running && new FileInfo(capture).Length > length, "exit tool preserves running service and stdout file handle"); Operate(engine, s, "stop");
            var stoppedRepo = new ConfigRepository(paths, false); stoppedRepo.Current.Services.Clear(); var stopExit = Service("退出前停止"); stoppedRepo.Current.Services.Add(stopExit); Operate(engine, stopExit, "start");
            using (var form = new MainForm(stoppedRepo, engine, fake, false)) { form.Shown += async (o, e) => { await Task.Delay(400); await form.ExitAsync(true); }; Application.Run(form); }
            Check(engine.Inspect(stopExit).State == ServiceState.Stopped, "stop-managed-services-then-exit semantics");
        }
    }
}
