using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LocalServiceManager
{
    public sealed class MainForm : Form
    {
        public readonly ConfigRepository Repository;
        public readonly ServiceEngine Engine;
        readonly IStartupStore startup;
        readonly bool startInTray;
        public readonly ListView ServiceList = new ThemeList { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
        public readonly Button StartButton = MakeButton(L.Text("启动")), StopButton = MakeButton(L.Text("停止")), RestartButton = MakeButton(L.Text("重启")), EditButton = MakeButton(L.Text("配置")), AddButton = MakeButton(L.Text("新增")), OpenButton = MakeButton(L.Text("打开入口"));
        public readonly ContextMenuStrip TrayMenu = new ContextMenuStrip();
        readonly NotifyIcon tray;
        readonly ImageList icons = new ImageList { ImageSize = new Size(36, 36), ColorDepth = ColorDepth.Depth32Bit };
        readonly Label detail = new Label { AutoSize = false, Dock = DockStyle.Fill, MinimumSize = new Size(0, 58), Padding = new Padding(4, 4, 4, 0) };
        readonly Label counts = new Label { AutoSize = true, Tag = "muted", Margin = new Padding(0, 3, 16, 0) };
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 12000 };
        readonly ComboBox sources = new ThemeCombo { Width = 230, DropDownStyle = ComboBoxStyle.DropDownList };
        readonly TextBox search = new TextBox { Width = 175 };
        readonly CheckBox pause = new ThemeCheckBox { Text = L.Text("暂停滚动"), AutoSize = true, Margin = new Padding(10, 5, 5, 0) };
        readonly RichTextBox log = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, Font = new Font("Consolas", 9.5f), WordWrap = false, BackColor = Color.FromArgb(246, 248, 250), BorderStyle = BorderStyle.None, DetectUrls = false };
        readonly Timer timer = new Timer { Interval = 1000 };
        readonly Dictionary<string, TailReader> readers = new Dictionary<string, TailReader>();
        List<string> sourcePaths = new List<string>();
        string selectedLog = "", selectedServiceId = "", lastRendered = "";
        string logSourceError = "";
        DateTime nextPoll = DateTime.MinValue, nextSources = DateTime.MinValue;
        bool refreshBusy, allowExit, shuttingDown, rebuildingList, showingLogPlaceholder;
        const string ServiceDragFormat = "LocalServiceManager.ServiceId";
        readonly ContextMenuStrip listMenu = new ContextMenuStrip();
        Rectangle trayWorkArea;
        Point trayAnchor;
        public MainForm(ConfigRepository repository, ServiceEngine engine, IStartupStore startup, bool startInTray, bool runServiceStartup = true)
        {
            Repository = repository; Engine = engine; this.startup = startup; this.startInTray = startInTray;
            AppTheme.Form(this);
            Text = L.ProductName; Font = new Font("Microsoft YaHei UI", 9.5f); Size = new Size(1080, 800); MinimumSize = new Size(880, 650); StartPosition = FormStartPosition.CenterScreen; Icon = IconLibrary.AppIcon();
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 12), ColumnCount = 1, RowCount = 4 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); Controls.Add(root);
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 0, 0, 6), Margin = new Padding(0) };
            var remove = MakeButton(L.Text("删除")); var import = MakeButton(L.Text("导入")); var export = MakeButton(L.Text("导出")); var settings = MakeButton(L.Text("工具设置")); var refresh = MakeButton(L.Text("刷新"));
            settings.Width=100; OpenButton.Width=104; toolbar.Controls.AddRange(new Control[] { AddButton, EditButton, OpenButton, remove, import, export, settings, refresh }); root.Controls.Add(toolbar, 0, 0);
            var split = new SplitContainer { Size = new Size(980, 460), Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 285, Panel1MinSize = 100, Panel2MinSize = 120, SplitterWidth = 8 };
            ServiceList.Columns.Add(L.Text("服务"), 245); ServiceList.Columns.Add(L.Text("状态"), 90); ServiceList.Columns.Add(L.Text("管理方式"), 175); ServiceList.Columns.Add(L.Text("健康条件"), 340); ServiceList.Columns.Add(L.Text("服务自启"), 100); ServiceList.SmallImageList = icons; ServiceList.BorderStyle = BorderStyle.None;
            ServiceList.OwnerDraw = true; ServiceList.ShowItemToolTips = true;
            ServiceList.DrawColumnHeader += (o, e) => { using(var brush=new SolidBrush(AppTheme.Panel))e.Graphics.FillRectangle(brush,e.Bounds);TextRenderer.DrawText(e.Graphics,e.Header.Text,ServiceList.Font,new Rectangle(e.Bounds.Left+10,e.Bounds.Top,e.Bounds.Width-16,e.Bounds.Height),AppTheme.Muted,TextFormatFlags.VerticalCenter|TextFormatFlags.Left|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);using(var pen=new Pen(AppTheme.Border))e.Graphics.DrawLine(pen,e.Bounds.Left,e.Bounds.Bottom-1,e.Bounds.Right,e.Bounds.Bottom-1); };
            ServiceList.DrawItem += (o, e) => { if (ServiceList.View != View.Details) e.DrawDefault = true; };
            ServiceList.DrawSubItem += (o, e) =>
            {
                using (var background = new SolidBrush(e.Item.Selected ? AppTheme.Selection : AppTheme.Panel)) e.Graphics.FillRectangle(background, e.Bounds);
                using(var pen=new Pen(Color.FromArgb(28,28,28)))e.Graphics.DrawLine(pen,e.Bounds.Left,e.Bounds.Bottom-1,e.Bounds.Right,e.Bounds.Bottom-1);
                int pad=Math.Max(10,e.Bounds.Height/4);
                if(e.ColumnIndex==0){if(e.Item.Selected)using(var brush=new SolidBrush(AppTheme.Accent))e.Graphics.FillRectangle(brush,e.Bounds.Left,e.Bounds.Top,3,e.Bounds.Height);int size=Math.Min(28,e.Bounds.Height-8);var image=icons.Images[e.Item.ImageKey];if(image!=null)e.Graphics.DrawImage(image,e.Bounds.Left+pad,e.Bounds.Top+(e.Bounds.Height-size)/2,size,size);TextRenderer.DrawText(e.Graphics,e.SubItem.Text,ServiceList.Font,new Rectangle(e.Bounds.Left+pad+size+8,e.Bounds.Top,Math.Max(1,e.Bounds.Width-pad-size-16),e.Bounds.Height),AppTheme.Text,TextFormatFlags.VerticalCenter|TextFormatFlags.Left|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);return;}
                if (e.ColumnIndex != 1) { TextRenderer.DrawText(e.Graphics,e.SubItem.Text,ServiceList.Font,new Rectangle(e.Bounds.Left+pad,e.Bounds.Top,Math.Max(1,e.Bounds.Width-pad-6),e.Bounds.Height),e.ColumnIndex==2?AppTheme.Text:AppTheme.Muted,TextFormatFlags.VerticalCenter|TextFormatFlags.Left|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);return; }
                var v = Engine.Snapshot((ServiceConfig)e.Item.Tag);
                using (var brush = new SolidBrush(StateColor(v.State))) e.Graphics.FillEllipse(brush, e.Bounds.Left + 7, e.Bounds.Top + (e.Bounds.Height - 7) / 2, 7, 7);
                TextRenderer.DrawText(e.Graphics, ((ServiceConfig)e.Item.Tag).ClientManaged ? L.Text("仅登记") : v.Label, ServiceList.Font, new Rectangle(e.Bounds.Left + 20, e.Bounds.Top, e.Bounds.Width - 20, e.Bounds.Height), StateColor(v.State), TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            };
            split.Panel1.Controls.Add(ServiceList);
            ServiceList.ClientSizeChanged+=(o,e)=>FitColumns();
            var logLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 }; logLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); logLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); logLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var logbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 6, 0, 6), Margin = new Padding(0) };
            var copy = MakeButton(L.Text("复制日志")); copy.Width = 96;
            logbar.Controls.Add(new Label { Text = L.Text("日志"), AutoSize = true, Margin = new Padding(0, 5, 7, 0) }); logbar.Controls.Add(sources); logbar.Controls.Add(new Label { Text = L.Text("搜索"), AutoSize = true, Margin = new Padding(12, 5, 7, 0) }); logbar.Controls.Add(search); logbar.Controls.Add(pause); logbar.Controls.Add(copy);
            logLayout.Controls.Add(logbar, 0, 0); logLayout.Controls.Add(log, 0, 1); split.Panel2.Controls.Add(logLayout); root.Controls.Add(split, 0, 1);
            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Padding = new Padding(0,10,0,0) }; actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); actions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            actions.Controls.Add(detail, 0, 0); var actionbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0), Margin = new Padding(0) }; actionbar.Controls.Add(RestartButton); actionbar.Controls.Add(StopButton); actionbar.Controls.Add(StartButton); actions.Controls.Add(actionbar, 1, 0); root.Controls.Add(actions, 0, 2);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Padding = new Padding(0,5,0,0) }; footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.Controls.Add(counts, 0, 0); footer.Controls.Add(new Label { Text = repository.Paths.Root, Dock = DockStyle.Fill, AutoEllipsis = true, Tag = "muted", Margin = new Padding(0, 3, 0, 0), MinimumSize = new Size(0,24) }, 1, 0); root.Controls.Add(footer, 0, 3);
            AppTheme.Apply(this); AppTheme.Menu(TrayMenu); AppTheme.Menu(listMenu); ServiceList.ContextMenuStrip=listMenu; listMenu.Items.Add(L.Text("打开入口"),null,(o,e)=>OpenEntry(Selected())); listMenu.Items.Add(L.Text("配置"),null,(o,e)=>Edit(Selected())); listMenu.Items.Add(new ToolStripSeparator()); listMenu.Items.Add(L.Text("上移"),null,(o,e)=>MoveSelected(-1)); listMenu.Items.Add(L.Text("下移"),null,(o,e)=>MoveSelected(1)); listMenu.Opening+=(o,e)=>{var s=Selected();int index=s==null?-1:Repository.Current.Services.IndexOf(s);listMenu.Items[0].Enabled=listMenu.Items[1].Enabled=s!=null;listMenu.Items[3].Enabled=index>0;listMenu.Items[4].Enabled=index>=0&&index<Repository.Current.Services.Count-1;}; ServiceList.MouseDown+=(o,e)=>{if(e.Button==MouseButtons.Right){var hit=ServiceList.GetItemAt(e.X,e.Y);if(hit!=null)hit.Selected=true;}};
            tips.SetToolTip(AddButton,L.Text("新增配置，或拖入脚本预填；均不会自动执行。"));
            tips.SetToolTip(search,L.Text("筛选当前日志内容")); tips.SetToolTip(sources,L.Text("切换 stdout、stderr 或服务日志文件"));
            tray = new NotifyIcon { Icon = IconLibrary.AppIcon(), Text = L.ProductName, Visible = true, ContextMenuStrip = TrayMenu };
            // Right-click menu is handled once by NotifyIcon.ContextMenuStrip.
            tray.MouseClick += (o, e) => { if (e.Button == MouseButtons.Left) ShowMain(); };
            TrayMenu.Opening += (o, e) => { trayAnchor=Cursor.Position;trayWorkArea=Screen.FromPoint(trayAnchor).WorkingArea;BuildTray(); };
            TrayMenu.Opened += (o,e) => TrayMenu.Location=TrayPlacement.Fit(new Rectangle(trayAnchor.X-TrayMenu.Width,trayAnchor.Y-TrayMenu.Height,TrayMenu.Width,TrayMenu.Height),trayWorkArea).Location;
            AddButton.Click += (o, e) => AddEntry(); OpenButton.Click += (o, e) => OpenEntry(Selected()); EditButton.Click += (o, e) => Edit(Selected()); remove.Click += (o, e) => Remove();
            import.Click += (o, e) => Import(); export.Click += (o, e) => Export(); settings.Click += (o, e) => Settings(); refresh.Click += async (o, e) => await RefreshNow();
            StartButton.Click += async (o, e) => await RunSelected("start"); StopButton.Click += async (o, e) => await RunSelected("stop"); RestartButton.Click += async (o, e) => await RunSelected("restart");
            ServiceList.SelectedIndexChanged += (o, e) => { if (!rebuildingList) { UpdateActions(); RefreshSources(true); } }; ServiceList.DoubleClick += (o, e) => OpenEntry(Selected());
            ServiceList.KeyDown += (o, e) => { if (e.KeyCode == Keys.F5) { nextPoll = DateTime.MinValue; e.Handled = true; } else if (e.KeyCode == Keys.Enter) { OpenEntry(Selected()); e.Handled = true; } else if (e.KeyCode == Keys.F2) { Edit(Selected()); e.Handled = true; } };
            copy.Click += (o, e) => { if (log.Text.Length > 0) Clipboard.SetText(log.SelectionLength > 0 ? log.SelectedText : log.Text); };
            search.TextChanged += (o, e) => RenderLog(true); pause.CheckedChanged += (o, e) => { if (!pause.Checked) RenderLog(true); };
            sources.SelectedIndexChanged += (o, e) => { selectedLog = sources.SelectedIndex >= 0 && sources.SelectedIndex < sourcePaths.Count ? sourcePaths[sources.SelectedIndex] : ""; RenderLog(true); };
            AllowDrop = true; ServiceList.AllowDrop = true; DragEnter += DragOverFiles; DragDrop += DropFiles; ServiceList.ItemDrag += DragService; ServiceList.DragEnter += ListDragOver; ServiceList.DragOver += ListDragOver; ServiceList.DragDrop += ListDrop; ServiceList.DragLeave += (o,e) => ServiceList.InsertionMark.Index = -1;
            Engine.Changed += OnEngineChanged;
            timer.Tick += async (o, e) =>
            {
                if (!refreshBusy && DateTime.UtcNow >= nextPoll) await RefreshNow();
                if (Visible) { if (DateTime.UtcNow >= nextSources) RefreshSources(false); ReadLog(); }
            };
            Shown += async (o, e) =>
            {
                if (startInTray) Hide(); await RefreshNow();
                if (runServiceStartup) foreach (var s in Repository.Current.Services.Where(x => x.AutoStart && x.Reviewed && !x.ClientManaged).ToArray()) { try { await Engine.OperateAsync(s, "start"); } catch (Exception ex) { Engine.Logs.Event(L.Text("自动启动未完成：") + s.Name + " / " + ex.Message); } }
            };
            FormClosing += (o, e) => { if (!allowExit) { e.Cancel = true; Hide(); nextPoll = DateTime.UtcNow.AddSeconds(15); } };
            VisibleChanged += (o, e) => { timer.Interval = Visible ? 1000 : 5000; };
            FormClosed += (o, e) => { timer.Stop(); tray.Visible = false; Engine.Changed -= OnEngineChanged; Engine.Dispose(); };
            L.Attach(this);L.Changed+=RefreshLanguage; RebuildList(); RefreshLanguage();timer.Start();
        }
        static Button MakeButton(string text) { return new ThemeButton(text,text==L.Text("新增")?"accent":text==L.Text("启动")?"start":text==L.Text("停止")?"stop":text==L.Text("重启")?"restart":"normal"); }
        void RefreshLanguage()
        {
            Text=L.ProductName;tray.Text=L.ProductName;
            L.Tip(tips,AddButton,"新增配置，或拖入脚本预填；均不会自动执行。");
            L.Tip(tips,search,"筛选当前日志内容");L.Tip(tips,sources,"切换 stdout、stderr 或服务日志文件");
            PaintStates();L.TranslateMenu(listMenu);L.TranslateMenu(TrayMenu);
            int serviceIndex=0;
            foreach(ToolStripMenuItem entry in TrayMenu.Items.OfType<ToolStripMenuItem>())
            {
                if(!(entry.Tag is ServiceSnapshot))continue;
                if(serviceIndex>=Repository.Current.Services.Count)break;
                var service=Repository.Current.Services[serviceIndex++];var snapshot=Engine.Snapshot(service);
                entry.Text=service.Name+"   ● "+(service.ClientManaged?L.Text("仅登记"):snapshot.Label);
            }
            TrayMenu.PerformLayout();
            if(TrayMenu.Visible)TrayMenu.Location=TrayPlacement.Fit(new Rectangle(TrayMenu.Location,TrayMenu.Size),trayWorkArea).Location;
            if(!pause.Checked)RenderLog(true);
            else if(showingLogPlaceholder) { int position=log.SelectionStart;int length=log.SelectionLength;lastRendered=L.Text("尚无日志。后台 stdout / stderr 写入项目 logs；可配置已有文件 tail。");if(logSourceError.Length>0)lastRendered=L.Display(logSourceError)+"\r\n\r\n"+lastRendered;log.Text=lastRendered;log.Select(Math.Min(position,log.TextLength),Math.Min(length,Math.Max(0,log.TextLength-position))); }
            FitColumns();ServiceList.Invalidate();
        }

        void FitColumns(){if(ServiceList.Columns.Count!=5)return;int width=Math.Max(500,ServiceList.ClientSize.Width-12);int[] weights={28,10,19,31,12};int used=0;for(int i=0;i<4;i++){ServiceList.Columns[i].Width=width*weights[i]/100;used+=ServiceList.Columns[i].Width;}ServiceList.Columns[4].Width=Math.Max(70,width-used);((ThemeList)ServiceList).FinishColumns();}
        public ServiceConfig Selected() { return ServiceList.SelectedItems.Count == 0 ? null : ServiceList.SelectedItems[0].Tag as ServiceConfig; }
        public static Color StateColor(ServiceState state) { return state == ServiceState.Running ? AppTheme.Green : state == ServiceState.Stopped ? AppTheme.Red : state == ServiceState.Transition ? AppTheme.Yellow : state == ServiceState.Error ? AppTheme.Orange : AppTheme.Muted; }
        public void RebuildList()
        {
            string selected = Selected() == null ? selectedServiceId : Selected().Id;
            rebuildingList=true; string topId=ServiceList.TopItem==null?null:((ServiceConfig)ServiceList.TopItem.Tag).Id; ServiceList.BeginUpdate(); ServiceList.Items.Clear(); icons.Images.Clear(); var imageHandle = icons.Handle;
            foreach (var s in Repository.Current.Services)
            {
                using (var bitmap = IconLibrary.Load(s.Icon, Repository.Paths, 28)) icons.Images.Add(s.Id, bitmap);
                var item = new ListViewItem(s.Name) { Tag = s, ImageKey = s.Id, UseItemStyleForSubItems = false };
                item.SubItems.AddRange(new[] { L.Text("未知"), "—", L.Text("尚未检查"), s.AutoStart ? L.Text("开启") : L.Text("关闭") }); ServiceList.Items.Add(item); if (s.Id == selected) item.Selected = true;
            }
            if (ServiceList.SelectedItems.Count == 0 && ServiceList.Items.Count > 0) ServiceList.Items[0].Selected = true;
            ServiceList.EndUpdate(); rebuildingList=false; if(topId!=null){var top=ServiceList.Items.Cast<ListViewItem>().FirstOrDefault(row=>((ServiceConfig)row.Tag).Id==topId);if(top!=null)ServiceList.TopItem=top;} PaintStates(); RefreshSources(true); BuildTray();
        }
        void PaintStates()
        {
            foreach (ListViewItem item in ServiceList.Items)
            {
                var s = (ServiceConfig)item.Tag; var value = Engine.Snapshot(s);
                item.SubItems[1].Text = "● " + (s.ClientManaged ? L.Text("仅登记") : value.Label); item.SubItems[1].ForeColor = StateColor(value.State); item.SubItems[2].Text = L.Display(value.Owner);
                item.SubItems[3].Text = !s.ClientManaged && !s.Reviewed ? L.Text("待核对，服务执行已禁用") : L.Display(value.Detail);
                item.SubItems[4].Text = s.ClientManaged ? L.Text("客户端负责") : s.AutoStart ? L.Text("开启") : L.Text("关闭");
                item.ToolTipText = s.Name + "\r\n" + L.Display(value.Owner) + "\r\n" + L.Display(value.Detail);
            }
            counts.Text = L.Format("{0} 个服务 · {1} 运行",Repository.Current.Services.Count,Repository.Current.Services.Count(x => Engine.Snapshot(x).State == ServiceState.Running)); UpdateActions();
        }
        void UpdateActions()
        {
            var s = Selected(); var v = s == null ? new ServiceSnapshot() : Engine.Snapshot(s); bool enabled = s != null && !v.Busy && !shuttingDown;
            StartButton.Enabled = enabled && s.Reviewed && !s.ClientManaged && v.State == ServiceState.Stopped;
            StopButton.Enabled = enabled && s.Reviewed && !s.ClientManaged && (v.Owned || v.ControllerValid) && s.StopPath.Length > 0 && (s.Adapter != ImageWorkbench.Adapter || (v.ControllerValid && !v.TasksActive));
            RestartButton.Enabled = StopButton.Enabled;
            EditButton.Enabled = s != null && !shuttingDown;
            OpenButton.Enabled = s != null && !shuttingDown;
            detail.Text = s == null ? L.Text("新增条目，或把启动脚本拖入窗口。") : s.Name + "  ·  " + L.Display(v.Owner) + "\r\n" + (!s.ClientManaged && !s.Reviewed ? L.Text("请先配置核对，才可执行服务操作。") : L.Display(v.Detail));
            if (s != null) selectedServiceId = s.Id;
        }
        void OnEngineChanged() { if (IsDisposed || !IsHandleCreated) return; BeginInvoke((Action)(() => { if (!IsDisposed) { PaintStates(); if (!TrayMenu.Visible) BuildTray(); } })); }
        public async Task RefreshNow()
        {
            if (refreshBusy) return; refreshBusy = true;
            try { await Engine.RefreshAsync(Repository.Current.Services.Select(x => x.Clone()).ToArray()); PaintStates(); }
            catch (Exception ex) { Engine.Logs.Event(L.Text("轮询失败：") + ex.ToString()); }
            finally { refreshBusy = false; nextPoll = DateTime.UtcNow.AddSeconds(Visible ? 3 : 15); }
        }
        public async Task Run(ServiceConfig s, string action)
        {
            if (s == null || shuttingDown) return;
            try { await Engine.OperateAsync(s, action); }
            catch (Exception ex) { L.MessageBox(this, ex.Message, L.Text("操作未完成"), MessageBoxButtons.OK, MessageBoxIcon.Information); }
            RefreshSources(true); PaintStates();
        }
        async Task RunSelected(string action) { await Run(Selected(), action); }
        void AddEntry(ServiceConfig prefill = null)
        {
            using (var chooser = new NewServiceDialog(Repository.Paths, prefill)) if (chooser.ShowDialog(this) == DialogResult.OK) EditNew(chooser.Result);
        }
        public void OpenEntry(ServiceConfig s)
        {
            if (s == null || shuttingDown) return;
            try { ServiceOpener.Open(s, Repository.Paths, Engine.Snapshot(s)); }
            catch (Exception ex) { L.MessageBox(this, ex.Message, L.Text("打开入口"), MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }
        public bool MoveService(string id, int insertionIndex)
        {
            if (shuttingDown) return false;
            var before = Repository.Current.Services.ToList(); int from = before.FindIndex(s => s.Id == id);
            if (from < 0) return false;
            int target = Math.Max(0, Math.Min(insertionIndex, before.Count)); if (from < target) target--;
            if (from == target) return false;
            var reordered = before.ToList(); var moved = reordered[from]; reordered.RemoveAt(from); reordered.Insert(target, moved);
            Repository.Current.Services = reordered;
            try { Repository.Save(); } catch { Repository.Current.Services = before; throw; }
            RebuildList(); return true;
        }
        void MoveSelected(int direction)
        {
            var s = Selected(); if (s == null) return; int index = Repository.Current.Services.IndexOf(s);
            try { MoveService(s.Id, direction < 0 ? index - 1 : index + 2); } catch (Exception ex) { L.MessageBox(this, ex.Message, L.Text("排序未保存")); }
        }
        void DragService(object sender, ItemDragEventArgs e)
        {
            if (e.Button != MouseButtons.Left || shuttingDown) return;
            var item = e.Item as ListViewItem; if (item == null) return;
            try { ServiceList.DoDragDrop(new DataObject(ServiceDragFormat, ((ServiceConfig)item.Tag).Id), DragDropEffects.Move); }
            finally { ServiceList.InsertionMark.Index = -1; }
        }
        int DropIndex(Point point)
        {
            var item = ServiceList.GetItemAt(point.X, point.Y);
            return item == null ? Repository.Current.Services.Count : item.Index + (point.Y >= item.Bounds.Top + item.Bounds.Height / 2 ? 1 : 0);
        }
        void ListDragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(ServiceDragFormat)) { DragOverFiles(sender,e); return; }
            var id = e.Data.GetData(ServiceDragFormat) as string;
            if (shuttingDown || !Repository.Current.Services.Any(s => s.Id == id)) { e.Effect = DragDropEffects.None; return; }
            e.Effect = DragDropEffects.Move; var point = ServiceList.PointToClient(new Point(e.X,e.Y)); int index = DropIndex(point);
            ServiceList.InsertionMark.Index = Math.Min(index,ServiceList.Items.Count-1); ServiceList.InsertionMark.AppearsAfterItem = index == ServiceList.Items.Count;
        }
        void ListDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(ServiceDragFormat)) { DropFiles(sender,e); return; }
            try { MoveService(Convert.ToString(e.Data.GetData(ServiceDragFormat)),DropIndex(ServiceList.PointToClient(new Point(e.X,e.Y)))); }
            catch (Exception ex) { L.MessageBox(this,ex.Message,L.Text("排序未保存")); }
            finally { ServiceList.InsertionMark.Index=-1; }
        }

        void Edit(ServiceConfig existing)
        {
            using (var editor = new ServiceEditor(Repository.Paths, existing ?? new ServiceConfig(), existing == null ? (Func<ServiceSnapshot>)null : () => Engine.Snapshot(existing), (value, image) => CommitEditor(existing, value, image), existing == null)) editor.ShowDialog(this);
        }
        void EditNew(ServiceConfig value)
        {
            using(var editor=new ServiceEditor(Repository.Paths,value,null,(result,image)=>CommitEditor(null,result,image),true))editor.ShowDialog(this);
        }
        void CommitEditor(ServiceConfig existing, ServiceConfig value, Bitmap image)
        {
            Action commit = () =>
            {
                int index = existing == null ? -1 : Repository.Current.Services.IndexOf(existing);
                if (existing != null && index < 0) throw new InvalidOperationException(L.Text("服务条目已变化，请取消并重新打开配置。"));
                if (image != null) value.Icon = IconLibrary.SaveBitmap(image, Repository.Paths);
                if (existing == null) Repository.Current.Services.Add(value); else Repository.Current.Services[index] = value;
                try { Repository.Save(); }
                catch { if (existing == null) Repository.Current.Services.Remove(value); else Repository.Current.Services[index] = existing; throw; }
            };
            if (existing == null) commit(); else Engine.CommitConfiguration(existing, commit);
            RebuildList(); nextPoll = DateTime.MinValue;
        }
        public void PrefillDrop(string path)
        {
            AddEntry(ServiceEditor.Prefill(path));
        }
        void DragOverFiles(object sender, DragEventArgs e) { e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; }
        void DropFiles(object sender, DragEventArgs e) { if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return; foreach (var file in ((string[])e.Data.GetData(DataFormats.FileDrop)).Take(10)) if (File.Exists(file)) PrefillDrop(file); }
        void SaveAndRebuild() { try { Repository.Save(); RebuildList(); nextPoll = DateTime.MinValue; } catch (Exception ex) { L.MessageBox(this, ex.Message, L.Text("保存失败")); } }
        void Remove()
        {
            var s = Selected(); if (s == null) return;
            if (Engine.Snapshot(s).Owned || Engine.Snapshot(s).Busy) { L.MessageBox(this, L.Text("先停止本工具管理的服务，再删除条目。")); return; }
            if (L.MessageBox(this, L.Format("删除配置条目“{0}”？脚本和服务进程不会被删除或停止。",s.Name), L.Text("删除条目"), MessageBoxButtons.OKCancel) == DialogResult.OK) { Repository.Current.Services.Remove(s); SaveAndRebuild(); }
        }
        void Import()
        {
            using (var pick = new OpenFileDialog { Title=L.Text("导入服务配置"),Filter = L.Text("服务配置 JSON|*.json") }) if (pick.ShowDialog(this) == DialogResult.OK)
            {
                try
                {
                    var entries = Repository.ReadImport(pick.FileName);
                    if (Repository.Current.Services.Count + entries.Count > 100) throw new InvalidDataException(L.Text("合并后超过 100 个条目"));
                    if (L.MessageBox(this, L.Format("追加 {0} 个条目：\r\n{1}\r\n所有自启关闭，执行禁用；需逐项编辑核对。",entries.Count,String.Join("\r\n",entries.Take(12).Select(x=>"• "+x.Name))), L.Text("导入预览"), MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                    Repository.Current.Services.AddRange(entries); SaveAndRebuild();
                }
                catch (Exception ex) { L.MessageBox(this, ex.Message, L.Text("导入失败")); }
            }
        }
        void Export()
        {
            using (var save = new SaveFileDialog { Title=L.Text("导出服务配置"),Filter = L.Text("服务配置 JSON|*.json"), FileName = "services-export.json" }) if (save.ShowDialog(this) == DialogResult.OK)
            {
                try { File.WriteAllText(save.FileName, Repository.Export(), new System.Text.UTF8Encoding(false)); L.MessageBox(this, L.Text("已导出。所有参数、HTTP 查询参数、进程凭据和私有图标被排除；不导出日志。路径和名称仍需自行核对。")); }
                catch (Exception ex) { L.MessageBox(this, ex.Message, L.Text("导出失败")); }
            }
        }
        void Settings()
        {
            using(var form=new ToolSettingsForm(Repository,startup))form.ShowDialog(this);
        }
        void RefreshSources(bool force)
        {
            nextSources = DateTime.UtcNow.AddSeconds(5); var s = Selected(); string old = selectedLog;
            var files = s == null || s.ClientManaged ? new List<string>() : Engine.CaptureFiles(s).Where(File.Exists).ToList();
            string sourceError = "";
            if (s != null && !s.ClientManaged)
            {
                try { string external = TailReader.ResolveLatest(Repository.Paths, s.LogPath); if (!String.IsNullOrEmpty(external)) files.Insert(0, external); }
                catch (Exception ex)
                {
                    if (!(ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is NotSupportedException)) throw;
                    sourceError = L.Format("日志未读取：{0}。请在服务编辑的日志路径中检查文件名与访问权限。",L.Dynamic(ex.Message));
                    if (sourceError != logSourceError) Engine.Logs.Event(s.Name + "：" + sourceError);
                }
            }
            bool errorChanged = sourceError != logSourceError; logSourceError = sourceError;
            if (s != null && s.Adapter == LocalBridge.Adapter) foreach (string file in LocalBridge.LogFiles()) if (File.Exists(file) && !files.Contains(file)) files.Add(file);
            if (s != null && s.Adapter == ImageWorkbench.Adapter) foreach (string file in ImageWorkbench.LogFiles(s, Repository.Paths)) if (File.Exists(file) && !files.Contains(file)) files.Add(file);
            files.Add(Path.Combine(Repository.Paths.Logs, "manager.log")); files = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!force && !errorChanged && files.SequenceEqual(sourcePaths)) return;
            sourcePaths = files; sources.Items.Clear(); foreach (string path in files) sources.Items.Add(Path.GetFileName(path));
            int index = files.IndexOf(old); sources.SelectedIndex = index >= 0 ? index : 0; ReadLog();
        }
        void ReadLog()
        {
            if (String.IsNullOrEmpty(selectedLog) || pause.Checked) return;
            TailReader active; if (!readers.TryGetValue(selectedLog, out active)) { active = new TailReader(); if (readers.Count >= 6) readers.Clear(); readers[selectedLog] = active; }
            var s = Selected(); active.Read(selectedLog, selectedLog.EndsWith("manager.log") || s == null ? "utf-8" : s.EncodingName); RenderLog(false);
        }
        void RenderLog(bool force)
        {
            if (pause.Checked) return; TailReader active;
            showingLogPlaceholder=!readers.TryGetValue(selectedLog,out active);
            string text = showingLogPlaceholder ? L.Text("尚无日志。后台 stdout / stderr 写入项目 logs；可配置已有文件 tail。") : active.Buffer.Text(search.Text);
            if (logSourceError.Length > 0) text = L.Display(logSourceError) + "\r\n\r\n" + text;
            if (Selected() != null && Selected().Adapter == LocalBridge.Adapter) text = LocalBridge.Redact(text);
            if (!force && text == lastRendered) return; lastRendered = text; log.Text = text; log.SelectionStart = log.TextLength; log.ScrollToCaret();
        }
        public void BuildTray()
        {
            // Keep item identity/hover stable until the entire menu session closes.
            if(TrayMenu.Visible||TrayMenu.Items.OfType<ToolStripMenuItem>().Any(x=>x.DropDown.Visible))return;
            while (TrayMenu.Items.Count > 0) { var old = TrayMenu.Items[0]; TrayMenu.Items.RemoveAt(0); if (old.Image != null) old.Image.Dispose(); old.Dispose(); }
            TrayMenu.Items.Add(L.Text("打开主窗口"), null, (o, e) => ShowMain()); TrayMenu.Items.Add(new ToolStripSeparator());
            foreach (var s in Repository.Current.Services)
            {
                var v = Engine.Snapshot(s); var item = new ToolStripMenuItem(s.Name + "   ● " + (s.ClientManaged ? L.Text("仅登记") : v.Label)) { Enabled=true, Image = IconLibrary.Load(s.Icon, Repository.Paths, 20), Tag = v, DropDownDirection=ToolStripDropDownDirection.Right };
                var drop=new TrayDropDown { WorkArea=trayWorkArea,Renderer=TrayMenu.Renderer,BackColor=AppTheme.Panel,ForeColor=AppTheme.Text };
                item.DropDown=drop;
                item.DropDownOpening+=(o,e)=>{drop.WorkArea=trayWorkArea;var row=item.Owner.RectangleToScreen(item.Bounds);var size=drop.GetPreferredSize(Size.Empty);item.DropDownDirection=row.Right-2+size.Width<=trayWorkArea.Right?ToolStripDropDownDirection.Right:ToolStripDropDownDirection.Left;};
                drop.Opened+=(o,e)=>drop.Bounds=TrayPlacement.Submenu(item.Owner.RectangleToScreen(item.Bounds),drop.Size,trayWorkArea);
                var start = new ToolStripMenuItem(L.Text("启动")) { Enabled = s.Reviewed && !s.ClientManaged && !v.Busy && v.State == ServiceState.Stopped };
                var stop = new ToolStripMenuItem(L.Text("停止")) { Enabled = s.Reviewed && !s.ClientManaged && !v.Busy && (v.Owned || v.ControllerValid) && s.StopPath.Length > 0 && (s.Adapter != ImageWorkbench.Adapter || (v.ControllerValid && !v.TasksActive)) };
                var restart = new ToolStripMenuItem(L.Text("重启")) { Enabled = stop.Enabled };
                start.Click += async (o, e) => await Run(s, "start"); stop.Click += async (o, e) => await Run(s, "stop"); restart.Click += async (o, e) => await Run(s, "restart");
                item.DropDownItems.Add(L.Text("打开入口"), null, (o, e) => OpenEntry(s)); item.DropDownItems.Add(L.Text("配置"), null, (o, e) => { ShowMain(); Edit(s); }); item.DropDownItems.Add(new ToolStripSeparator());
                item.DropDownItems.AddRange(new ToolStripItem[] { start, stop, restart }); item.DropDownItems.Add(L.Text("查看日志"), null, (o, e) => { ShowMain(); foreach (ListViewItem row in ServiceList.Items) row.Selected = ((ServiceConfig)row.Tag).Id == s.Id; });
                TrayMenu.Items.Add(item);
            }
            if (Repository.Current.Services.Count == 0) TrayMenu.Items.Add(new ToolStripMenuItem(L.Text("暂无服务，请打开主窗口新增")) { Enabled = false });
            TrayMenu.Items.Add(new ToolStripSeparator()); TrayMenu.Items.Add(L.Text("退出工具（服务继续运行）"), null, async (o, e) => await ExitAsync(false)); TrayMenu.Items.Add(L.Text("停止本工具管理的服务后退出"), null, async (o, e) => await ExitAsync(true));
        }
        public void ShowMain() { Show(); WindowState = FormWindowState.Normal; Activate(); nextPoll = DateTime.MinValue; }
        public async Task ExitAsync(bool stopServices)
        {
            if (Engine.AnyBusy) { L.MessageBox(this, L.Text("服务操作尚未完成，请等待后退出。")); return; }
            if (stopServices)
            {
                shuttingDown = true; UpdateActions(); await RefreshNow();
                try
                {
                    foreach (var s in Repository.Current.Services.Where(x => Engine.Snapshot(x).Owned).ToArray()) await Engine.OperateAsync(s, "stop");
                }
                catch (Exception ex) { shuttingDown = false; UpdateActions(); ShowMain(); L.MessageBox(this, L.Format("未退出：{0}\r\n其余服务保留当前状态。",L.Dynamic(ex.Message))); return; }
            }
            allowExit = true; Close();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { L.Changed-=RefreshLanguage;L.Detach(this);timer.Dispose(); tray.Dispose(); icons.Dispose(); TrayMenu.Dispose(); tips.Dispose(); }
            base.Dispose(disposing);
        }
    }
    sealed class StateMenuRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var v = e.Item.Tag as ServiceSnapshot; int split = e.Text.LastIndexOf("   ● ", StringComparison.Ordinal);
            if (v == null || split < 0) { base.OnRenderItemText(e); return; }
            string name = e.Text.Substring(0, split); TextRenderer.DrawText(e.Graphics, name, e.TextFont, e.TextRectangle, e.TextColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            int x = e.TextRectangle.Left + TextRenderer.MeasureText(e.Graphics, name, e.TextFont, Size.Empty, TextFormatFlags.NoPrefix).Width + 9;
            using (var brush = new SolidBrush(MainForm.StateColor(v.State))) e.Graphics.FillEllipse(brush, x, e.TextRectangle.Top + (e.TextRectangle.Height - 7) / 2, 7, 7);
            TextRenderer.DrawText(e.Graphics, e.Text.Substring(split + "   ● ".Length), e.TextFont, new Rectangle(x + 12, e.TextRectangle.Top, e.TextRectangle.Right - x - 12, e.TextRectangle.Height), MainForm.StateColor(v.State), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }
}
