using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace LocalServiceManager
{
    public static class ServiceTemplates
    {
        // Generic drafts and client-managed stdio entries are supplied by the new-entry dialog.
        public static readonly string[] Names = { "ComfyUI" };
        public static string DisplayName(string name) { return L.Text(name); }
        public static string Installation(string name) { return ""; }
        public static string Description(ServiceConfig s)
        {
            if (s.ClientManaged || s.Adapter == "stdio") return L.Text("仅登记 MCP 信息；所属客户端负责启动和关闭。不检测实际工具调用，不修改客户端 MCP 配置。");
            if (s.Adapter == "comfy") return L.Text("检查后端状态；有运行或排队任务时保护停止与重启。");
            return L.Text("配置启动入口、停止方式和状态检测。");
        }
        public static ServiceConfig ClientEntry()
        {
            return new ServiceConfig { Name = L.Text("MCP 登记"), Icon = "builtin:mcp", Adapter = "stdio", ClientManaged = true, AutoStart = false, Reviewed = false };
        }
        public static ServiceConfig Create(string name, ProjectPaths paths, string installation = null)
        {
            name = L.Canonical(name);
            if (!Names.Contains(name)) throw new ArgumentException(L.Text("请选择已提供的服务模板。"));
            string directory = String.IsNullOrWhiteSpace(installation) ? "" : installation.Trim();
            ProjectPaths.CheckPathText(directory);
            if (directory.Length > 0)
            {
                directory = paths.Resolve(directory);
                if (!Directory.Exists(directory)) throw new InvalidDataException(L.Text("安装工作目录不存在，请重新定位完整安装。"));
            }
            var result = new ServiceConfig {
                Name = L.Text("ComfyUI 后端"), Icon = "builtin:comfy", Adapter = "comfy",
                WorkingDirectory = directory, CommandContains = "main.py", Port = 8188,
                HealthUrl = "http://127.0.0.1:8188/system_stats", TimeoutSeconds = 120,
                AutoStart = false, Reviewed = false
            };
            ConfigRepository.Validate(new AppConfig { Services = new List<ServiceConfig> { result } });
            return result;
        }
        public static void ValidateNewDraft(ServiceConfig value, ProjectPaths paths)
        {
            if (value == null || value.Adapter != "comfy" || value.ClientManaged) throw new InvalidDataException(L.Text("适配模式无效"));
            // User scripts, executables, arguments and directories remain freely editable.
            // Lifecycle authorization, process identity and queue checks are enforced by the engine.
            ConfigRepository.Validate(new AppConfig { Services = new List<ServiceConfig> { value } });
        }
    }
    public sealed class TemplatePicker : Form
    {
        sealed class TemplateChoice
        {
            public readonly string Name;
            public TemplateChoice(string name) { Name = name; }
            public override string ToString() { return ServiceTemplates.DisplayName(Name); }
        }
        readonly ProjectPaths paths;
        readonly ComboBox selection = new ThemeCombo { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly TextBox location = new TextBox();
        readonly Label explanation = new Label { AutoSize = true, MaximumSize = new Size(590, 0) };
        bool refreshingLanguage;
        public ServiceConfig Result { get; private set; }
        string SelectedName { get { var choice = selection.SelectedItem as TemplateChoice; return choice == null ? "" : choice.Name; } }
        void UpdateExplanation()
        {
            explanation.Text = L.Text("预填 ComfyUI 健康检测与队列保护；请填写自己的启动/停止脚本和进程 EXE。");
        }
        void RefreshLanguage()
        {
            int selected = selection.SelectedIndex;
            refreshingLanguage = true;
            try { for (int i = 0; i < selection.Items.Count; i++) selection.Items[i] = new TemplateChoice(((TemplateChoice)selection.Items[i]).Name); selection.SelectedIndex = selected; }
            finally { refreshingLanguage = false; }
            UpdateExplanation();
        }
        public TemplatePicker(ProjectPaths paths)
        {
            this.paths = paths; Text = L.Text("预填后台服务模板"); Font = new Font("Microsoft YaHei UI", 9.5f); Size = new Size(730, 380); MinimumSize = new Size(670, 355); StartPosition = FormStartPosition.CenterParent; AppTheme.Form(this);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 5 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); for (int i = 0; i < 4; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); Controls.Add(root);
            root.Controls.Add(new Label { Text = L.Text("只预填当前后台服务草稿；不会保存或启动。更换模板前会确认草稿替换。"), AutoSize = true, MaximumSize = new Size(590, 0) }, 0, 0);
            foreach (string name in ServiceTemplates.Names) selection.Items.Add(new TemplateChoice(name)); selection.Dock = DockStyle.Fill; selection.Margin = new Padding(0, 12, 0, 10); root.Controls.Add(selection, 0, 1);
            root.Controls.Add(explanation, 0, 2);
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 12, 0, 8) }; row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105)); location.Dock = DockStyle.Fill; var browse = new ThemeButton(L.Text("定位…")) { Dock = DockStyle.Fill, Height = 30 }; row.Controls.Add(location); row.Controls.Add(browse); root.Controls.Add(row, 0, 3);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, FlowDirection = FlowDirection.RightToLeft }; var cancel = new ThemeButton(L.Text("取消")) { DialogResult = DialogResult.Cancel, Width = 90, Height = 34 }; var next = new ThemeButton(L.Text("预填配置"), "accent") { Width = 175, Height = 34 }; buttons.Controls.Add(cancel); buttons.Controls.Add(next); root.Controls.Add(buttons, 0, 4); AcceptButton = next; CancelButton = cancel;
            selection.SelectedIndexChanged += (o, e) => { if (refreshingLanguage) return; location.Text = ServiceTemplates.Installation(SelectedName); UpdateExplanation(); }; selection.SelectedIndex = 0;
            browse.Click += (o, e) => { using (var dialog = new FolderBrowserDialog { Description = L.Text("可选：选择 ComfyUI 工作目录"), SelectedPath = Directory.Exists(location.Text) ? location.Text : "" }) if (dialog.ShowDialog(this) == DialogResult.OK) location.Text = dialog.SelectedPath; };
            next.Click += (o, e) => { try { Result = ServiceTemplates.Create(SelectedName, paths, location.Text.Trim()); DialogResult = DialogResult.OK; Close(); } catch (Exception ex) { L.MessageBox(this, ex.Message, L.Text("尚未预填"), MessageBoxButtons.OK, MessageBoxIcon.Information); } };
            AppTheme.Apply(this); L.Attach(this); L.Changed += RefreshLanguage; Disposed += (o, e) => { L.Changed -= RefreshLanguage; };
        }
    }
}
