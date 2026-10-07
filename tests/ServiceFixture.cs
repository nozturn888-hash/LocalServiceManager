using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

public static class ServiceFixture
{
    static string Arg(string[] args, string key, string fallback) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (Array.IndexOf(args, "--noop") >= 0) { Console.WriteLine("隔离停止入口未停止服务"); return 0; }
        if (Array.IndexOf(args, "--fail") >= 0) { Console.Error.WriteLine("隔离脚本故意失败"); return 7; }
        int port = Int32.Parse(Arg(args, "--port", "0")); string token = Arg(args, "--token", "fixture");
        if (Array.IndexOf(args, "--stop") >= 0)
        {
            using (var client = new TcpClient())
            {
                client.Connect(IPAddress.Loopback, port); client.ReceiveTimeout = 5000;
                byte[] request = Encoding.ASCII.GetBytes("POST /stop HTTP/1.1\r\nHost: localhost\r\nX-Test-Token: " + token + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                var stream = client.GetStream(); stream.Write(request, 0, request.Length);
                using (var reader = new StreamReader(stream)) { string response = reader.ReadLine(); return response != null && response.Contains("200") ? 0 : 2; }
            }
        }
        string queueFile = Arg(args, "--queue-file", ""), text = Arg(args, "--text", "中文与空格路径"), events = Arg(args, "--events", "");
        int delay = Int32.Parse(Arg(args, "--stop-delay", "0")); bool stopping = false;
        var listener = new TcpListener(IPAddress.Loopback, port); listener.Start();
        if (events.Length > 0) File.AppendAllText(events, "start " + System.Diagnostics.Process.GetCurrentProcess().Id + " " + DateTime.UtcNow.Ticks + "\n");
        Console.WriteLine("测试服务已就绪：" + text + " | " + token); Console.Error.WriteLine("stderr 中文错误样例");
        using (var pulse = new Timer(o => { try { Console.WriteLine("心跳 " + DateTime.Now.ToString("HH:mm:ss") + " 中文输出"); } catch { } }, null, 500, 500))
        {
            while (!stopping)
            {
                using (var client = listener.AcceptTcpClient())
                {
                    client.ReceiveTimeout = 2000; client.SendTimeout = 2000;
                    using (var stream = client.GetStream()) using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                    {
                        string first = reader.ReadLine() ?? "", header, receivedToken = "";
                        while (!String.IsNullOrEmpty(header = reader.ReadLine())) if (header.StartsWith("X-Test-Token: ")) receivedToken = header.Substring(14);
                        string status = "200 OK", body = "{\"system\":{\"fixture\":true}}";
                        if (first.Contains("/queue"))
                        {
                            string queue = File.Exists(queueFile) ? File.ReadAllText(queueFile).Trim() : "";
                            body = queue == "busy" ? "{\"queue_running\":[[1]],\"queue_pending\":[]}" : queue == "broken" ? "{}" : "{\"queue_running\":[],\"queue_pending\":[]}";
                        }
                        else if (first.StartsWith("POST /stop"))
                        {
                            if (receivedToken == token) { Thread.Sleep(delay); stopping = true; body = "{\"stopping\":true}"; }
                            else { status = "403 Forbidden"; body = "{}"; }
                        }
                        byte[] bytes = Encoding.UTF8.GetBytes(body); byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
                        stream.Write(head, 0, head.Length); stream.Write(bytes, 0, bytes.Length);
                    }
                }
            }
        }
        listener.Stop(); Console.WriteLine("测试服务已停止");
        if (events.Length > 0) File.AppendAllText(events, "stop " + System.Diagnostics.Process.GetCurrentProcess().Id + " " + DateTime.UtcNow.Ticks + "\n");
        return 0;
    }
}
