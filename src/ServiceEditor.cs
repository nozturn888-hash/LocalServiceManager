using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LocalServiceManager
{
    public sealed class ServiceEditor : Form
    {
        readonly ProjectPaths paths;
        ServiceConfig draft;
        readonly bool newEntry, clientMode;
        readonly Func<ServiceSnapshot> readState;
        readonly Action<ServiceConfig, Bitmap> commit;
        readonly Timer stateTimer = new Timer { Interval = 500 };
        readonly Dictionary<string, TextBox> fields = new Dictionary<string, TextBox>();
        readonly Dictionary<string, Button> browseButtons = new Dictionary<string, Button>();
        readonly List<Control> editingControls = new List<Control>();
        readonly Label kind = new Label { AutoSize = true };
        readonly Label management = new Label { AutoSize = true, MaximumSize = new Size(520, 0) };
        readonly Label warning = new Label { AutoSize = true, Tag = "warning" };
        readonly Button template = new ThemeButton(L.Text("预填模板…")) { AutoSize = true, MinimumSize = new Size(112, 30), Height = 30 };
        readonly Button generic = new ThemeButton(L.Text("清空为通用")) { AutoSize = true, MinimumSize = new Size(112, 30), Height = 30 };
        readonly ComboBox builtins = new ThemeCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        readonly ComboBox encoding = new ThemeCombo { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly ComboBox openMode = new ThemeCombo { DropDownStyle = ComboBoxStyle.DropDownList };
        Label followPortLabel;
        readonly CheckBox followPort = new ThemeCheckBox { Text = L.Text("跟随工作台端口（运行实例优先，否则读取工作台配置）"), AutoSize = true };
        readonly CheckBox auto = new ThemeCheckBox { Text = L.Text("随工具启动此服务（与 Windows 登录启动工具独立）"), AutoSize = true };
        readonly CheckBox reviewed = new ThemeCheckBox { Text = L.Text("已核对脚本、参数和身份；允许执行此条目"), AutoSize = true };
        readonly Button save = new ThemeButton(L.Text("保存配置"), "accent") { Width = 110, Height = 34 };
        readonly PictureBox iconPreview = new PictureBox { Size = new Size(40, 40), SizeMode = PictureBoxSizeMode.Zoom };
        readonly ToolTip tips = new ToolTip();
        readonly Dictionary<TextBox, string> cues = new Dictionary<TextBox, string>();
        Bitmap pendingIcon;
        bool applying;
        string initialFingerprint;
        const string DefaultWarning = "仅保存配置；打开入口和服务启停独立，保存不会运行程序。";
        public ServiceConfig Result { get; private set; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, uint message, IntPtr w, string text);

        public ServiceEditor(ProjectPaths paths, ServiceConfig source, Func<ServiceSnapshot> readState = null, Action<ServiceConfig, Bitmap> commit = null, bool newEntry = false)
        {
            this.paths = paths; this.readState = readState; this.commit = commit; this.newEntry = newEntry;
            draft = source.Clone(); clientMode = draft.ClientManaged || draft.Adapter == "stdio";
            AppTheme.Form(this); Font = new Font("Microsoft YaHei UI", 9.5f); Size = new Size(840, 780); MinimumSize = new Size(740, 640); StartPosition = FormStartPosition.CenterParent;
            SetTitle();
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(16) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); Controls.Add(root);
            warning.Text = L.Text(DefaultWarning); root.Controls.Add(warning, 0, 0);
            var tabs = new ThemeTabs { Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 8) }; root.Controls.Add(tabs, 0, 1);
            var basic = Page(tabs, clientMode ? L.Text("客户端登记") : L.Text("名称与打开入口"));
            AddField(basic, L.Text("名称"), "name", draft.Name);
            var typeRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
            kind.Margin = new Padding(0, 7, 12, 5); typeRow.Controls.Add(kind);
            template.Visible = generic.Visible = newEntry && !clientMode; typeRow.Controls.Add(template); typeRow.Controls.Add(generic);
            editingControls.AddRange(new Control[] { template, generic });
            AddControl(basic, L.Text("配置类型"), typeRow); AddControl(basic, L.Text("管理说明"), management);
            template.Click += (o, e) => { using (var picker = new TemplatePicker(paths)) if (picker.ShowDialog(this) == DialogResult.OK) SwitchTemplate(picker.Result); };
            generic.Click += (o, e) => SwitchTemplate(new ServiceConfig());
            var iconRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            builtins.Items.AddRange(new object[] { L.Text("服务"), "ComfyUI", "MCP" });
            builtins.SelectedIndex = draft.Icon == "builtin:comfy" ? 1 : draft.Icon == "builtin:mcp" ? 2 : 0;
            var choose = new ThemeButton(L.Text("图片 / ICO / EXE…")) { AutoSize = true, MinimumSize = new Size(170, 30), Height = 30 };
            iconRow.Controls.Add(iconPreview); iconRow.Controls.Add(builtins); iconRow.Controls.Add(choose); AddControl(basic, L.Text("图标"), iconRow);
            editingControls.AddRange(new Control[] { builtins, choose }); UpdateIcon();
            builtins.SelectedIndexChanged += (o, e) => { if (applying || L.IsApplying) return; if (pendingIcon != null) { pendingIcon.Dispose(); pendingIcon = null; } draft.Icon = "builtin:" + (builtins.SelectedIndex == 1 ? "comfy" : builtins.SelectedIndex == 2 ? "mcp" : "server"); UpdateIcon(); };
            choose.Click += (o, e) => { try { var image = IconLibrary.PickInMemory(this); if (image != null) { if (pendingIcon != null) pendingIcon.Dispose(); pendingIcon = image; UpdateIcon(); } } catch (Exception ex) { L.MessageBox(this, ex.Message, L.Text("无法提取图标")); } };
            if (clientMode)
            {
                AddField(basic, L.Text("所属客户端"), "clientOwner", draft.ClientOwner, false, false, L.Text("例如 Codex 或 Claude Desktop；仅作登记"));
                AddField(basic, L.Text("MCP 命令 / 程序"), "start", draft.StartPath, true, false, L.Text("填写客户端中使用的命令；本工具不会执行"));
                AddField(basic, L.Text("MCP 命令参数"), "startArgs", draft.StartArgs, false, false, L.Text("手工记录参数；不读取或修改客户端配置"));
                AddField(basic, L.Text("工作目录（可选）"), "cwd", draft.WorkingDirectory, true, true);
                AddField(basic, L.Text("登记说明（可选）"), "clientNotes", draft.ClientNotes);
                AddControl(basic, L.Text("登记边界"), new Label { Text = L.Text("登记信息可以编辑。本工具不检测实际工具调用，不独立启动、停止或读取 stdio 协议流；所属客户端负责生命周期。"), AutoSize = true, MaximumSize = new Size(520, 0) });
            }
            var opening = clientMode ? Page(tabs, L.Text("打开入口")) : basic;
            openMode.Items.AddRange(new object[] { L.Text("未设置"), L.Text("网页 URL"), L.Text("本机应用程序") }); editingControls.Add(openMode);
            AddControl(opening, L.Text("打开入口类型"), openMode);
            AddField(opening, L.Text("URL / 应用程序"), "openTarget", draft.OpenTarget, true, false, L.Text("选择类型后填写；网页支持 http/https，应用选择 .exe"));
            followPortLabel = AddControl(opening, L.Text("工作台动态页面"), followPort);
            AddField(opening, L.Text("应用参数（可选）"), "openArgs", draft.OpenArgs);
            AddField(opening, L.Text("应用工作目录"), "openCwd", draft.OpenWorkingDirectory, true, true);
            AddControl(opening, L.Text("打开行为"), new Label { Text = L.Text("主列表双击或托盘“打开入口”只打开页面/应用，不会启动或重启服务。未设置入口时会提示。"), AutoSize = true, MaximumSize = new Size(520, 0) });
            followPort.Checked = draft.OpenFollowWorkbenchPort; editingControls.Add(followPort);
            openMode.SelectedIndex = draft.OpenType == "url" ? 1 : draft.OpenType == "application" ? 2 : 0;
            openMode.SelectedIndexChanged += (o, e) => { if (applying || L.IsApplying) return; if (openMode.SelectedIndex != 1) followPort.Checked = false; RefreshSaveState(); };
            followPort.CheckedChanged += (o, e) => { if (!applying && !L.IsApplying) RefreshSaveState(); };
            if (!clientMode)
            {
                var scripts = Page(tabs, L.Text("启动与停止"));
                AddField(scripts, L.Text("启动入口"), "start", draft.StartPath, true); AddField(scripts, L.Text("启动参数"), "startArgs", draft.StartArgs);
                AddField(scripts, L.Text("停止入口"), "stop", draft.StopPath, true); AddField(scripts, L.Text("停止参数"), "stopArgs", draft.StopArgs);
                AddField(scripts, L.Text("重启入口（可选）"), "restart", draft.RestartPath, true); AddField(scripts, L.Text("重启参数"), "restartArgs", draft.RestartArgs);
                AddField(scripts, L.Text("工作目录"), "cwd", draft.WorkingDirectory, true, true);
                auto.Checked = draft.AutoStart; reviewed.Checked = draft.Reviewed; AddControl(scripts, L.Text("服务自启"), auto); AddControl(scripts, L.Text("执行许可"), reviewed); editingControls.AddRange(new Control[] { auto, reviewed });
                AddControl(scripts, L.Text("后台适配"), new Label { Text = L.Text("支持 CMD/BAT/PS1/EXE。脚本参数按原样传递，空格路径需要双引号。交互输入或脚本自行弹窗需先适配；重启会等待原服务停止。"), AutoSize = true, MaximumSize = new Size(520, 0) });
                var health = Page(tabs, L.Text("身份、健康与日志"));
                AddField(health, L.Text("服务 EXE 完整路径"), "exe", draft.ExpectedExecutable, true); AddField(health, L.Text("命令行唯一片段"), "token", draft.CommandContains);
                AddField(health, L.Text("本机监听端口"), "port", draft.Port.ToString()); AddField(health, L.Text("HTTP 健康地址"), "http", draft.HealthUrl);
                AddField(health, L.Text("操作超时（秒）"), "timeout", draft.TimeoutSeconds.ToString()); AddField(health, L.Text("已有日志文件"), "log", draft.LogPath, true);
                encoding.Items.AddRange(new object[] { "utf-8", "gb18030", "utf-16", "big5" }); if (!encoding.Items.Contains(draft.EncodingName)) encoding.Items.Add(draft.EncodingName); encoding.SelectedItem = draft.EncodingName;
                AddControl(health, L.Text("输出与文件编码"), encoding); editingControls.Add(encoding); AddField(health, L.Text("进程记录文件"), "receipt", draft.ReceiptPath, true);
                AddControl(health, L.Text("身份保护"), new Label { Text = L.Text("身份需完整 EXE 与唯一命令片段。脚本退出不代表健康；通用外部服务只读，专用适配核验身份后控制。"), AutoSize = true, MaximumSize = new Size(520, 0) });
            }
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, FlowDirection = FlowDirection.RightToLeft };
            var cancel = new ThemeButton(L.Text("取消")) { DialogResult = DialogResult.Cancel, Width = 90, Height = 34 };
            buttons.Controls.Add(cancel); buttons.Controls.Add(save); root.Controls.Add(buttons, 0, 2); AcceptButton = save; CancelButton = cancel; save.Click += (o, e) => SaveDraft();
            AppTheme.Apply(this); ClientSizeChanged += (o, e) => { warning.MaximumSize = new Size(Math.Max(200, ClientSize.Width - 48), 0); management.MaximumSize = new Size(Math.Max(220, ClientSize.Width - 250), 0); };
            warning.MaximumSize = new Size(Math.Max(200, ClientSize.Width - 48), 0); SetMode(); initialFingerprint = Fingerprint(); stateTimer.Tick += (o, e) => RefreshSaveState();
            Shown += (o, e) => { RefreshSaveState(); if (readState != null && !clientMode) stateTimer.Start(); };
            L.Attach(this); L.Changed += RefreshLanguage; RefreshLanguage();
        }
        void SetTitle() { Text = (newEntry ? L.Text("新增 · ") : L.Text("配置 · ")) + draft.Name; }
        TableLayoutPanel Page(TabControl tabs, string title)
        {
            var page = new TabPage(title) { Padding = new Padding(12), AutoScroll = true }; tabs.TabPages.Add(page);
            var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 4, 0, 20) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 174)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); page.Controls.Add(table); return table;
        }
        Label AddControl(TableLayoutPanel table, string label, Control control)
        {
            int row = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var caption = new Label { Text = label, AutoSize = true, MaximumSize = new Size(168, 0), Margin = new Padding(0, 9, 6, 5) }; table.Controls.Add(caption, 0, row);
            control.Margin = new Padding(0, 5, 0, 7); control.Dock = DockStyle.Fill; table.Controls.Add(control, 1, row);
            if (control is Label || control is CheckBox) { control.MaximumSize = new Size(520, 0); table.Layout += (o, e) => { int available = Math.Max(180, table.ClientSize.Width - 186); if (control.MaximumSize.Width != available) control.MaximumSize = new Size(available, 0); }; }
            return caption;
        }
        void AddField(TableLayoutPanel table, string label, string key, string value, bool browse = false, bool directory = false, string cue = "")
        {
            var row = new TableLayoutPanel { AutoSize = true, ColumnCount = browse ? 2 : 1, Dock = DockStyle.Fill }; row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var text = new TextBox { Text = value, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 6, 0) }; fields[key] = text; row.Controls.Add(text, 0, 0);
            if (cue.Length > 0) { cues[text] = L.Canonical(cue); text.HandleCreated += (o, e) => SendMessage(text.Handle, 0x1501, new IntPtr(1), L.Text(cues[text])); }
            if (browse)
            {
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36)); var button = new ThemeButton("…") { Dock = DockStyle.Fill, Margin = new Padding(0), Height = 28 }; row.Controls.Add(button, 1, 0); browseButtons[key] = button;
                button.Click += (o, e) =>
                {
                    if (directory) { using (var pick = new FolderBrowserDialog { Description = L.Text("选择工作目录") }) if (pick.ShowDialog(this) == DialogResult.OK) text.Text = pick.SelectedPath; }
                    else using (var pick = new OpenFileDialog { Title = L.Text(key == "openTarget" ? "选择应用程序" : key == "log" ? "选择日志文件" : key == "receipt" ? "选择进程记录文件" : "选择脚本或程序"), Filter = key == "openTarget" ? L.Text("应用程序|*.exe") : key == "log" || key == "receipt" ? L.Text("所有文件|*.*") : L.Text("脚本与程序|*.cmd;*.bat;*.ps1;*.exe|所有文件|*.*") })
                        if (pick.ShowDialog(this) == DialogResult.OK) { text.Text = pick.FileName; if (key == "start" && fields.ContainsKey("exe") && Path.GetExtension(text.Text).Equals(".exe", StringComparison.OrdinalIgnoreCase) && fields["exe"].Text.Length == 0) fields["exe"].Text = text.Text; }
                };
            }
            AddControl(table, label, row);
        }
        void UpdateIcon() { var previous = iconPreview.Image; iconPreview.Image = pendingIcon == null ? IconLibrary.Load(draft.Icon, paths, 40) : new Bitmap(pendingIcon, 40, 40); if (previous != null) previous.Dispose(); }
        void SetMode()
        {
            kind.Text = clientMode ? L.Text("客户端托管 stdio MCP · 仅登记") : draft.Adapter == "comfy" ? L.Text("ComfyUI 模板") : draft.Adapter == "hermes-gateway" ? L.Text("Hermes Gateway 模板") : draft.Adapter == LocalBridge.Adapter ? L.Text("Local Codex Bridge 模板") : draft.Adapter == ImageWorkbench.Adapter ? L.Text("生图工作台模板") : L.Text("独立后台服务 · 通用");
            management.Text = L.Text(ServiceTemplates.Description(draft)); template.Text = draft.Adapter == "generic" ? L.Text("预填模板…") : L.Text("更换模板…");
            followPortLabel.Visible = followPort.Visible = draft.Adapter == ImageWorkbench.Adapter; RefreshSaveState();
        }
        void RefreshLanguage()
        {
            if (IsDisposed) return;
            SetTitle(); SetMode();
            foreach (var cue in cues) if (cue.Key.IsHandleCreated) SendMessage(cue.Key.Handle, 0x1501, new IntPtr(1), L.Text(cue.Value));
            PerformLayout(); Invalidate(true);
        }
        string Fingerprint() { return String.Join("\u001f", fields.OrderBy(x => x.Key).Select(x => x.Key + "=" + x.Value.Text)) + "|" + draft.Adapter + "|" + draft.Icon + "|" + auto.Checked + "|" + reviewed.Checked + "|" + openMode.SelectedIndex + "|" + followPort.Checked + "|" + encoding.SelectedItem; }
        bool HasContent() { return pendingIcon != null || Fingerprint() != initialFingerprint || L.Canonical(fields["name"].Text) != "新服务" || fields.Any(x => x.Key != "name" && x.Key != "port" && x.Key != "timeout" && x.Value.Text.Length > 0); }
        void SwitchTemplate(ServiceConfig replacement)
        {
            bool discard = !HasContent() || L.MessageBox(this, L.Text("预填模板会替换当前草稿及待存图标，但不会保存或启动。要替换吗？"), L.Text("替换当前草稿"), MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            ApplyTemplate(replacement, discard);
        }
        public bool ApplyTemplate(ServiceConfig replacement, bool discardConfirmed)
        {
            if (!newEntry || clientMode || replacement.ClientManaged || replacement.Adapter == "stdio" || (!discardConfirmed && HasContent()) || !save.Enabled) return false;
            draft = replacement.Clone(); draft.Id = Guid.NewGuid().ToString("N"); draft.AutoStart = false;
            if (pendingIcon != null) { pendingIcon.Dispose(); pendingIcon = null; }
            var values = new Dictionary<string, string> { {"name",draft.Name},{"start",draft.StartPath},{"startArgs",draft.StartArgs},{"stop",draft.StopPath},{"stopArgs",draft.StopArgs},{"restart",draft.RestartPath},{"restartArgs",draft.RestartArgs},{"cwd",draft.WorkingDirectory},{"exe",draft.ExpectedExecutable},{"token",draft.CommandContains},{"port",draft.Port.ToString()},{"http",draft.HealthUrl},{"timeout",draft.TimeoutSeconds.ToString()},{"log",draft.LogPath},{"receipt",draft.ReceiptPath},{"openTarget",draft.OpenTarget},{"openArgs",draft.OpenArgs},{"openCwd",draft.OpenWorkingDirectory} };
            applying = true;
            try { foreach (var value in values) if (fields.ContainsKey(value.Key)) fields[value.Key].Text = value.Value; auto.Checked = false; reviewed.Checked = draft.Reviewed; builtins.SelectedIndex = draft.Icon == "builtin:comfy" ? 1 : draft.Icon == "builtin:mcp" ? 2 : 0; if (!encoding.Items.Contains(draft.EncodingName)) encoding.Items.Add(draft.EncodingName); encoding.SelectedItem = draft.EncodingName; openMode.SelectedIndex = draft.OpenType == "url" ? 1 : draft.OpenType == "application" ? 2 : 0; followPort.Checked = draft.OpenFollowWorkbenchPort; UpdateIcon(); }
            finally { applying = false; }
            SetTitle(); SetMode(); initialFingerprint = Fingerprint(); return true;
        }
        static void Enable(Control control, bool enabled) { if (control.Enabled != enabled) control.Enabled = enabled; }
        void RefreshSaveState()
        {
            string blocked = clientMode ? "" : ServiceEngine.ConfigurationSaveBlock(readState == null ? null : readState());
            bool locked = blocked.Length > 0; Enable(save, !locked);
            string explanation = locked ? L.Text(blocked) : clientMode ? L.Text("仅保存本工具的登记信息；不修改外部客户端配置，不独立运行 MCP。") : L.Text(DefaultWarning);
            if (warning.Text != explanation) warning.Text = explanation;
            foreach (var control in editingControls) Enable(control, !locked && (control != followPort || openMode.SelectedIndex == 1));
            foreach (var field in fields)
            {
                bool readOnly = locked || (field.Key == "openTarget" && (openMode.SelectedIndex == 0 || (followPort.Checked && draft.Adapter == ImageWorkbench.Adapter && openMode.SelectedIndex == 1))) || ((field.Key == "openArgs" || field.Key == "openCwd") && openMode.SelectedIndex != 2);
                Enable(field.Value, true); if (field.Value.ReadOnly != readOnly) field.Value.ReadOnly = readOnly;
            }
            foreach (var button in browseButtons)
            {
                bool enabled = !locked && (button.Key == "openTarget" ? openMode.SelectedIndex == 2 : button.Key != "openCwd" || openMode.SelectedIndex == 2);
                Enable(button.Value, enabled);
            }
            // No intermediate enable/disable pass for controls with special rules.
        }
        string Value(string key, string fallback = "") { TextBox field; return fields.TryGetValue(key, out field) ? field.Text.Trim() : fallback; }
        string Raw(string key, string fallback = "") { TextBox field; return fields.TryGetValue(key, out field) ? field.Text : fallback; }
        int Number(string key, int fallback, string error)
        {
            int value; if (!Int32.TryParse(Value(key, fallback.ToString()), out value)) throw new InvalidDataException(L.Text(error)); return value;
        }
        void SaveDraft()
        {
            RefreshSaveState(); if (!save.Enabled) return;
            try
            {
                draft.Name = Value("name"); draft.StartPath = Value("start", draft.StartPath); draft.StartArgs = Raw("startArgs", draft.StartArgs); draft.StopPath = Value("stop", draft.StopPath); draft.StopArgs = Raw("stopArgs", draft.StopArgs); draft.RestartPath = Value("restart", draft.RestartPath); draft.RestartArgs = Raw("restartArgs", draft.RestartArgs); draft.WorkingDirectory = Value("cwd", draft.WorkingDirectory);
                draft.ExpectedExecutable = Value("exe", draft.ExpectedExecutable); draft.CommandContains = Value("token", draft.CommandContains); draft.Port = Number("port", draft.Port, "本机监听端口必须是整数。"); draft.TimeoutSeconds = Number("timeout", draft.TimeoutSeconds, "操作超时必须是整数。"); draft.HealthUrl = Value("http", draft.HealthUrl); draft.LogPath = Value("log", draft.LogPath); draft.ReceiptPath = Value("receipt", draft.ReceiptPath);
                if (!clientMode) { draft.EncodingName = Convert.ToString(encoding.SelectedItem); draft.Reviewed = reviewed.Checked; draft.AutoStart = auto.Checked && draft.Reviewed; } else { draft.ClientOwner = Value("clientOwner"); draft.ClientNotes = Value("clientNotes"); draft.AutoStart = false; }
                draft.OpenType = openMode.SelectedIndex == 1 ? "url" : openMode.SelectedIndex == 2 ? "application" : "none"; draft.OpenFollowWorkbenchPort = draft.Adapter == ImageWorkbench.Adapter && draft.OpenType == "url" && followPort.Checked; draft.OpenTarget = Value("openTarget"); draft.OpenArgs = Raw("openArgs"); draft.OpenWorkingDirectory = Value("openCwd");
                ConfigRepository.Validate(new AppConfig { Services = new List<ServiceConfig> { draft } });
                if (newEntry && draft.Adapter != "generic" && !clientMode) ServiceTemplates.ValidateNewDraft(draft, paths);
                if (commit != null) commit(draft, pendingIcon); else if (pendingIcon != null) draft.Icon = IconLibrary.SaveBitmap(pendingIcon, paths);
                Result = draft; DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { RefreshSaveState(); if (save.Enabled) L.MessageBox(this, ex.Message, L.Text("配置尚未保存"), MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }
        public static ServiceConfig Prefill(string path) { return new ServiceConfig { Name = Path.GetFileNameWithoutExtension(path), StartPath = path, WorkingDirectory = Path.GetDirectoryName(path), ExpectedExecutable = Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? path : "", Reviewed = true }; }
        protected override void Dispose(bool disposing) { if (disposing) { L.Changed -= RefreshLanguage; L.Detach(this); stateTimer.Dispose(); tips.Dispose(); if (pendingIcon != null) pendingIcon.Dispose(); if (iconPreview.Image != null) iconPreview.Image.Dispose(); } base.Dispose(disposing); }
    }
}
