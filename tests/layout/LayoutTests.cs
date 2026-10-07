using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace LocalServiceManager
{
    class LayoutTests
    {
        static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly List<string> report = new List<string>();
        static readonly List<Font> simulatedFonts = new List<Font>();
        static int passed, failed;
        static string output;
        sealed class NoStartup : IStartupStore
        {
            public int Writes;
            public bool IsEnabled(string executable) { return false; }
            public void Set(bool enabled, string executable) { Writes++; throw new Exception("No registry writes allowed"); }
        }
        static T Field<T>(object value, string name) { return (T)value.GetType().GetField(name, Hidden).GetValue(value); }
        static IEnumerable<Control> All(Control root)
        {
            yield return root;
            foreach (Control child in root.Controls) foreach (var control in All(child)) yield return control;
        }
        static void Check(bool ok, string description)
        {
            if (ok) { passed++; report.Add("PASS " + description); }
            else { failed++; report.Add("FAIL " + description); }
        }
        static void Pump(int milliseconds)
        {
            var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (DateTime.UtcNow < until) { Application.DoEvents(); System.Threading.Thread.Sleep(5); }
        }
        static string Rect(Rectangle value) { return String.Format("[x={0},y={1},w={2},h={3}]", value.X,value.Y,value.Width,value.Height); }
        static Rectangle ScreenBounds(Control control)
        {
            return control.RectangleToScreen(new Rectangle(0,0,control.Width,control.Height));
        }
        static string Clip(Control button)
        {
            Rectangle full = ScreenBounds(button);
            for (Control ancestor = button.Parent; ancestor != null; ancestor = ancestor.Parent)
            {
                Rectangle viewport = ancestor.RectangleToScreen(ancestor.ClientRectangle);
                if (!viewport.Contains(full))
                {
                    var table = ancestor as TableLayoutPanel;
                    string columns = table == null ? "" : " columns=" + String.Join(",", table.GetColumnWidths()) +
                        " explicitStyles=" + table.ColumnStyles.Count;
                    return button.Text + " full=" + Rect(full) + " clipped by " + ancestor.GetType().Name +
                        " client=" + Rect(viewport) + " button-parent-bounds=" + Rect(button.Bounds) + columns;
                }
            }
            return null;
        }
        static void RevealScrollableField(Control button)
        {
            // Field browse buttons in scrollable editor pages may require scrolling. The persistent
            // command groups have no scrollable ancestor; every one must be fully visible at once.
            for (Control parent = button.Parent; parent != null; parent = parent.Parent)
            {
                var scroll = parent as ScrollableControl;
                if (scroll != null && scroll.AutoScroll) scroll.ScrollControlIntoView(button);
            }
            Application.DoEvents();
        }
        static void Shot(Form form, string name)
        {
            form.Refresh(); Application.DoEvents();
            using (var bitmap = new Bitmap(form.Width,form.Height))
            {
                form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));
                bitmap.Save(Path.Combine(output,name+".png"));
            }
        }
        static void FullVisibility(Form form, string scenario)
        {
            var clips = new List<string>();
            var tabs = All(form).OfType<TabControl>().ToArray();
            var savedTabs = tabs.Select(tab=>tab.SelectedIndex).ToArray();
            if (tabs.Length == 0) CheckPageButtons(form,clips);
            else
            {
                // Editors have one tab control. Each page is checked after it becomes visible.
                foreach (var tab in tabs) for (int index=0;index<tab.TabPages.Count;index++)
                {
                    tab.SelectedIndex=index;form.PerformLayout();Application.DoEvents();
                    CheckPageButtons(form,clips);
                }
                for(int index=0;index<tabs.Length;index++)tabs[index].SelectedIndex=savedTabs[index];
            }
            Check(clips.Count==0, scenario+" all button rectangles fit every ancestor viewport"+
                (clips.Count==0?"":" / "+String.Join(" | ",clips.Distinct())));
        }
        static void CheckPageButtons(Form form, List<string> clips)
        {
            foreach (Button button in All(form).OfType<Button>().Where(button=>button.Visible))
            {
                RevealScrollableField(button);
                string clip=Clip(button);if(clip!=null)clips.Add(clip);
            }
        }
        static void GroupGeometry(Form form, string scenario)
        {
            var main=form as MainForm;
            var groups=new List<Button[]>();
            if(main!=null)groups.Add(new[]{main.StartButton,main.StopButton,main.RestartButton});
            else foreach(var flow in All(form))
            {
                var buttons=flow.Controls.OfType<Button>().Where(button=>button.Visible).ToArray();
                if(buttons.Length>1)groups.Add(buttons);
            }
            foreach(var buttons in groups)
            {
                var bounds=buttons.Select(ScreenBounds).ToArray();
                bool oneRow=bounds.All(boundsItem=>Math.Abs(boundsItem.Top-bounds[0].Top)<=1);
                bool disjoint=true;
                for(int left=0;left<bounds.Length;left++)for(int right=left+1;right<bounds.Length;right++)
                    if(bounds[left].IntersectsWith(bounds[right]))disjoint=false;
                Check(oneRow,scenario+" command group stays on one row / "+String.Join(" | ",buttons.Select((button,index)=>button.Text+" "+Rect(bounds[index]))));
                Check(disjoint,scenario+" command group never overlaps");
                if(main!=null)
                {
                    var parent=main.StartButton.Parent as FlowLayoutPanel;
                    var table=parent==null?null:parent.Parent as TableLayoutPanel;
                    report.Add("GEOMETRY "+scenario+" actionbar="+(parent==null?"none":Rect(parent.ClientRectangle))+
                        " WrapContents="+(parent==null?"n/a":parent.WrapContents.ToString())+
                        " columnStyles="+(table==null?"n/a":String.Join(",",table.ColumnStyles.Cast<ColumnStyle>().Select(style=>style.SizeType+":"+style.Width)))+
                        " controlsOuterWidth="+buttons.Sum(button=>button.Width+button.Margin.Horizontal));
                }
            }
        }
        static void ScaleSynthetic(Form form, float targetScale, float actualScale)
        {
            float factor=targetScale/actualScale;
            if(Math.Abs(factor-1f)<0.001f)return;
            var fonts=All(form).ToDictionary(control=>control,control=>control.Font);
            foreach(var container in All(form).OfType<ContainerControl>())container.AutoScaleMode=AutoScaleMode.None;
            form.Scale(new SizeF(factor,factor));
            foreach(var pair in fonts)
            {
                var font=new Font(pair.Value.FontFamily,pair.Value.SizeInPoints*factor,pair.Value.Style);
                simulatedFonts.Add(font);pair.Key.Font=font;
            }
            form.PerformLayout();Pump(30);
        }
        static Form Make(string kind, ProjectPaths paths, ConfigRepository repository, ServiceEngine engine, NoStartup startup)
        {
            if(kind=="main")return new MainForm(repository,engine,startup,false,false);
            if(kind=="editor")return new ServiceEditor(paths,new ServiceConfig{Name="Fake test service",Reviewed=false},()=>new ServiceSnapshot{State=ServiceState.Running,Owned=true});
            if(kind=="stdio-editor")
            {
                var service=ServiceTemplates.ClientEntry();service.Name="Fake client registration";service.ClientOwner="Codex";
                return new ServiceEditor(paths,service);
            }
            if(kind=="new")
            {
                var wizard=new NewServiceDialog(paths);Field<RadioButton>(wizard,"independent").Checked=true;return wizard;
            }
            if(kind=="icon")
            {
                var list=new ListView{Dock=DockStyle.Fill,View=View.LargeIcon,MultiSelect=false};
                list.Items.Add("#1 mock icon");list.Items.Add("#2 mock icon");list.Items[0].Selected=true;
                return IconLibrary.CreateResourcePicker(list);
            }
            if(kind=="template")return new TemplatePicker(paths);
            if(kind=="settings")return new ToolSettingsForm(repository,startup);
            var type=typeof(L).GetNestedTypes(BindingFlags.NonPublic).First(candidate=>typeof(Form).IsAssignableFrom(candidate));
            return (Form)Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public,null,
                new object[]{L.Format("删除配置条目“{0}”？脚本和服务进程不会被删除或停止。","Fake test service"),
                    L.Text("删除条目"),MessageBoxButtons.YesNoCancel,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2},null);
        }
        static void Exercise(string kind, string scaleName, float targetScale, bool synthetic, ProjectPaths paths, ConfigRepository repository,
            ServiceEngine engine, NoStartup startup, bool repro)
        {
            L.SetLanguage("zh-CN");
            using(var form=Make(kind,paths,repository,engine,startup))
            {
                var main=form as MainForm;
                if(main!=null)Field<Timer>(main,"timer").Stop();
                form.StartPosition=FormStartPosition.Manual;form.Location=new Point(50,50);form.Show();Pump(main==null?30:100);
                if(main!=null)Field<Timer>(main,"timer").Stop();
                float dpiX,dpiY;
                using(var graphics=form.CreateGraphics()){dpiX=graphics.DpiX;dpiY=graphics.DpiY;}
                report.Add("DPI "+kind+" "+scaleName+" measured="+dpiX+"x"+dpiY+
                    " initialWindow="+form.Size+" initialMinimum="+form.MinimumSize+
                    " screen="+Screen.FromControl(form).WorkingArea);
                if(synthetic)ScaleSynthetic(form,targetScale,dpiX/96f);
                Size normal=form.Size,min=form.MinimumSize;
                if(min.Width==0||min.Height==0)min=normal; // Fixed custom message has no resize entry.
                foreach(string language in new[]{"zh-CN","en-US","zh-CN"})
                {
                    L.SetLanguage(language);Pump(20);
                    foreach(bool smallest in new[]{false,true})
                    {
                        form.Size=smallest?min:normal;form.PerformLayout();Pump(20);
                        string scenario=kind+"/"+scaleName+"/"+language+"/"+(smallest?"minimum":"default");
                        if(language=="en-US"&&(kind=="main"||scaleName=="native"||scaleName=="synthetic-200"))
                            Shot(form,scenario.Replace('/','-'));
                        FullVisibility(form,scenario);GroupGeometry(form,scenario);
                        if(kind=="icon")
                        {
                            var button=All(form).OfType<Button>().Single();
                            Check(TextRenderer.MeasureText(button.Text,button.Font).Width<=button.ClientSize.Width-10,
                                scenario+" selected-icon button text fits completely");
                        }
                    }
                }
                if(main!=null)main.ExitAsync(false).GetAwaiter().GetResult();
            }
        }
        [STAThread] static int Main(string[] args)
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            output=args[0];Directory.CreateDirectory(output);bool repro=args.Length>1&&args[1]=="repro";
            var startup=new NoStartup();int code=0;
            try
            {
                report.Add("DPI-aware test executable embeds the production app.manifest.");
                report.Add("Synthetic target 125/150/200% scales fonts and controls relative to measured native DPI. Windows display scaling is never changed; synthetic cases are not claims of actual monitor DPI.");
                report.Add("Persistent command buttons must fit all ancestor client rectangles simultaneously. Scrollable editor field browse buttons are checked after individually revealing them; this does not excuse command-group cropping.");
                var paths=new ProjectPaths(Path.Combine(output,"isolated-data"));var repository=new ConfigRepository(paths,false);
                var fake=ServiceTemplates.ClientEntry();fake.Name="Fake registration only";repository.Current.Services.Add(fake);repository.Save();
                byte[] original=File.ReadAllBytes(paths.Config);
                using(var engine=new ServiceEngine(paths))
                {
                    Exercise("main","native",1f,false,paths,repository,engine,startup,repro);
                    if(!repro)foreach(string kind in new[]{"editor","stdio-editor","new","template","settings","message","icon"})
                        Exercise(kind,"native",1f,false,paths,repository,engine,startup,false);
                    if(!repro)foreach(float scale in new[]{1.25f,1.5f,2f})foreach(string kind in new[]{"main","editor","stdio-editor","new","template","settings","message","icon"})
                        Exercise(kind,"synthetic-"+(scale*100).ToString("0"),scale,true,paths,repository,engine,startup,false);
                }
                Check(File.ReadAllBytes(paths.Config).SequenceEqual(original),"isolated service file remains byte for byte unchanged");
                Check(startup.Writes==0,"no Windows startup writes");
                report.Add("No real service lifecycle, network probe, actual manager restart, browser launch or external project modification.");
                if(failed>0)code=1;
            }
            catch(Exception error){report.Add("ERROR "+error);code=2;}
            finally
            {
                foreach(var font in simulatedFonts)font.Dispose();
                report.Add("TOTAL passed="+passed+" failed="+failed);
                File.WriteAllLines(Path.Combine(output,"results.txt"),report,new UTF8Encoding(false));
                foreach(var line in report.Where(line=>line.StartsWith("DPI ")||line.StartsWith("ERROR ")||line.StartsWith("TOTAL ")))Console.WriteLine(line);
                foreach(var line in report.Where(line=>line.StartsWith("FAIL ")||line.StartsWith("GEOMETRY ")).Take(30))Console.WriteLine(line);
                Console.WriteLine(Path.Combine(output,"results.txt"));
            }
            return code;
        }
    }
}
