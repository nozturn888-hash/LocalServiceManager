// External desktop acceptance for a freshly extracted portable package.
// Compile with .NET Framework 4 csc and System.Drawing, System.Core, System.Web.Extensions,
// WPF/UIAutomationClient, WPF/UIAutomationTypes and WPF/WindowsBase references.
// Usage: PortableAcceptance.exe <package-executable> <separate-validation-output>
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using Microsoft.Win32;

public static class PortableAcceptance
{
    delegate bool EnumCallback(IntPtr window, IntPtr data);
    delegate bool ResourceCallback(IntPtr module, IntPtr type, IntPtr name, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] struct MinMax { public Point Reserved,MaxSize,MaxPosition,MinTrack,MaxTrack; }
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumCallback callback,IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent,EnumCallback callback,IntPtr data);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out int pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window,StringBuilder text,int size);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window,StringBuilder text,int size);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window,int message,IntPtr w,IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window,int message,IntPtr w,IntPtr l);
    [DllImport("user32.dll",EntryPoint="SendMessageW")] static extern IntPtr ReadMinMax(IntPtr window,int message,IntPtr w,ref MinMax value);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window,out Rect value);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window,out Rect value);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr window,ref Point value);
    [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr window,int x,int y,int width,int height,bool repaint);
    [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr window,IntPtr rectangle,IntPtr region,uint flags);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr LoadLibraryEx(string file,IntPtr reserved,uint flags);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern bool EnumResourceNames(IntPtr module,IntPtr type,ResourceCallback callback,IntPtr data);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);

    public sealed class Config { public bool LoginStartup;public string Language;public List<Entry> Services; }
    public sealed class Entry { public bool AutoStart; }
    static readonly List<string> records=new List<string>();
    static Process process;static int ownPid;static long ownTicks;
    static string executable,packageRoot,output;static bool uiOnly;
    static void Check(bool success,string label)
    { if(!success)throw new Exception(label);records.Add("PASS "+label);Console.WriteLine("PASS "+label); }
    static void Identity()
    {
        process.Refresh();
        if(process.HasExited||process.StartTime.ToUniversalTime().Ticks!=ownTicks||
            !String.Equals(process.MainModule.FileName,executable,StringComparison.OrdinalIgnoreCase))
            throw new Exception("Acceptance process identity changed.");
    }
    static string Text(IntPtr window)
    { var result=new StringBuilder(1024);GetWindowText(window,result,result.Capacity);return result.ToString(); }
    static string Class(IntPtr window)
    { var result=new StringBuilder(256);GetClassName(window,result,result.Capacity);return result.ToString(); }
    static List<IntPtr> Windows(bool visible)
    {
        var result=new List<IntPtr>();EnumWindows((window,data)=>{
            int pid;GetWindowThreadProcessId(window,out pid);
            if(pid==ownPid&&(!visible||IsWindowVisible(window)))result.Add(window);return true;},IntPtr.Zero);return result;
    }
    static IntPtr MainWindow()
    { return Windows(false).FirstOrDefault(window=>Text(window)=="本机服务管家"||Text(window)=="Local Service Manager"); }
    static void Wait(Func<bool> condition,string label)
    {
        var until=DateTime.UtcNow.AddSeconds(15);
        while(!condition()&&DateTime.UtcNow<until)Thread.Sleep(100);Check(condition(),label);
    }
    static AutomationElement Find(IntPtr window,ControlType type,string name)
    {
        return AutomationElement.FromHandle(window).FindFirst(TreeScope.Descendants,new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty,ownPid),
            new PropertyCondition(AutomationElement.ControlTypeProperty,type),
            new PropertyCondition(AutomationElement.NameProperty,name)));
    }
    static AutomationElement FindAny(IntPtr window,ControlType type,params string[] names)
    { foreach(string name in names){var item=Find(window,type,name);if(item!=null)return item;}return null; }
    static void Invoke(AutomationElement element)
    {
        Identity();if(element==null||!element.Current.IsEnabled)throw new Exception("Requested safe control is unavailable.");
        object pattern;if(!element.TryGetCurrentPattern(InvokePattern.Pattern,out pattern))throw new Exception("Safe control has no InvokePattern.");
        ((InvokePattern)pattern).Invoke();
    }
    static string Hash(string path)
    {
        using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
        using(var hash=SHA256.Create())return Convert.ToBase64String(hash.ComputeHash(file));
    }
    static string StartupHash()
    {
        string value="";using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            if(key!=null)value=Convert.ToString(key.GetValue("LocalServiceManager"));
        using(var hash=SHA256.Create())return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(value)));
    }
    static void Sample(string label)
    {
        Identity();process.Refresh();double cpuBefore=process.TotalProcessorTime.TotalSeconds;
        var elapsed=Stopwatch.StartNew();long minWorking=Int64.MaxValue,maxWorking=0,minPrivate=Int64.MaxValue,maxPrivate=0;
        for(int sample=0;sample<20;sample++)
        {
            Thread.Sleep(1000);process.Refresh();
            minWorking=Math.Min(minWorking,process.WorkingSet64);maxWorking=Math.Max(maxWorking,process.WorkingSet64);
            minPrivate=Math.Min(minPrivate,process.PrivateMemorySize64);maxPrivate=Math.Max(maxPrivate,process.PrivateMemorySize64);
        }
        double seconds=elapsed.Elapsed.TotalSeconds,cpu=process.TotalProcessorTime.TotalSeconds-cpuBefore;
        string line=String.Format(CultureInfo.InvariantCulture,
            "MEASURE {0}: elapsedSeconds={1:F3}; cpuSeconds={2:F4}; logicalProcessors={3}; oneCorePercent={4:F4}; machinePercent={5:F4}; workingSetMiB={6:F3}..{7:F3}; privateBytesMiB={8:F3}..{9:F3}",
            label,seconds,cpu,Environment.ProcessorCount,cpu/seconds*100,cpu/seconds/Environment.ProcessorCount*100,
            minWorking/1048576d,maxWorking/1048576d,minPrivate/1048576d,maxPrivate/1048576d);
        records.Add(line);Console.WriteLine(line);
    }
    static Config ReadConfig()
    { return new JavaScriptSerializer().Deserialize<Config>(File.ReadAllText(Path.Combine(packageRoot,"config","services.json"),Encoding.UTF8)); }
    static Rect Client(IntPtr window)
    {
        Rect value;Check(GetClientRect(window,out value),"own client rectangle is readable");
        var point=new Point{X=value.Left,Y=value.Top};ClientToScreen(window,ref point);
        return new Rect{Left=point.X,Top=point.Y,Right=point.X+value.Right-value.Left,Bottom=point.Y+value.Bottom-value.Top};
    }
    static bool Contains(Rect parent,Rect child)
    { return child.Left>=parent.Left&&child.Top>=parent.Top&&child.Right<=parent.Right&&child.Bottom<=parent.Bottom; }
    static Rect Element(AutomationElement element)
    {
        var value=element.Current.BoundingRectangle;
        return new Rect{Left=(int)Math.Floor(value.Left),Top=(int)Math.Floor(value.Top),Right=(int)Math.Ceiling(value.Right),Bottom=(int)Math.Ceiling(value.Bottom)};
    }
    static void Capture(Rect rectangle,string name)
    {
        Check(rectangle.Right>rectangle.Left&&rectangle.Bottom>rectangle.Top,"safe screenshot has positive dimensions");
        using(var bitmap=new Bitmap(rectangle.Right-rectangle.Left,rectangle.Bottom-rectangle.Top))
        using(var graphics=Graphics.FromImage(bitmap))
        { graphics.CopyFromScreen(rectangle.Left,rectangle.Top,0,0,bitmap.Size,CopyPixelOperation.SourceCopy);bitmap.Save(Path.Combine(output,name+".png")); }
    }
    static IntPtr Tray()
    {
        Identity();foreach(var window in Windows(false).Where(window=>!IsWindowVisible(window)&&Text(window)==""&&Class(window).StartsWith("WindowsForms10.Window",StringComparison.Ordinal)).ToArray())
            PostMessage(window,0x800,new IntPtr(1),new IntPtr(0x205));
        IntPtr tray=IntPtr.Zero;Wait(()=>{
            tray=Windows(true).FirstOrDefault(window=>Text(window)==""&&FindAny(window,ControlType.MenuItem,"打开主窗口","Open main window")!=null);
            return tray!=IntPtr.Zero;},"own registered NotifyIcon callback opens actual tray menu");return tray;
    }
    static void ExitOwn()
    {
        Identity();var tray=Tray();
        Invoke(FindAny(tray,ControlType.MenuItem,"退出工具（服务继续运行）","Exit tool (keep services running)"));
        Wait(()=>{process.Refresh();return process.HasExited;},"own package exits via keep-services-running tray command");
    }
    static void Icons(IntPtr main)
    {
        int groups=0;IntPtr module=LoadLibraryEx(executable,IntPtr.Zero,2);
        Check(module!=IntPtr.Zero,"package PE loads as nonexecuting data for icon inspection");
        try{EnumResourceNames(module,new IntPtr(14),(m,t,n,d)=>{groups++;return true;},IntPtr.Zero);}
        finally{FreeLibrary(module);}
        Check(groups>0,"executable contains RT_GROUP_ICON resources");
        string ico=Path.Combine(packageRoot,"assets","application.ico");Check(File.Exists(ico),"portable package includes its own ICO asset");
        using(var file=new BinaryReader(File.OpenRead(ico)))
        { Check(file.ReadUInt16()==0&&file.ReadUInt16()==1&&file.ReadUInt16()>1,"ICO includes multiple image sizes"); }
        using(var icon=new Icon(ico)){Check(icon.Width>0&&icon.Height>0,"ICO asset decodes to a nonempty icon");}
        IntPtr windowIcon=SendMessage(main,0x7f,new IntPtr(1),IntPtr.Zero);
        if(windowIcon==IntPtr.Zero)windowIcon=SendMessage(main,0x7f,new IntPtr(0),IntPtr.Zero);
        Check(windowIcon!=IntPtr.Zero,"actual main window owns a nonempty icon handle");
        // The tray receives the same AppIcon factory as the window. Registration/callback is
        // independently exercised; this test does not inspect another process's private toolbar memory.
    }
    static void Layout(IntPtr main,bool english,string size)
    {
        Identity();var buttons=new[]{Find(main,ControlType.Button,english?"Start":"启动"),Find(main,ControlType.Button,english?"Stop":"停止"),Find(main,ControlType.Button,english?"Restart":"重启")};
        Check(buttons.All(button=>button!=null),"all lifecycle controls exist without invoking them");
        var handles=buttons.Select(button=>new IntPtr(button.Current.NativeWindowHandle)).ToArray();
        IntPtr panel=GetParent(handles[0]);Check(panel!=IntPtr.Zero&&panel!=main&&handles.All(handle=>GetParent(handle)==panel),"buttons share a dedicated action container");
        bool hasLog=false;EnumChildWindows(panel,(window,data)=>{if(Class(window).IndexOf("RichEdit",StringComparison.OrdinalIgnoreCase)>=0)hasLog=true;return true;},IntPtr.Zero);
        Check(!hasLog,"safe action crop contains no log control");
        SetForegroundWindow(main);RedrawWindow(main,IntPtr.Zero,IntPtr.Zero,0x185);Thread.Sleep(150);
        Rect parent=Client(panel);Check(Contains(Client(main),parent),"safe action crop lies inside main client");
        Capture(parent,(english?"en":"zh")+"-bottom-"+size);
        var bounds=buttons.Select(Element).ToArray();
        Check(buttons.All(button=>!button.Current.IsOffscreen)&&bounds.All(value=>value.Right>value.Left&&value.Bottom>value.Top),"three lifecycle controls are fully visible");
        Check(bounds.All(value=>value.Top==bounds[0].Top&&value.Bottom==bounds[0].Bottom),"three lifecycle controls remain on one row");
        for(int i=0;i<bounds.Length;i++)
        {
            for(int j=i+1;j<bounds.Length;j++)Check(Math.Min(bounds[i].Right,bounds[j].Right)<=Math.Max(bounds[i].Left,bounds[j].Left),"lifecycle controls do not overlap");
            bool reached=false;
            for(IntPtr ancestor=GetParent(handles[i]);ancestor!=IntPtr.Zero;ancestor=GetParent(ancestor))
            {
                int pid;GetWindowThreadProcessId(ancestor,out pid);Check(pid==ownPid,"layout ancestor belongs to acceptance PID");
                Check(Contains(Client(ancestor),bounds[i]),"lifecycle rectangle fully fits ancestor client");
                if(ancestor==main){reached=true;break;}
            }
            Check(reached,"lifecycle ancestry reaches main window");
        }
        records.Add("LAYOUT "+(english?"en":"zh")+" "+size+" DPI="+GetDpiForWindow(main));
    }
    static void Language(IntPtr main,bool english)
    {
        Invoke(FindAny(main,ControlType.Button,"工具设置","Tool settings"));
        IntPtr settings=IntPtr.Zero;Wait(()=>{
            settings=Windows(true).FirstOrDefault(window=>Text(window).StartsWith("工具设置 · ",StringComparison.Ordinal)||Text(window).StartsWith("Tool settings · ",StringComparison.Ordinal));
            return settings!=IntPtr.Zero;},"own tool settings opened");
        string loginName=english?"Start this tool in the tray when signing in to Windows":"Windows 登录时启动本工具并收进托盘";
        var combo=AutomationElement.FromHandle(settings).FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.ComboBox));
        object expand;Check(combo!=null&&combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern,out expand),"language combo can expand");
        SetForegroundWindow(settings);combo.SetFocus();Thread.Sleep(150);
        ((ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();Thread.Sleep(200);
        var options=new List<AutomationElement>();
        options.AddRange(combo.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.ListItem)).Cast<AutomationElement>());
        foreach(var popup in Windows(false).Where(window=>Class(window).IndexOf("ComboLBox",StringComparison.OrdinalIgnoreCase)>=0))
            options.AddRange(AutomationElement.FromHandle(popup).FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.ListItem)).Cast<AutomationElement>());
        foreach(string option in options.Select(item=>item.Current.Name).Distinct()){records.Add("OWN_COMBO_ITEM "+option);Console.WriteLine("OWN_COMBO_ITEM "+option);}
        var choice=options.FirstOrDefault(item=>english?(item.Current.Name.IndexOf("English",StringComparison.OrdinalIgnoreCase)>=0||item.Current.Name.IndexOf("英语",StringComparison.Ordinal)>=0):
            (item.Current.Name.IndexOf("Chinese",StringComparison.OrdinalIgnoreCase)>=0||item.Current.Name.IndexOf("中文",StringComparison.Ordinal)>=0));
        if(choice!=null)
        {
            ((SelectionItemPattern)choice.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            records.Add("SELECTION Own owner-drawn combo ListItem provider.");
        }
        else
        {
            // Some desktop providers omit owner-drawn ComboLBox items. Send navigation only
            // to the verified combo HWND; never send global keyboard input or edit JSON.
            ((ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse();
            IntPtr handle=new IntPtr(combo.Current.NativeWindowHandle);int pid;GetWindowThreadProcessId(handle,out pid);
            Check(handle!=IntPtr.Zero&&pid==ownPid&&GetParent(handle)!=IntPtr.Zero,"keyboard fallback targets own language combo handle");
            combo.SetFocus();SendMessage(handle,0x100,new IntPtr(0x24),IntPtr.Zero);SendMessage(handle,0x101,new IntPtr(0x24),IntPtr.Zero);
            if(english){SendMessage(handle,0x100,new IntPtr(0x28),IntPtr.Zero);SendMessage(handle,0x101,new IntPtr(0x28),IntPtr.Zero);}
            SendMessage(handle,0x100,new IntPtr(0x0d),IntPtr.Zero);SendMessage(handle,0x101,new IntPtr(0x0d),IntPtr.Zero);
            records.Add("SELECTION Own owner-drawn Combo provider exposed no ListItem; focused Home/Down/Enter navigation used.");
        }
        Thread.Sleep(350);
        string expectedLanguage=english?"en-US":"zh-CN";
        var preference=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(packageRoot,"config","preferences.json"),Encoding.UTF8));
        Check(Convert.ToString(preference["Language"])==expectedLanguage,"actual language selection persisted its preference");
        string product=english?"Local Service Manager":"本机服务管家";
        Check(Text(main)==product&&Text(settings)==(english?"Tool settings · ":"工具设置 · ")+product,"main and open settings titles switch immediately");
        Check(Find(settings,ControlType.Text,english?"Interface language":"界面语言")!=null,"settings language label is translated");
        var login=Find(settings,ControlType.CheckBox,loginName);Check(login!=null,"settings Windows startup label is translated");
        Check(((TogglePattern)login.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState==ToggleState.Off,"package Windows sign-in startup remains off");
        SetForegroundWindow(settings);RedrawWindow(settings,IntPtr.Zero,IntPtr.Zero,0x185);Thread.Sleep(150);Capture(Client(settings),(english?"en":"zh")+"-settings");
        Invoke(Find(settings,ControlType.Button,english?"Close settings":"关闭设置"));Wait(()=>!IsWindowVisible(settings),"settings closed without Save or startup writes");
        Rect original;GetWindowRect(main,out original);var minimum=new MinMax();ReadMinMax(main,0x24,IntPtr.Zero,ref minimum);
        Check(minimum.MinTrack.X>0&&minimum.MinTrack.Y>0,"actual minimum dimensions available");
        try
        {
            MoveWindow(main,original.Left,original.Top,Math.Max(1080,minimum.MinTrack.X),Math.Max(800,minimum.MinTrack.Y),true);Thread.Sleep(200);Layout(main,english,"default");
            MoveWindow(main,original.Left,original.Top,minimum.MinTrack.X,minimum.MinTrack.Y,true);Thread.Sleep(200);Layout(main,english,"minimum");
        }
        finally{MoveWindow(main,original.Left,original.Top,original.Right-original.Left,original.Bottom-original.Top,true);}
        var tray=Tray();Check(Find(tray,ControlType.MenuItem,english?"No services yet. Add one in the main window.":"暂无服务，请打开主窗口新增")!=null,"actual tray retains translated empty-service state");
        Invoke(Find(tray,ControlType.MenuItem,english?"Open main window":"打开主窗口"));
    }
    [STAThread] public static int Main(string[] args)
    {
        Console.OutputEncoding=Encoding.UTF8;string startupBefore=null;
        try
        {
            if(args.Length<2||args.Length>3||(args.Length==3&&args[2]!="--ui-only"))throw new Exception("Usage: PortableAcceptance.exe executable output-directory [--ui-only]");
            uiOnly=args.Length==3;
            executable=Path.GetFullPath(args[0]);packageRoot=Path.GetDirectoryName(executable);output=Path.GetFullPath(args[1]);
            if(output==packageRoot||output.StartsWith(packageRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("Validation output must be separate from the portable package.");
            Directory.CreateDirectory(output);Check(File.Exists(executable),"portable executable exists");
            using(var hash=SHA256.Create())records.Add("EXE_SHA256 "+BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(executable))).Replace("-",""));
            var version=FileVersionInfo.GetVersionInfo(executable);records.Add("EXE_FILE_VERSION "+version.FileVersion);records.Add("EXE_PRODUCT_VERSION "+version.ProductVersion);
            if(!uiOnly)Check(!File.Exists(Path.Combine(packageRoot,"config","services.json"))&&!File.Exists(Path.Combine(packageRoot,"config","preferences.json")),"fresh package contains no actual user configuration or language preference");
            else records.Add("MODE UI-only follow-up; startup/root/resource samples are preserved in the preceding fresh-package report.");
            foreach(string name in new[]{"config","logs","icons"})Check(!Directory.Exists(Path.Combine(output,name)),"validation working directory has no preexisting "+name+" data");
            startupBefore=StartupHash();SetProcessDPIAware();
            process=Process.Start(new ProcessStartInfo(executable,"--no-service-autostart"){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=output});
            ownPid=process.Id;ownTicks=process.StartTime.ToUniversalTime().Ticks;records.Add("OWN_PID "+ownPid);Console.WriteLine("OWN_PID "+ownPid);
            IntPtr main=IntPtr.Zero;Wait(()=>{main=MainWindow();return main!=IntPtr.Zero;},"portable package opens its own GUI");
            Identity();Check(Text(main)=="本机服务管家","fresh package starts in Chinese");
            foreach(string name in new[]{"config","logs","icons"})Check(Directory.Exists(Path.Combine(packageRoot,name))&&!Directory.Exists(Path.Combine(output,name)),"portable "+name+" data stays beside the executable, independent of current directory");
            Check(FindAny(main,ControlType.Text,packageRoot,packageRoot+Path.DirectorySeparatorChar)!=null,"main window displays executable directory as data root");
            Config config=ReadConfig();Check(config!=null&&config.Services!=null&&config.Services.Count==0,"portable configuration starts with zero services");
            Check(!config.LoginStartup&&config.Services.All(entry=>!entry.AutoStart),"both Windows tool startup and service auto-start remain off");
            string configBefore=Hash(Path.Combine(packageRoot,"config","services.json"));Icons(main);if(!uiOnly){Thread.Sleep(1000);Sample("GUI visible, empty service list");}
            PostMessage(main,0x10,IntPtr.Zero,IntPtr.Zero);Wait(()=>!IsWindowVisible(main),"closing main hides only this package to tray");
            Check(!process.HasExited,"tray-resident package remains running");if(!uiOnly)Sample("Tray hidden, empty service list");var menu=Tray();Invoke(Find(menu,ControlType.MenuItem,"打开主窗口"));Wait(()=>IsWindowVisible(main),"own tray restores own GUI");
            Language(main,true);Language(main,false);
            Check(Hash(Path.Combine(packageRoot,"config","services.json"))==configBefore,"language and layout QA preserve portable service configuration");
            Check(StartupHash()==startupBefore,"real HKCU Run value remains unchanged");
            ExitOwn();Check(StartupHash()==startupBefore,"safe package exit preserves real HKCU Run value");
            records.Add("BOUNDARY No service was added or started/stopped/restarted; no original installation was accessed; tray registration and window icon verified without foreign process memory.");
            File.WriteAllLines(Path.Combine(output,"portable-checks.txt"),records,new UTF8Encoding(false));return 0;
        }
        catch(Exception error)
        {
            records.Add("FAIL "+error.Message);Console.WriteLine("FAIL "+error.Message);
            if(process!=null){try{if(!process.HasExited)ExitOwn();}catch(Exception exitError){records.Add("OWN PACKAGE LEFT RUNNING: "+exitError.Message);}}
            if(startupBefore!=null)records.Add("STARTUP_UNCHANGED "+(StartupHash()==startupBefore));
            if(!String.IsNullOrEmpty(output)&&Directory.Exists(output))File.WriteAllLines(Path.Combine(output,"portable-checks.txt"),records,new UTF8Encoding(false));return 1;
        }
        finally{if(process!=null)process.Dispose();}
    }
}
