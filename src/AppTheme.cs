using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LocalServiceManager
{
    // Palette adapted from ByteTrix/process-manager's official default dark theme.
    public static class AppTheme
    {
        public static readonly Color Background=Color.FromArgb(10,10,10), Panel=Color.FromArgb(19,19,19), Input=Color.FromArgb(15,15,15), Border=Color.FromArgb(42,42,42);
        public static readonly Color Text=Color.FromArgb(226,232,240), Muted=Color.FromArgb(148,163,184), Disabled=Color.FromArgb(116,126,140), Accent=Color.FromArgb(129,140,248), Selection=Color.FromArgb(27,29,51);
        public static readonly Color Green=Color.FromArgb(74,222,128), Red=Color.FromArgb(248,113,113), Yellow=Color.FromArgb(251,191,36), Orange=Color.FromArgb(251,146,60);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h,int attr,ref int value,int size);
        public static void Form(Form form)
        {
            form.AutoScaleDimensions=new SizeF(96,96); form.AutoScaleMode=AutoScaleMode.Dpi;
            form.BackColor=Background; form.ForeColor=Text;
            form.HandleCreated+=(o,e)=>{try{int on=1;if(DwmSetWindowAttribute(form.Handle,20,ref on,4)!=0)DwmSetWindowAttribute(form.Handle,19,ref on,4);}catch(DllNotFoundException){}catch(EntryPointNotFoundException){}};
        }
        public static void Apply(Control parent)
        {
            foreach(Control control in parent.Controls)
            {
                control.ForeColor=Text;
                if(control is TextBoxBase){control.BackColor=Input; ((TextBoxBase)control).BorderStyle=control is RichTextBox?BorderStyle.None:BorderStyle.FixedSingle;}
                else if(control is ListView){control.BackColor=Panel;}
                else if(control is ComboBox)
                {
                    var combo=(ComboBox)control; combo.BackColor=Input; combo.FlatStyle=FlatStyle.Flat;
                    combo.DrawMode=DrawMode.OwnerDrawFixed; combo.ItemHeight=Math.Max(24,combo.Font.Height+8);
                    combo.DrawItem+=(o,e)=>{if(e.Index<0)return;bool selected=(e.State&DrawItemState.Selected)!=0;using(var b=new SolidBrush(selected?Selection:Input))e.Graphics.FillRectangle(b,e.Bounds);TextRenderer.DrawText(e.Graphics,Convert.ToString(combo.Items[e.Index]),combo.Font,new Rectangle(e.Bounds.X+6,e.Bounds.Y,e.Bounds.Width-10,e.Bounds.Height),combo.Enabled?Text:Disabled,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);};
                }
                else if(control is TabPage){control.BackColor=Panel;}
                else if(control is TabControl)
                {
                    var tabs=(TabControl)control; tabs.DrawMode=TabDrawMode.OwnerDrawFixed; tabs.Padding=new Point(16,8);
                    tabs.DrawItem+=(o,e)=>{bool selected=e.Index==tabs.SelectedIndex;using(var b=new SolidBrush(selected?Selection:Panel))e.Graphics.FillRectangle(b,e.Bounds);TextRenderer.DrawText(e.Graphics,tabs.TabPages[e.Index].Text,tabs.Font,e.Bounds,selected?Accent:Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);};
                }
                else if(control is CheckBox){control.BackColor=parent.BackColor;((CheckBox)control).UseVisualStyleBackColor=false;((CheckBox)control).FlatStyle=FlatStyle.Flat;}
                else if(!(control is Button)) control.BackColor=parent.BackColor;
                if(Convert.ToString(control.Tag)=="muted")control.ForeColor=Muted;
                if(Convert.ToString(control.Tag)=="warning")control.ForeColor=Yellow;
                Apply(control);
            }
        }
        public static void Menu(ToolStrip menu)
        {
            menu.BackColor=Panel; menu.ForeColor=Text; menu.Font=new Font("Microsoft YaHei UI",9.5f);
            menu.Padding=new Padding(5); menu.Renderer=new DarkMenuRenderer();
        }
    }
    public sealed class ThemeCheckBox : CheckBox
    {
        public ThemeCheckBox()
        {
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
            UseVisualStyleBackColor=false;
        }
        public override Size GetPreferredSize(Size proposedSize)
        {
            int glyph=Math.Max(16,Font.Height),gap=7;
            int limit=MaximumSize.Width>0?MaximumSize.Width:(proposedSize.Width>0?proposedSize.Width:Int32.MaxValue/4);
            var text=TextRenderer.MeasureText(Text,Font,new Size(Math.Max(30,limit-glyph-gap),Int32.MaxValue/4),TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix);
            return new Size(Math.Min(limit,text.Width+glyph+gap),Math.Max(glyph,text.Height)+4);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            int size=Math.Max(13,(int)(14*e.Graphics.DpiX/96f));int y=(Height-size)/2;
            var box=new Rectangle(0,y,size,size);
            using(var fill=new SolidBrush(AppTheme.Input))e.Graphics.FillRectangle(fill,box);
            using(var border=new Pen(Enabled?AppTheme.Muted:AppTheme.Disabled))e.Graphics.DrawRectangle(border,box);
            if(Checked)
            {
                using(var pen=new Pen(Enabled?AppTheme.Accent:AppTheme.Muted,2))
                {
                    pen.StartCap=pen.EndCap=LineCap.Round;
                    e.Graphics.DrawLines(pen,new[]{new Point(size/5,y+size/2),new Point(size*2/5,y+size*3/4),new Point(size*4/5,y+size/4)});
                }
            }
            var textBounds=new Rectangle(size+7,0,Math.Max(1,Width-size-7),Height);
            TextRenderer.DrawText(e.Graphics,Text,Font,textBounds,Enabled?AppTheme.Text:AppTheme.Muted,TextFormatFlags.WordBreak|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);
            if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,textBounds,AppTheme.Muted,BackColor);
        }
    }

    public sealed class ThemeButton : Button
    {
        readonly string tone; bool hover;
        public ThemeButton(string text,string tone="normal")
        {
            Text=text;this.tone=tone;FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;UseVisualStyleBackColor=false;
            Width=82;Height=34;Margin=new Padding(0,0,8,6);SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);
        }
        protected override void OnMouseEnter(EventArgs e){hover=true;Invalidate();base.OnMouseEnter(e);}
        protected override void OnMouseLeave(EventArgs e){hover=false;Invalidate();base.OnMouseLeave(e);}
        protected override void OnEnabledChanged(EventArgs e){Invalidate();base.OnEnabledChanged(e);}
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? AppTheme.Background : Parent.BackColor);
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            Color text=AppTheme.Text,bg=Color.FromArgb(26,26,26),border=AppTheme.Border;
            if(tone=="accent"){text=Color.FromArgb(165,180,252);bg=Color.FromArgb(30,27,75);border=Color.FromArgb(67,56,202);}
            else if(tone=="start"){text=AppTheme.Green;bg=Color.FromArgb(5,46,22);border=Color.FromArgb(20,83,45);}
            else if(tone=="stop"){text=AppTheme.Red;bg=Color.FromArgb(40,10,10);border=Color.FromArgb(91,27,27);}
            else if(tone=="restart"){text=AppTheme.Yellow;bg=Color.FromArgb(39,29,11);border=Color.FromArgb(120,53,15);}
            if(!Enabled){bg=Color.FromArgb(20,20,20);text=AppTheme.Disabled;border=Color.FromArgb(35,35,35);}else if(hover){bg=ControlPaint.Light(bg,.18f);border=text;}
            using(var path=Round(new Rectangle(1,1,Width-3,Height-3),5))using(var brush=new SolidBrush(bg))using(var pen=new Pen(border)){e.Graphics.FillPath(brush,path);e.Graphics.DrawPath(pen,path);}
            TextRenderer.DrawText(e.Graphics,Text,Font,new Rectangle(5,2,Width-10,Height-4),text,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
            if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,new Rectangle(5,5,Width-10,Height-10),text,bg);
        }
        static GraphicsPath Round(Rectangle r,int radius){var p=new GraphicsPath();int d=radius*2;p.AddArc(r.Left,r.Top,d,d,180,90);p.AddArc(r.Right-d,r.Top,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.Left,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;}
    }
    public sealed class ThemeTabs : TabControl
    {
        public ThemeTabs(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);BackColor=AppTheme.Panel;}
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(AppTheme.Panel);
            for(int i=0;i<TabCount;i++){var r=GetTabRect(i);bool selected=i==SelectedIndex;using(var b=new SolidBrush(selected?AppTheme.Selection:AppTheme.Panel))e.Graphics.FillRectangle(b,r);TextRenderer.DrawText(e.Graphics,TabPages[i].Text,Font,r,selected?AppTheme.Accent:AppTheme.Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);if(selected)using(var b=new SolidBrush(AppTheme.Accent))e.Graphics.FillRectangle(b,r.Left,r.Bottom-2,r.Width,2);}
        }
    }
    public sealed class ThemeList : ListView
    {
        [DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr h,int m,IntPtr w,IntPtr l);
        [DllImport("user32.dll")]static extern int GetScrollPos(IntPtr h,int bar);
        [DllImport("user32.dll")]static extern bool ShowScrollBar(IntPtr h,int bar,bool show);
        HeaderWindow header;
        protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);header=new HeaderWindow(this,SendMessage(Handle,0x101f,IntPtr.Zero,IntPtr.Zero));}
        protected override void OnHandleDestroyed(EventArgs e){if(header!=null){header.ReleaseHandle();header=null;}base.OnHandleDestroyed(e);}
        public void FinishColumns(){if(IsHandleCreated){int width=0;foreach(ColumnHeader c in Columns)width+=c.Width;if(width<=ClientSize.Width)ShowScrollBar(Handle,0,false);}}
        sealed class HeaderWindow : NativeWindow
        {
            readonly ThemeList list;
            public HeaderWindow(ThemeList list,IntPtr handle){this.list=list;if(handle!=IntPtr.Zero)AssignHandle(handle);}
            protected override void WndProc(ref Message m){base.WndProc(ref m);if(m.Msg==0xF){int x=-GetScrollPos(list.Handle,0);foreach(ColumnHeader c in list.Columns)x+=c.Width;using(var g=Graphics.FromHwnd(Handle)){var bounds=g.VisibleClipBounds;if(x<bounds.Right)using(var b=new SolidBrush(AppTheme.Panel))g.FillRectangle(b,x,0,bounds.Right-x,bounds.Height);}}}
        }
    }
    public sealed class ThemeCombo : ComboBox
    {
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if(m.Msg==0xF&&DropDownStyle==ComboBoxStyle.DropDownList){using(var g=Graphics.FromHwnd(Handle)){int w=SystemInformation.VerticalScrollBarWidth+4;var r=new Rectangle(Width-w,0,w,Height);using(var b=new SolidBrush(AppTheme.Panel))g.FillRectangle(b,r);int x=r.Left+r.Width/2,y=Height/2;using(var b=new SolidBrush(Enabled?AppTheme.Muted:AppTheme.Disabled))g.FillPolygon(b,new[]{new Point(x-4,y-2),new Point(x+4,y-2),new Point(x,y+2)});using(var p=new Pen(AppTheme.Border))g.DrawRectangle(p,0,0,Width-1,Height-1);}}
        }
    }
    public sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer():base(new DarkMenuColors()){RoundedEdges=false;}
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e){e.ArrowColor=AppTheme.Muted;base.OnRenderArrow(e);}
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor=e.Item.Enabled?AppTheme.Text:AppTheme.Muted;
            var value=e.Item.Tag as ServiceSnapshot;int split=e.Text.LastIndexOf("   ● ",StringComparison.Ordinal);
            if(value==null||split<0){base.OnRenderItemText(e);return;}
            string name=e.Text.Substring(0,split);TextRenderer.DrawText(e.Graphics,name,e.TextFont,e.TextRectangle,e.TextColor,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);
            int x=e.TextRectangle.Left+TextRenderer.MeasureText(e.Graphics,name,e.TextFont,Size.Empty,TextFormatFlags.NoPrefix).Width+9;
            using(var b=new SolidBrush(MainForm.StateColor(value.State)))e.Graphics.FillEllipse(b,x,e.TextRectangle.Top+(e.TextRectangle.Height-7)/2,7,7);
            TextRenderer.DrawText(e.Graphics,e.Text.Substring(split+"   ● ".Length),e.TextFont,new Rectangle(x+12,e.TextRectangle.Top,Math.Max(1,e.TextRectangle.Right-x-12),e.TextRectangle.Height),MainForm.StateColor(value.State),TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);
        }
    }
    sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground{get{return AppTheme.Panel;}}
        public override Color ImageMarginGradientBegin{get{return AppTheme.Panel;}}
        public override Color ImageMarginGradientMiddle{get{return AppTheme.Panel;}}
        public override Color ImageMarginGradientEnd{get{return AppTheme.Panel;}}
        public override Color MenuItemSelected{get{return AppTheme.Selection;}}
        public override Color MenuItemBorder{get{return AppTheme.Border;}}
        public override Color MenuBorder{get{return AppTheme.Border;}}
        public override Color SeparatorDark{get{return AppTheme.Border;}}
        public override Color SeparatorLight{get{return AppTheme.Border;}}
        public override Color MenuItemSelectedGradientBegin{get{return AppTheme.Selection;}}
        public override Color MenuItemSelectedGradientEnd{get{return AppTheme.Selection;}}
    }
}
