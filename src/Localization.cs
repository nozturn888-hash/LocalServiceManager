using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace LocalServiceManager
{
    public sealed class LanguagePreference { public int Version = 1; public string Language = "zh-CN"; }
    // Chinese resource keys are stable and separate from service names, paths and protocol values.
    public static class L
    {
        static volatile string current = "zh-CN";
        static readonly Dictionary<string, string> english = EnglishResources.Values;
        static readonly Dictionary<string, string> reverse = BuildReverse();
        static readonly Dictionary<Form, List<Action>> forms = new Dictionary<Form, List<Action>>();
        static readonly object textGate = new object();
        static readonly Dictionary<string, Func<string>> prepared = new Dictionary<string, Func<string>>();
        static readonly Queue<string> preparedOrder = new Queue<string>();
        public static event Action Changed;
        public static string Current { get { return current; } }
        public static bool IsApplying { get; private set; }
        public static string ProductName { get { return Text("本机服务管家"); } }
        public static IEnumerable<string> ResourceKeys { get { return english.Keys; } }
        static Dictionary<string,string> BuildReverse()
        {
            var values = new Dictionary<string,string>(StringComparer.Ordinal);
            foreach (var pair in english) if (!values.ContainsKey(pair.Value)) values.Add(pair.Value, pair.Key);
            return values;
        }
        public static string Canonical(string value)
        {
            if (value == null) return "";
            if (english.ContainsKey(value)) return value;
            string key; return reverse.TryGetValue(value, out key) ? key : value;
        }
        public static bool HasResource(string value) { return english.ContainsKey(Canonical(value)); }
        public static string Text(string key)
        {
            if (key == null) return "";
            string original = Canonical(key), value;
            return current == "en-US" && english.TryGetValue(original, out value) ? value : original;
        }
        public static string Format(string key, params object[] args)
        {
            string resource = Canonical(key); var copied = (object[])args.Clone();
            Func<string> render = () => String.Format(CultureInfo.InvariantCulture, Text(resource), copied);
            string value = render();
            lock (textGate)
            {
                if (!prepared.ContainsKey(value)) preparedOrder.Enqueue(value);
                prepared[value] = render;
                while (preparedOrder.Count > 2048) prepared.Remove(preparedOrder.Dequeue());
            }
            return value;
        }
        public static string Display(string value)
        {
            if (value == null) return "";
            Func<string> render;
            lock (textGate) { if (!prepared.TryGetValue(value, out render)) render = null; }
            return render != null ? render() : Text(value);
        }
        sealed class DynamicText
        {
            readonly string original;
            public DynamicText(string value) { original = value; }
            public override string ToString() { return Display(original); }
        }
        // Only program-owned text belongs here. Keep user strings as ordinary format arguments.
        public static object Dynamic(string appOwnedText) { return new DynamicText(appOwnedText); }
        public static void SetLanguage(string value)
        {
            if (value != "zh-CN" && value != "en-US") throw new ArgumentException(Text("语言设置无效"));
            if (current == value) return;
            current = value; IsApplying = true;
            try
            {
                foreach (var pair in forms.ToArray())
                {
                    if (pair.Key.IsDisposed) { forms.Remove(pair.Key); continue; }
                    pair.Key.SuspendLayout();
                    try { foreach (var apply in pair.Value) apply(); }
                    finally { pair.Key.ResumeLayout(true); }
                }
                var changed = Changed; if (changed != null) changed();
            }
            finally { IsApplying = false; }
        }
        public static void LoadPreference(string root, string fallback = "zh-CN")
        {
            string path = Path.Combine(root, "config", "preferences.json");
            string value = fallback;
            if (File.Exists(path))
            {
                var settings = Json.FileRead<LanguagePreference>(path);
                if (settings == null || settings.Version != 1) throw new InvalidDataException(Text("语言设置版本不受支持"));
                value = settings.Language;
            }
            SetLanguage(value);
        }
        public static void SavePreference(string root, string value)
        {
            if (value != "zh-CN" && value != "en-US") throw new ArgumentException(Text("语言设置无效"));
            Json.AtomicWrite(Path.Combine(root, "config", "preferences.json"), new LanguagePreference { Language = value });
        }
        public static void Attach(Form form)
        {
            Detach(form); var bindings = new List<Action>(); var menus = new HashSet<ToolStrip>();
            CaptureControl(form, bindings, menus); forms[form] = bindings;
            form.Disposed += (o,e) => Detach(form);
            bool previous = IsApplying; IsApplying = true;
            try { foreach (var binding in bindings) binding(); }
            finally { IsApplying = previous; }
        }
        public static void Detach(Form form) { forms.Remove(form); }
        static void CaptureControl(Control control, List<Action> bindings, HashSet<ToolStrip> menus)
        {
            if (!(control is TextBoxBase) && !(control is ComboBox) && HasResource(control.Text))
            {
                string key = Canonical(control.Text);
                var button = control as Button;
                if (button != null && button.Dock != DockStyle.Fill)
                {
                    // Native preferred size follows translated text, font and DPI.
                    // Capture design minima once; docked width must remain free to follow its parent.
                    button.MinimumSize = new Size(button.Dock == DockStyle.None ? Math.Max(button.MinimumSize.Width, button.Width) : button.MinimumSize.Width,
                        Math.Max(button.MinimumSize.Height, button.Height));
                    if (button.Padding == Padding.Empty) button.Padding = new Padding(12, 0, 12, 0);
                    button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                    button.AutoSize = true;
                }
                bindings.Add(() =>
                {
                    if (control.IsDisposed) return;
                    string translated = Text(key); if (control.Text != translated) control.Text = translated;
                });
            }
            var list = control as ListView;
            if (list != null) foreach (ColumnHeader header in list.Columns)
            {
                if (!HasResource(header.Text)) continue; var captured = header; string key = Canonical(header.Text);
                bindings.Add(() => { if (!list.IsDisposed) captured.Text = Text(key); });
            }
            var combo = control as ComboBox;
            if (combo != null && combo.DropDownStyle == ComboBoxStyle.DropDownList)
            {
                var itemKeys = combo.Items.Cast<object>().Select(x => x is string && HasResource((string)x) ? Canonical((string)x) : null).ToArray();
                if (itemKeys.Any(x => x != null)) bindings.Add(() =>
                {
                    if (combo.IsDisposed || combo.Items.Count != itemKeys.Length) return;
                    int index = combo.SelectedIndex; combo.BeginUpdate();
                    try { for (int i=0;i<itemKeys.Length;i++) if(itemKeys[i]!=null) combo.Items[i] = Text(itemKeys[i]); combo.SelectedIndex=index; }
                    finally { combo.EndUpdate(); }
                });
            }
            var strip = control as ToolStrip; if(strip!=null) CaptureMenu(strip, bindings, menus);
            if(control.ContextMenuStrip!=null) CaptureMenu(control.ContextMenuStrip,bindings,menus);
            foreach(Control child in control.Controls) CaptureControl(child,bindings,menus);
        }
        static void CaptureMenu(ToolStrip menu, List<Action> bindings, HashSet<ToolStrip> visited)
        {
            if (!visited.Add(menu)) return;
            foreach (ToolStripItem item in menu.Items)
            {
                if (HasResource(item.Text))
                {
                    var captured = item; string key = Canonical(item.Text);
                    bindings.Add(() => { if (!captured.IsDisposed) captured.Text = Text(key); });
                }
                var parent=item as ToolStripDropDownItem;
                if(parent!=null && parent.HasDropDownItems) CaptureMenu(parent.DropDown,bindings,visited);
            }
        }
        public static void TranslateMenu(ToolStrip menu)
        {
            foreach(ToolStripItem item in menu.Items)
            {
                if(HasResource(item.Text)) item.Text=Text(item.Text);
                var parent=item as ToolStripDropDownItem;
                if(parent!=null && parent.HasDropDownItems) TranslateMenu(parent.DropDown);
            }
            menu.PerformLayout(); menu.Invalidate();
        }
        public static void Tip(ToolTip tips,Control target,string resource)
        {
            tips.SetToolTip(target,Text(resource));
        }
        public static DialogResult MessageBox(string message) { return MessageBox(null,message,ProductName,MessageBoxButtons.OK,MessageBoxIcon.None); }
        public static DialogResult MessageBox(string message,string caption) { return MessageBox(null,message,caption,MessageBoxButtons.OK,MessageBoxIcon.None); }
        public static DialogResult MessageBox(string message,string caption,MessageBoxButtons buttons,MessageBoxIcon icon) { return MessageBox(null,message,caption,buttons,icon); }
        public static DialogResult MessageBox(IWin32Window owner,string message) { return MessageBox(owner,message,ProductName,MessageBoxButtons.OK,MessageBoxIcon.None); }
        public static DialogResult MessageBox(IWin32Window owner,string message,string caption) { return MessageBox(owner,message,caption,MessageBoxButtons.OK,MessageBoxIcon.None); }
        public static DialogResult MessageBox(IWin32Window owner,string message,string caption,MessageBoxButtons buttons) { return MessageBox(owner,message,caption,buttons,MessageBoxIcon.None); }
        public static DialogResult MessageBox(IWin32Window owner,string message,string caption,MessageBoxButtons buttons,MessageBoxIcon icon) { return MessageBox(owner,message,caption,buttons,icon,MessageBoxDefaultButton.Button1); }
        public static DialogResult MessageBox(IWin32Window owner,string message,string caption,MessageBoxButtons buttons,MessageBoxIcon icon,MessageBoxDefaultButton defaultButton)
        {
            using(var dialog=new LocalizedMessage(message,caption,buttons,icon,defaultButton))
                return owner==null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        }
        sealed class LocalizedMessage : Form
        {
            readonly string originalMessage,originalCaption;
            readonly Label body;
            public LocalizedMessage(string message,string caption,MessageBoxButtons choices,MessageBoxIcon icon,MessageBoxDefaultButton defaultButton)
            {
                originalMessage=message; originalCaption=caption; Font=new Font("Microsoft YaHei UI",9.5f);
                StartPosition=FormStartPosition.CenterParent; FormBorderStyle=FormBorderStyle.FixedDialog; MinimizeBox=MaximizeBox=false;
                ClientSize=new Size(660,260); AppTheme.Form(this);
                var root=new TableLayoutPanel { Dock=DockStyle.Fill,Padding=new Padding(20),ColumnCount=1,RowCount=2 };
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.AutoSize));Controls.Add(root);
                var scroll=new Panel { Dock=DockStyle.Fill,AutoScroll=true };root.Controls.Add(scroll,0,0);
                body=new Label { AutoSize=true,MaximumSize=new Size(600,0),Location=new Point(2,4) };scroll.Controls.Add(body);
                var buttons=new FlowLayoutPanel { AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,WrapContents=false,Padding=new Padding(0,12,0,0) };
                root.Controls.Add(buttons,0,1);
                DialogResult[] results=choices==MessageBoxButtons.OKCancel?new[]{DialogResult.OK,DialogResult.Cancel}:choices==MessageBoxButtons.YesNo?new[]{DialogResult.Yes,DialogResult.No}:choices==MessageBoxButtons.YesNoCancel?new[]{DialogResult.Yes,DialogResult.No,DialogResult.Cancel}:choices==MessageBoxButtons.RetryCancel?new[]{DialogResult.Retry,DialogResult.Cancel}:choices==MessageBoxButtons.AbortRetryIgnore?new[]{DialogResult.Abort,DialogResult.Retry,DialogResult.Ignore}:new[]{DialogResult.OK};
                var built=new List<Button>();
                foreach(var result in results)
                {
                    string key=result==DialogResult.OK?"确定":result==DialogResult.Cancel?"取消":result==DialogResult.Yes?"是":result==DialogResult.No?"否":result==DialogResult.Retry?"重试":result==DialogResult.Abort?"中止":"忽略";
                    var button=new ThemeButton(L.Text(key)) { DialogResult=result,Width=100 };built.Add(button);
                    if(result==DialogResult.Cancel)CancelButton=button;
                }
                for(int i=built.Count-1;i>=0;i--)buttons.Controls.Add(built[i]);
                int chosen=defaultButton==MessageBoxDefaultButton.Button2?1:defaultButton==MessageBoxDefaultButton.Button3?2:0;
                AcceptButton=built[Math.Min(chosen,built.Count-1)];
                AppTheme.Apply(this); RefreshLanguage(); Attach(this); Changed+=RefreshLanguage;
            }
            void RefreshLanguage() { Text=L.Display(originalCaption);body.Text=L.Display(originalMessage); }
            protected override void Dispose(bool disposing) { if(disposing){Changed-=RefreshLanguage;Detach(this);}base.Dispose(disposing); }
        }
    }
}
