using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LocalServiceManager
{
    public static class IconLibrary
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint count);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
        public static Bitmap Builtin(string name, int size)
        {
            var b = new Bitmap(size, size); using (var g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
                Color color = name == "comfy" ? Color.FromArgb(114, 87, 215) : name == "mcp" ? Color.FromArgb(193, 125, 39) : Color.FromArgb(39, 116, 182);
                using (var brush = new SolidBrush(color)) g.FillEllipse(brush, 1, 1, size - 2, size - 2);
                float u = size / 32f; using (var pen = new Pen(Color.White, 1.8f * u))
                {
                    if (name == "mcp") { g.DrawLine(pen, 9*u, 11*u, 16*u, 6*u); g.DrawLine(pen, 16*u, 6*u, 23*u, 11*u); g.DrawLine(pen, 9*u, 21*u, 16*u, 26*u); g.DrawLine(pen, 16*u, 26*u, 23*u, 21*u); g.DrawEllipse(pen, 11*u, 11*u, 10*u, 10*u); }
                    else { g.DrawRectangle(pen, 8*u, 8*u, 16*u, 7*u); g.DrawRectangle(pen, 8*u, 18*u, 16*u, 7*u); g.DrawLine(pen, 11*u, 11.5f*u, 13*u, 11.5f*u); g.DrawLine(pen, 11*u, 21.5f*u, 13*u, 21.5f*u); }
                }
            }
            return b;
        }
        public static Bitmap Load(string key, ProjectPaths paths, int size)
        {
            if (key.StartsWith("builtin:")) return Builtin(key.Substring(8), size);
            try { using (var img = Image.FromFile(paths.Resolve(key))) return new Bitmap(img, size, size); } catch { return Builtin("server", size); }
        }
        public static Icon AppIcon()
        {
            using (var stream = typeof(IconLibrary).Assembly.GetManifestResourceStream("LocalServiceManager.Application.ico"))
                if (stream != null) using (var embedded = new Icon(stream, 32, 32)) return (Icon)embedded.Clone();
            using (var b = Builtin("server", 32)) { IntPtr h = b.GetHicon(); try { return (Icon)Icon.FromHandle(h).Clone(); } finally { DestroyIcon(h); } }
        }
        public static List<Bitmap> Extract(string path)
        {
            var result = new List<Bitmap>(); string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".exe" || ext == ".dll")
            {
                uint count = Math.Min(128, ExtractIconEx(path, -1, null, null, 0));
                for (int i = 0; i < count; i++)
                {
                    var large = new IntPtr[1]; var small = new IntPtr[1]; ExtractIconEx(path, i, large, small, 1);
                    try { IntPtr h = large[0] != IntPtr.Zero ? large[0] : small[0]; if (h != IntPtr.Zero) using (var icon = (Icon)Icon.FromHandle(h).Clone()) result.Add(icon.ToBitmap()); }
                    finally { if (large[0] != IntPtr.Zero) DestroyIcon(large[0]); if (small[0] != IntPtr.Zero) DestroyIcon(small[0]); }
                }
            }
            else if (ext == ".ico")
            {
                byte[] bytes = File.ReadAllBytes(path); if (bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException(L.Text("图标超过 8 MiB"));
                using (var input = new BinaryReader(new MemoryStream(bytes)))
                {
                    if (input.ReadUInt16() != 0 || input.ReadUInt16() != 1) throw new InvalidDataException(L.Text("ICO 无效"));
                    int count = Math.Min(128, (int)input.ReadUInt16());
                    for (int i = 0; i < count; i++)
                    {
                        input.BaseStream.Position = 6 + i * 16; byte[] entry = input.ReadBytes(16);
                        int length = BitConverter.ToInt32(entry, 8), offset = BitConverter.ToInt32(entry, 12);
                        if (length <= 0 || offset < 0 || (long)offset + length > bytes.Length) throw new InvalidDataException(L.Text("ICO 资源范围无效"));
                        using (var file = new MemoryStream()) using (var output = new BinaryWriter(file))
                        {
                            output.Write((ushort)0); output.Write((ushort)1); output.Write((ushort)1); output.Write(entry, 0, 12); output.Write(22); output.Write(bytes, offset, length); file.Position = 0;
                            using (var icon = new Icon(file)) result.Add(icon.ToBitmap());
                        }
                    }
                }
            }
            else
            {
                using (var image = Image.FromFile(path)) { if (image.Width > 8192 || image.Height > 8192) throw new InvalidDataException(L.Text("图片尺寸过大")); result.Add(new Bitmap(image, 64, 64)); }
            }
            if (result.Count == 0) throw new InvalidDataException(L.Text("没有可提取图标")); return result;
        }
        public static Bitmap PickInMemory(IWin32Window owner)
        {
            using (var picker = new OpenFileDialog { Filter = L.Text("图标 / 图片 / 程序资源|*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.exe;*.dll"), Title = L.Text("仅提取图标，不执行程序") })
            {
                if (picker.ShowDialog(owner) != DialogResult.OK) return null;
                var images = Extract(picker.FileName);
                try
                {
                    int selected = 0;
                    if (images.Count > 1)
                    {
                        using (var list = new ListView { Dock = DockStyle.Fill, View = View.LargeIcon, MultiSelect = false })
                        using (var icons = new ImageList { ImageSize = new Size(48, 48), ColorDepth = ColorDepth.Depth32Bit })
                        using (var form = CreateResourcePicker(list))
                        {
                            for (int i = 0; i < images.Count; i++) { icons.Images.Add(images[i]); list.Items.Add(new ListViewItem("#" + (i + 1) + " · " + images[i].Width + "px", i)); }
                            list.LargeImageList = icons; list.Items[0].Selected = true;
                            try { if (form.ShowDialog(owner) != DialogResult.OK || list.SelectedIndices.Count == 0) return null; selected = list.SelectedIndices[0]; }
                            finally { L.Detach(form); }
                        }
                    }
                    return new Bitmap(images[selected]);
                }
                finally { foreach (var image in images) image.Dispose(); }
            }
        }
        internal static Form CreateResourcePicker(ListView list)
        {
            var form = new Form { Text = L.Text("选择图标资源"), Size = new Size(530, 370),
                MinimumSize = new Size(420, 280), StartPosition = FormStartPosition.CenterParent };
            var ok = new ThemeButton(L.Text("使用所选图标"), "accent") {
                Dock = DockStyle.Bottom, Height = 40, DialogResult = DialogResult.OK };
            form.Controls.Add(list); form.Controls.Add(ok); form.AcceptButton = ok;
            form.Font = new Font("Microsoft YaHei UI", 9.5f);
            AppTheme.Form(form); AppTheme.Apply(form); L.Attach(form);
            return form;
        }
        public static string SaveBitmap(Bitmap image, ProjectPaths paths)
        {
            string name = Guid.NewGuid().ToString("N") + ".png";
            image.Save(Path.Combine(paths.Icons, name), System.Drawing.Imaging.ImageFormat.Png); return "icons/" + name;
        }
    }
}
