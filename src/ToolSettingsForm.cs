using System;
using System.Drawing;
using System.Windows.Forms;

namespace LocalServiceManager
{
    public sealed class ToolSettingsForm : Form
    {
        readonly ConfigRepository repository;
        readonly IStartupStore startup;
        readonly bool initialLogin, actualLogin;
        readonly CheckBox login = new ThemeCheckBox { AutoSize=true };
        readonly ComboBox language = new ThemeCombo { DropDownStyle=ComboBoxStyle.DropDownList,Width=250 };
        readonly Label status = new Label { AutoSize=true,MaximumSize=new Size(580,0) };
        readonly Label languageHint = new Label { AutoSize=true,MaximumSize=new Size(580,0),Tag="muted" };
        bool initializing;
        public ToolSettingsForm(ConfigRepository repository,IStartupStore startup)
        {
            this.repository=repository;this.startup=startup;initialLogin=repository.Current.LoginStartup;
            actualLogin=startup.IsEnabled(Application.ExecutablePath);
            Font=new Font("Microsoft YaHei UI",9.5f);Size=new Size(680,400);MinimumSize=new Size(640,380);
            StartPosition=FormStartPosition.CenterParent;FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=MinimizeBox=false;AppTheme.Form(this);
            var root=new TableLayoutPanel { Dock=DockStyle.Fill,Padding=new Padding(24),ColumnCount=1,RowCount=6 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));for(int i=0;i<5;i++)root.RowStyles.Add(new RowStyle(SizeType.AutoSize));root.RowStyles.Add(new RowStyle(SizeType.Percent,100));Controls.Add(root);
            root.Controls.Add(new Label { Text=L.Text("界面语言"),AutoSize=true },0,0);
            language.Items.AddRange(new object[]{L.Text("简体中文"),L.Text("英语")});
            initializing=true;language.SelectedIndex=L.Current=="en-US"?1:0;initializing=false;
            language.Margin=new Padding(0,7,0,8);root.Controls.Add(language,0,1);
            languageHint.Text=L.Text("语言切换立即保存，不改变服务配置或 Windows 登录项。");root.Controls.Add(languageHint,0,2);
            login.Text=L.Text("Windows 登录时启动本工具并收进托盘");login.Checked=initialLogin;
            login.MaximumSize=new Size(580,0);login.Margin=new Padding(0,18,0,12);root.Controls.Add(login,0,3);root.Controls.Add(status,0,4);
            var buttons=new FlowLayoutPanel { Dock=DockStyle.Bottom,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,WrapContents=false,FlowDirection=FlowDirection.RightToLeft,Padding=new Padding(0,16,0,0) };
            var save=new ThemeButton(L.Text("保存"),"accent") { Width=110 };var close=new ThemeButton(L.Text("关闭设置")) { Width=110,DialogResult=DialogResult.Cancel };
            buttons.Controls.Add(save);buttons.Controls.Add(close);root.Controls.Add(buttons,0,5);CancelButton=close;
            language.SelectedIndexChanged+=(o,e)=>
            {
                if(initializing||L.IsApplying||language.SelectedIndex<0)return;
                string value=language.SelectedIndex==1?"en-US":"zh-CN";
                try { L.SavePreference(repository.Paths.Root,value);repository.Current.Language=value;L.SetLanguage(value); }
                catch(Exception ex)
                {
                    initializing=true;language.SelectedIndex=L.Current=="en-US"?1:0;initializing=false;
                    L.MessageBox(this,ex.Message,L.Text("语言设置未保存"),MessageBoxButtons.OK,MessageBoxIcon.Information);
                }
            };
            save.Click+=(o,e)=>
            {
                try
                {
                    // Merely switching language never touches the HKCU Run value or services.json.
                    if(login.Checked!=initialLogin)
                    {
                        startup.Set(login.Checked,Application.ExecutablePath);
                        bool previous=repository.Current.LoginStartup;repository.Current.LoginStartup=login.Checked;
                        try { repository.Save(); } catch { repository.Current.LoginStartup=previous;throw; }
                    }
                    Close();
                }
                catch(Exception ex){L.MessageBox(this,ex.Message,L.Text("设置未完成"));}
            };
            AppTheme.Apply(this);RefreshLanguage();L.Attach(this);L.Changed+=RefreshLanguage;
        }
        void RefreshLanguage()
        {
            Text=L.Format("工具设置 · {0}",L.ProductName);
            status.Text=L.Format("当前此程序的系统登录项：{0}\r\n保存会仅更新 HKCU Run 中的 LocalServiceManager 项。\r\n各服务自启在服务编辑窗口单独设置，默认关闭。",L.Dynamic(L.Text(actualLogin?"开启":"关闭")));
            initializing=true;language.SelectedIndex=L.Current=="en-US"?1:0;initializing=false;
        }
        protected override void Dispose(bool disposing){if(disposing){L.Changed-=RefreshLanguage;L.Detach(this);}base.Dispose(disposing);}
    }
}
