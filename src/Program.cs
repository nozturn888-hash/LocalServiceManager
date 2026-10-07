using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace LocalServiceManager
{
    internal static class Program
    {
        [STAThread] static void Main(string[] args)
        {
            if (Array.IndexOf(args, "--workbench-control") >= 0) { Environment.ExitCode = ImageWorkbench.RunControl(args); return; }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string root = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory); bool tray = Array.IndexOf(args, "--tray") >= 0, defaults = true;
            for (int i = 0; i < args.Length; i++) if (args[i] == "--data-dir" && i + 1 < args.Length) { root = Path.GetFullPath(args[++i]); defaults = false; }
            try { L.LoadPreference(root); } catch(Exception ex) { L.MessageBox(ex.Message,L.ProductName); return; }
            string mutexName = "Local\\LocalServiceManager-" + Convert.ToBase64String(System.Security.Cryptography.SHA256.Create().ComputeHash(System.Text.Encoding.UTF8.GetBytes(root.ToLowerInvariant()))).Replace("/", "_").Replace("+", "-");
            bool first; using (var mutex = new Mutex(true, mutexName, out first))
            {
                if (!first) { L.MessageBox(L.Text("此项目已有一个管理器实例，请从系统托盘打开。"), L.ProductName); return; }
                try
                {
                    var paths = new ProjectPaths(root); var repository = new ConfigRepository(paths, defaults); using (var engine = new ServiceEngine(paths)) using (var form = new MainForm(repository, engine, new WindowsStartupStore(), tray, Array.IndexOf(args, "--no-service-autostart") < 0)) Application.Run(form);
                }
                catch (Exception ex) { L.MessageBox(L.Format("无法启动：{0}\r\n配置未被覆盖。请检查项目 config 目录和 services.json.bak。",L.Dynamic(ex.Message)), L.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error); }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
