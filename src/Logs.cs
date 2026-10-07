using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LocalServiceManager
{
    public sealed class LogBuffer
    {
        readonly Queue<string> entries = new Queue<string>();
        int characters;
        public int Version { get; private set; }
        public void Add(string text)
        {
            if (String.IsNullOrEmpty(text)) return;
            for (int i = 0; i < text.Length; i += 8192)
            {
                string chunk = text.Substring(i, Math.Min(8192, text.Length - i)); entries.Enqueue(chunk); characters += chunk.Length;
                while (entries.Count > 1200 || characters > 512 * 1024) characters -= entries.Dequeue().Length;
            }
            Version++;
        }
        public string Text(string search)
        {
            string all = String.Concat(entries);
            if (String.IsNullOrWhiteSpace(search)) return all;
            return String.Join(Environment.NewLine, all.Split(new[] { '\n' }).Where(x => x.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0));
        }
    }
    public sealed class TailReader
    {
        public readonly LogBuffer Buffer = new LogBuffer();
        string path = "", encoding = "";
        long offset;
        Decoder decoder;
        bool accessErrorShown;
        public void Reset() { path = ""; offset = 0; decoder = null; }
        public void Read(string file, string encodingName)
        {
            if (String.IsNullOrEmpty(file)) return;
            try
            {
                using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (file != path || encoding != encodingName || input.Length < offset)
                    {
                        bool first = file != path; path = file; encoding = encodingName;
                        offset = Math.Max(0, input.Length - 65536);
                        var enc = Encoding.GetEncoding(encodingName); decoder = enc.GetDecoder();
                        if (offset == 0 && input.Length >= 3)
                        {
                            var bom = new byte[3]; input.Read(bom, 0, 3);
                            if (bom[0] == 0xef && bom[1] == 0xbb && bom[2] == 0xbf) { offset = 3; decoder = Encoding.UTF8.GetDecoder(); }
                            else if (bom[0] == 0xff && bom[1] == 0xfe) { offset = 2; decoder = Encoding.Unicode.GetDecoder(); }
                            else if (bom[0] == 0xfe && bom[1] == 0xff) { offset = 2; decoder = Encoding.BigEndianUnicode.GetDecoder(); }
                        }
                        if (first) Buffer.Add("\r\n── " + Path.GetFileName(file) + " ──\r\n");
                        else Buffer.Add(L.Text("\r\n── 日志已截断/轮转 ──\r\n"));
                    }
                    accessErrorShown = false; input.Position = offset; byte[] bytes = new byte[65536]; int n = input.Read(bytes, 0, bytes.Length); offset += n;
                    if (n > 0) { char[] chars = new char[65538]; int count = decoder.GetChars(bytes, 0, n, chars, 0, false); Buffer.Add(new string(chars, 0, count)); }
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { if (!accessErrorShown) { Buffer.Add(L.Text("\r\n日志读取被拒绝：请检查文件访问权限。\r\n")); accessErrorShown = true; } }
            catch (System.Security.SecurityException) { if (!accessErrorShown) { Buffer.Add(L.Text("\r\n日志读取受到本机安全策略限制。\r\n")); accessErrorShown = true; } }
        }
        static void ParseExpression(string expression, out string directory, out string filename)
        {
            string value = expression.Trim();
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"') value = value.Substring(1, value.Length - 2);
            int separator = value.LastIndexOfAny(new[] { '\\', '/' });
            directory = separator < 0 ? "." : value.Substring(0, separator + 1);
            filename = separator < 0 ? value : value.Substring(separator + 1);
            if (filename.Length == 0 || filename == "." || filename == "..") throw new ArgumentException(L.Text("日志路径需包含文件名或文件名通配符，不能只填目录"));
            if (directory.IndexOfAny(new[] { '*', '?' }) >= 0) throw new ArgumentException(L.Text("日志目录不能含通配符；* 和 ? 只能用于最后的文件名"));
            ProjectPaths.CheckPathText(directory);
            if (filename.Any(c => Path.GetInvalidFileNameChars().Contains(c) && c != '*' && c != '?')) throw new ArgumentException(L.Text("日志文件名含非法字符；请检查引号、冒号或控制字符"));
        }
        public static void ValidateExpression(string expression)
        {
            if (String.IsNullOrWhiteSpace(expression)) return;
            string directory, filename; ParseExpression(expression, out directory, out filename);
        }
        public static string ResolveLatest(ProjectPaths paths, string expression)
        {
            if (String.IsNullOrWhiteSpace(expression)) return "";
            string directory, filename; ParseExpression(expression, out directory, out filename);
            // A mask is not a filesystem path. Normalize only its wildcard-free directory,
            // then hand the leaf pattern to Directory.GetFiles; never GetFullPath(mask).
            string dir = paths.Resolve(directory);
            if (filename.IndexOfAny(new[] { '*', '?' }) < 0)
            {
                string file = Path.Combine(dir, filename);
                if (Directory.Exists(file)) throw new ArgumentException(L.Text("指定的是目录，请填写日志文件或目录中的 *.log"));
                return file;
            }
            if (!Directory.Exists(dir)) return "";
            return Directory.GetFiles(dir, filename).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() ?? "";
        }
    }
    public sealed class LogMaintenance
    {
        public const long Limit = 2 * 1024 * 1024;
        readonly ProjectPaths paths;
        readonly object gate = new object();
        public LogMaintenance(ProjectPaths paths) { this.paths = paths; }
        public void Event(string message)
        {
            lock (gate)
            {
                string path = Path.Combine(paths.Logs, "manager.log");
                try { Rotate(path); File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine, new UTF8Encoding(false)); } catch (IOException) { }
            }
        }
        public void Maintain(IEnumerable<string> activeFiles)
        {
            var active = new HashSet<string>(activeFiles, StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.GetFiles(paths.Logs, "*.log"))
            {
                try { Rotate(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            // At most 10 inactive capture files per service. Active handles are never deleted.
            foreach (var group in Directory.GetFiles(paths.Logs, "*.log").Where(x => Path.GetFileName(x).Length > 32 && Path.GetFileName(x)[32] == '-').GroupBy(x => Path.GetFileName(x).Substring(0, 32)))
            {
                foreach (var old in group.Where(x => !active.Contains(x)).OrderByDescending(File.GetLastWriteTimeUtc).Skip(10))
                    try { File.Delete(old); if (File.Exists(old + ".1")) File.Delete(old + ".1"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        public static void Rotate(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length <= Limit) return;
            // Child handles use FILE_APPEND_DATA. Truncating does not leave their write pointer
            // past EOF. Retain the last 2 MiB in one bounded archive and reopen no child pipe.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                long count = Math.Min(Limit, stream.Length); stream.Position = stream.Length - count;
                using (var archive = new FileStream(path + ".1", FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    byte[] block = new byte[8192]; while (count > 0) { int n = stream.Read(block, 0, (int)Math.Min(block.Length, count)); if (n == 0) break; archive.Write(block, 0, n); count -= n; }
                }
                stream.SetLength(0);
            }
        }
    }
}
