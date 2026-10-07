using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace LocalServiceManager
{
    public sealed class NewServiceDialog : Form
    {
        readonly ProjectPaths paths;
        readonly ServiceConfig prefill;
        readonly RadioButton independent = new RadioButton { Text = L.Text("独立后台服务"), AutoSize = true };
        readonly RadioButton client = new RadioButton { Text = L.Text("客户端托管 stdio MCP（仅登记）"), AutoSize = true };
        readonly ComboBox template = new ThemeCombo { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        readonly Label description = new Label { AutoSize = true, MaximumSize = new Size(590, 0) };
        readonly Button next = new ThemeButton(L.Text("填写配置"), "accent") { Width = 110, Height = 34, Enabled = false };
        public ServiceConfig Result { get; private set; }
        public NewServiceDialog(ProjectPaths paths, ServiceConfig prefill = null)
        {
            this.paths = paths; this.prefill = prefill;
            Text = L.Text("新增 · 选择服务类型"); Font = new Font("Microsoft YaHei UI", 9.5f); Size = new Size(680, 380); MinimumSize = new Size(610, 360); StartPosition = FormStartPosition.CenterParent; AppTheme.Form(this);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 6 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); for (int i = 0; i < 5; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); Controls.Add(root);
            root.Controls.Add(new Label { Text = L.Text("先选择管理方式，再填写信息。此步骤不保存、不执行。"), AutoSize = true }, 0, 0);
            independent.Margin = new Padding(0, 14, 0, 8); client.Margin = new Padding(0, 0, 0, 12); root.Controls.Add(independent, 0, 1); root.Controls.Add(client, 0, 2);
            template.Items.Add(L.Text("手工填写（通用后台服务）")); foreach (string name in ServiceTemplates.Names) template.Items.Add(ServiceTemplates.DisplayName(name)); template.SelectedIndex = 0; template.Enabled = false; root.Controls.Add(template, 0, 3);
            description.Margin = new Padding(0, 12, 0, 8); description.Text = L.Text("请选择服务类型。托管登记不会读取或修改 Codex / Claude 等客户端配置。"); root.Controls.Add(description, 0, 4);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, FlowDirection = FlowDirection.RightToLeft }; var cancel = new ThemeButton(L.Text("取消")) { DialogResult = DialogResult.Cancel, Width = 90, Height = 34 }; buttons.Controls.Add(cancel); buttons.Controls.Add(next); root.Controls.Add(buttons, 0, 5); AcceptButton = next; CancelButton = cancel;
            EventHandler mode = (o, e) => RefreshLanguage();
            independent.CheckedChanged += mode; client.CheckedChanged += mode;
            next.Click += (o, e) =>
            {
                try
                {
                    if (client.Checked)
                    {
                        Result = ServiceTemplates.ClientEntry();
                        if (prefill != null) { Result.Name = prefill.Name; Result.StartPath = prefill.StartPath; Result.StartArgs = prefill.StartArgs; Result.WorkingDirectory = prefill.WorkingDirectory; }
                    }
                    else if (independent.Checked)
                    {
                        if (prefill != null && template.SelectedIndex > 0 && L.MessageBox(this, L.Text("模板会替换拖入脚本的预填信息，但不会保存或执行。要替换吗？"), L.Text("替换预填信息"), MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                        Result = template.SelectedIndex == 0 ? (prefill == null ? new ServiceConfig { Reviewed = false } : prefill.Clone()) : ServiceTemplates.Create(ServiceTemplates.Names[template.SelectedIndex - 1], paths);
                    }
                    else return;
                    Result.AutoStart = false; DialogResult = DialogResult.OK; Close();
                }
                catch (Exception ex) { L.MessageBox(this, ex.Message, L.Text("尚未创建草稿"), MessageBoxButtons.OK, MessageBoxIcon.Information); }
            };
            AppTheme.Apply(this); if (prefill != null) independent.Checked = true;
            ClientSizeChanged += (o, e) => FitText(); L.Attach(this); L.Changed += RefreshLanguage; RefreshLanguage();
        }
        void FitText()
        {
            int width = Math.Max(300, ClientSize.Width - 48);
            description.MaximumSize = independent.MaximumSize = client.MaximumSize = new Size(width, 0);
            foreach (Control root in Controls) foreach (Control control in root.Controls) if (control is Label) control.MaximumSize = new Size(width, 0);
        }
        void RefreshLanguage()
        {
            if (IsDisposed) return;
            template.Enabled = independent.Checked; next.Enabled = independent.Checked || client.Checked;
            description.Text = client.Checked ? L.Text("填写名称、所属客户端和 MCP 命令/参数等登记信息。生命周期由所属客户端负责，本工具不检测实际调用，不独立启停 MCP。") : independent.Checked ? L.Text("可手工填写或预填已支持的模板；脚本只作配置，保存与启动分开。") : L.Text("请选择服务类型。托管登记不会读取或修改 Codex / Claude 等客户端配置。");
            FitText(); PerformLayout(); Invalidate(true);
        }
        protected override void Dispose(bool disposing) { if (disposing) { L.Changed -= RefreshLanguage; L.Detach(this); } base.Dispose(disposing); }
    }
}
