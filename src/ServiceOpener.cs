using System;
using System.Diagnostics;
using System.IO;

namespace LocalServiceManager
{
    // Navigation never invokes service lifecycle operations.
    public static class ServiceOpener
    {
        public static Uri WebUri(string target)
        {
            Uri uri;
            if (!Uri.TryCreate(target, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https") || !String.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidDataException(L.Text("网页入口需为无账号密码的 http:// 或 https:// 地址。"));
            return uri;
        }
        public static void Validate(ServiceConfig s)
        {
            if (s.OpenType != "none" && s.OpenType != "url" && s.OpenType != "application") throw new InvalidDataException(L.Text("打开入口类型不受支持。"));
            foreach (string value in new[] { s.OpenTarget, s.OpenArgs, s.OpenWorkingDirectory, s.ClientOwner, s.ClientNotes })
                if (value == null || value.Length > 8192 || value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0) throw new InvalidDataException(L.Text("打开入口或登记信息过长或含换行。"));
            if (s.OpenFollowWorkbenchPort && (s.Adapter != ImageWorkbench.Adapter || s.OpenType != "url")) throw new InvalidDataException(L.Text("只有工作台网页入口可跟随工作台端口。"));
            if (s.OpenType == "url" && !s.OpenFollowWorkbenchPort && !String.IsNullOrWhiteSpace(s.OpenTarget)) WebUri(s.OpenTarget);
            if (s.OpenType == "application")
            {
                ProjectPaths.CheckPathText(s.OpenTarget); ProjectPaths.CheckPathText(s.OpenWorkingDirectory);
                if (!String.IsNullOrWhiteSpace(s.OpenTarget) && !Path.GetExtension(s.OpenTarget).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(L.Text("本机应用入口请选择 .exe 文件。"));
            }
        }
        public static ProcessStartInfo Build(ServiceConfig s, ProjectPaths paths, ServiceSnapshot snapshot = null)
        {
            Validate(s);
            if (s.OpenType == "none" || (!s.OpenFollowWorkbenchPort && String.IsNullOrWhiteSpace(s.OpenTarget)))
                throw new InvalidOperationException(L.Text("尚未设置打开入口。请在“配置 → 打开入口设置”中设置网页或本机应用；本次没有启动服务。"));
            if (s.OpenType == "url")
            {
                string target = s.OpenTarget;
                if (s.OpenFollowWorkbenchPort)
                {
                    int port = ImageWorkbench.DesiredPort(ImageWorkbench.Binding(paths));
                    if (snapshot != null && snapshot.State == ServiceState.Running && snapshot.Processes.Count == 1 && ProcessInspector.Alive(snapshot.Processes[0]))
                    {
                        var ports = TcpOwners.ForProcess(snapshot.Processes[0].Pid); if (ports.Count == 1) port = ports[0];
                    }
                    target = "http://127.0.0.1:" + port + "/";
                }
                return new ProcessStartInfo(WebUri(target).AbsoluteUri) { UseShellExecute = true };
            }
            string executable = paths.Resolve(s.OpenTarget);
            if (!File.Exists(executable)) throw new FileNotFoundException(L.Text("打开入口的应用程序不存在，请检查配置。"), executable);
            string directory = String.IsNullOrWhiteSpace(s.OpenWorkingDirectory) ? Path.GetDirectoryName(executable) : paths.Resolve(s.OpenWorkingDirectory);
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(L.Text("打开入口的工作目录不存在，请检查配置。"));
            return new ProcessStartInfo(executable, s.OpenArgs) { WorkingDirectory = directory, UseShellExecute = true };
        }
        public static void Open(ServiceConfig s, ProjectPaths paths, ServiceSnapshot snapshot = null, Action<ProcessStartInfo> launch = null)
        {
            var request = Build(s, paths, snapshot);
            if (launch != null) launch(request); else using (var process = Process.Start(request)) { }
        }
    }
}
