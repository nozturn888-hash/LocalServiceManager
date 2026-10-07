using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LocalServiceManager
{
    // Private adapters are absent from public packages. This boundary retains source
    // compatibility without discovering, adopting, logging or controlling a service.
    public static class PublicAdapterBoundary
    {
        public static string Message { get { return L.Text("此公开版本不提供专用适配；请新建通用脚本配置。"); } }
        public static Exception Unavailable() { return new NotSupportedException(Message); }
        public static ServiceSnapshot Inspect()
        {
            return new ServiceSnapshot { State = ServiceState.Unknown, Owner = L.Text("未核实"), Detail = Message, Owned = false, ControllerValid = false };
        }
    }
    public static class HermesGateway
    {
        public static ServiceSnapshot Inspect(ServiceConfig s, ProjectPaths paths, List<ProcessIdentity> records) { return PublicAdapterBoundary.Inspect(); }
    }
    public static class LocalBridge
    {
        public const string Adapter = "local-bridge";
        public static ServiceSnapshot Inspect(ServiceConfig s, ProjectPaths paths, List<ProcessIdentity> records) { return PublicAdapterBoundary.Inspect(); }
        public static IEnumerable<string> LogFiles() { return new string[0]; }
        public static string Redact(string text)
        {
            text = Regex.Replace(text, @"(?i)(bearer\s+)[^\s""';,]+", "$1[redacted]");
            text = Regex.Replace(text, @"(?i)((?:api[-_ ]?key|token|secret|password|authorization)[""']?\s*[=:]\s*)[^\s,;]+", "$1[redacted]");
            return Regex.Replace(text, @"https?://[^\s""'<>]+", match => {
                try { var uri = new UriBuilder(match.Value) { UserName = "", Password = "", Query = "", Fragment = "" }; return uri.Uri.GetLeftPart(UriPartial.Authority); }
                catch { return "[URL redacted]"; }
            });
        }
    }
    public sealed class WorkbenchBinding
    {
        public int Version = 1;
        public string Root = "", PythonLauncher = "", RuntimeExecutable = "";
    }
    public sealed class WorkbenchReceipt
    {
        public string ServiceId = "", Root = "", Stdout = "", Stderr = "";
        public ProcessIdentity Process;
    }
    public static class ImageWorkbench
    {
        public const string Adapter = "image-workbench";
        public static WorkbenchBinding Binding(ProjectPaths paths) { throw PublicAdapterBoundary.Unavailable(); }
        public static int DesiredPort(WorkbenchBinding binding) { throw PublicAdapterBoundary.Unavailable(); }
        public static ServiceSnapshot Inspect(ServiceConfig s, ProjectPaths paths, List<ProcessIdentity> records) { return PublicAdapterBoundary.Inspect(); }
        public static WorkbenchReceipt Receipt(ServiceConfig s, ProjectPaths paths, long afterTicks) { return null; }
        public static List<string> ActiveLogFiles(ProjectPaths paths) { return new List<string>(); }
        public static List<string> LogFiles(ServiceConfig s, ProjectPaths paths) { return new List<string>(); }
        public static void AssertRestartPossible(ServiceConfig s, ProjectPaths paths) { throw PublicAdapterBoundary.Unavailable(); }
        public static int RunControl(string[] args) { Console.Error.WriteLine(PublicAdapterBoundary.Message); return 1; }
    }
}
