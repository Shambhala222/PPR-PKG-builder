using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LibProsperoPkg.Gui.ViewModels;
using LibProsperoPkg.Gui.Services;
using LibProsperoPkg.PKG;

internal static class Program
{
    [STAThread] public static void Main(string[] args)
    {
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory,"libcrypto.3.dylib"),out _);
        AppBuilder.Configure<ExtendedApp>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
    }
}
public sealed class ExtendedApp : LibProsperoPkg.Gui.App
{
    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if(ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || desktop.MainWindow is not Window window) return;
        window.ClearValue(Window.TitleProperty); window.Title="FPKG Builder 0.8.3 - macOS";
        var tabs=window.GetLogicalDescendants().OfType<TabControl>().First();
        var original=tabs.Items.Cast<TabItem>().ToArray();
        original[0].ClearValue(HeaderedContentControl.HeaderProperty); original[0].Header="Plain Build";
        original[1].ClearValue(HeaderedContentControl.HeaderProperty); original[1].Header="Inspect & Unpack";
        var sdk=new SdkTab(window);
        var sdkItem=new TabItem { Header="Drakmor's SDK Fix v12",Content=sdk };
        tabs.Items.Insert(1,sdkItem); tabs.SelectedIndex=0;
        foreach(var text in window.GetLogicalDescendants().OfType<TextBlock>().Where(t=>t.Text=="v0.6.5.2"||t.Text=="v0.8.2").ToArray()) {text.ClearValue(TextBlock.TextProperty);text.Text="v0.8.3";}
        var vm=(MainWindowViewModel)window.DataContext!;
        var grid=(Grid)window.Content!;
        var footer=grid.Children.Where(c=>Grid.GetRow(c)==2).ToArray();
        var oldHeight=grid.RowDefinitions[2].Height;
        var baseStatus=grid.Children.OfType<StackPanel>().FirstOrDefault(c=>Grid.GetRow(c)==3)?.Children.OfType<StackPanel>().FirstOrDefault();
        tabs.SelectionChanged+=(_,e)=> {
            if(e.Source!=tabs) return;
            bool selected=tabs.SelectedItem==sdkItem;
            foreach(var c in footer)c.IsVisible=!selected;
            if(baseStatus!=null)baseStatus.IsVisible=!selected;
            grid.RowDefinitions[2].Height=selected ? new GridLength(0) : oldHeight;
        };
        sdk.BusyChanged+=busy=> {foreach(var item in original)item.IsEnabled=!busy;};
        ((INotifyPropertyChanged)vm).PropertyChanged+=(_,e)=> { if(e.PropertyName==nameof(vm.IsBusy))sdkItem.IsEnabled=!vm.IsBusy; };
        window.Closing+=(_,e)=> { if(sdk.IsBusy) {e.Cancel=true;sdk.ShowMessage("Cancel the SDK Fix operation before closing this window.");} };
    }
}
public sealed class SdkTab : UserControl
{
    readonly Window owner;
    readonly TextBox source=new(), output=new(), temporary=new(), reference=new(), package=new(), extract=new(), passcode=new(){Text=new string('0',32)};
    readonly NumericUpDown compression=new(){Minimum=-4,Maximum=9,Value=7,FormatString="0",Width=100,HorizontalAlignment=HorizontalAlignment.Left};
    readonly NumericUpDown chunks=new(){Minimum=1,Maximum=255,Value=100,FormatString="0",Width=100,HorizontalAlignment=HorizontalAlignment.Left};
    readonly ComboBox verification=new(){Width=220,HorizontalAlignment=HorizontalAlignment.Left,MinHeight=30};
    readonly CheckBox keep=new(){Content="Keep intermediate files"};
    readonly CheckBox force=new(){Content="Overwrite existing files (Force)"};
    readonly Image icon=new(){Stretch=Stretch.Uniform};
    Bitmap? iconBitmap; DispatcherTimer? iconTimer;
    readonly Button start=new(){Content="Build SDK Fix PKG"},verify=new(){Content="Verify PKG"},extractPkg=new(){Content="Extract PKG"},selectPkg=new(){Content="Select existing PKG…"},openFolder=new(){Content="Open result folder"},clearLog=new(){Content="Clear log"},cancel=new(){Content="Cancel",IsEnabled=false};
    readonly TextBox log=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,FontFamily=new FontFamily("Menlo, monospace"),FontSize=11,MinHeight=150};
    readonly TextBlock status=new(){Text="Ready",TextWrapping=TextWrapping.Wrap}, metrics=new(){Text="",TextWrapping=TextWrapping.Wrap};
    readonly ProgressBar progress=new(){Minimum=0,Maximum=100,Height=10};
    readonly StackPanel settings=new(){Spacing=7};
    readonly StringBuilder history=new();
    Process? process; bool requestedCancel; DateTime started; DispatcherTimer? timer; string? result; string sizeInfo=""; DateTime phaseStarted; double? sdkPercent; string? lastFolder;
    public bool IsBusy {get;private set;}
    public event Action<bool>? BusyChanged;
    public SdkTab(Window owner)
    {
        this.owner=owner;
        var root=new Grid{RowDefinitions=new RowDefinitions("Auto,Auto,Auto,Auto,*"),RowSpacing=8,Margin=new Thickness(0,10,0,0)};
        var header=new TextBlock{Text="Drakmor's SDK Fix v12",FontSize=17,FontWeight=FontWeight.Bold};root.Children.Add(header);
        void Row(string title,Control field,Func<Task>? browse=null,string? help=null)
        {
            var row=new Grid{ColumnDefinitions=new ColumnDefinitions("140,*,Auto"),ColumnSpacing=8};
            var label=new StackPanel{Orientation=Orientation.Horizontal,Spacing=6,VerticalAlignment=VerticalAlignment.Center};
            label.Children.Add(new TextBlock{Text=title,VerticalAlignment=VerticalAlignment.Center});
            if(help!=null){
                var question=new Button{Content="?",Width=20,Height=20,MinWidth=0,MinHeight=0,Padding=new Thickness(0),CornerRadius=new CornerRadius(10),HorizontalContentAlignment=HorizontalAlignment.Center,VerticalContentAlignment=VerticalAlignment.Center};
                Avalonia.Automation.AutomationProperties.SetName(question,"About Reference PKG");
                TextBlock Explanation()=>new(){Text=help,TextWrapping=TextWrapping.Wrap,MaxWidth=410};
                ToolTip.SetTip(question,Explanation());
                var flyout=new Flyout{Content=Explanation()};
                question.Click+=(_,_)=>{ToolTip.SetIsOpen(question,false);flyout.ShowAt(question);};
                label.Children.Add(question);
            }
            row.Children.Add(label);Grid.SetColumn(field,1);row.Children.Add(field);
            if(browse!=null){var b=new Button{Content="Browse…",MinWidth=85};b.Click+=async(_,_)=>{try{await browse();}catch(Exception e){ShowMessage(e.Message);}};Grid.SetColumn(b,2);row.Children.Add(b);}
            settings.Children.Add(row);
        }
        verification.Items.Add("Full (format + integrity)");verification.Items.Add("Format only");verification.SelectedIndex=0;
        source.PlaceholderText="Game folder, exFAT or FFPFSC"; output.PlaceholderText="Choose an output folder";temporary.PlaceholderText="Optional (defaults to output folder)";reference.PlaceholderText="Optional (base PKG for a patch)";package.PlaceholderText="Existing .pkg for verify/extract";extract.PlaceholderText="Empty folder for extract";
        Row("Source",source,ChooseSource);Row("Output folder",output,()=>ChooseFolder(output));Row("Temporary folder",temporary,()=>ChooseFolder(temporary));Row("PKG file",package,ChoosePackage);Row("Extraction folder",extract,()=>ChooseFolder(extract));Row("Reference PKG",reference,ChooseReference,
            "Create an update for a PKG you already have. Leave this empty to build a full package.\n\n"+
            "Example:\nSource folder: the complete updated game folder (1.01).\nReference PKG: your previous game PKG (1.00), matching the installed base.\nOutput: a patch for that reference plus a full .remastered.pkg companion.\n\n"+
            "Keep the same title ID and content ID. The source folder's contentVersion must be higher than the reference, for example 01.000.000 to 01.001.000. Changing the version alone does not add updated game files.\n\n"+
            "The SDK builds the new image first, then compares it with the reference to create the delta. The patch can be smaller, but building it is not necessarily faster.\n\n"+
            "The reference is the old PKG, not a downloaded update. Both inputs remain unchanged.");
        ToolTip.SetTip(reference,"Optional: select the original PKG to create a patch. Increase contentVersion in the updated source folder. Also creates a .remastered.pkg companion.");Row("Compression",compression);Row("PlayGo chunks",chunks);Row("Passcode",passcode);Row("Verification",verification);settings.Children.Add(force);settings.Children.Add(keep);
        var upper=new Grid{ColumnDefinitions=new ColumnDefinitions("*,140"),ColumnSpacing=10};upper.Children.Add(settings);
        var frame=new Border{Child=icon,Width=128,Height=128,BorderThickness=new Thickness(1),Background=Brushes.Transparent,VerticalAlignment=VerticalAlignment.Top};frame.Bind(Border.BorderBrushProperty,new DynamicResourceExtension("ThemeBorderLowBrush"));Grid.SetColumn(frame,1);upper.Children.Add(frame);
        Grid.SetRow(upper,1);root.Children.Add(upper);
        source.TextChanged+=(_,_)=>{
            package.Text="";
            extract.Text="";
            iconTimer?.Stop();iconTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(350)};iconTimer.Tick+=(_,_)=>{iconTimer.Stop();RefreshIcon();};iconTimer.Start();
        };
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};actions.Children.Add(start);actions.Children.Add(selectPkg);actions.Children.Add(verify);actions.Children.Add(extractPkg);actions.Children.Add(openFolder);actions.Children.Add(clearLog);actions.Children.Add(cancel);
        var copy=new Button{Content="Copy log"};copy.Click+=async(_,_)=>{if(owner.Clipboard!=null)await owner.Clipboard.SetTextAsync(history.ToString());};actions.Children.Add(copy);
        var save=new Button{Content="Save log…"};save.Click+=async(_,_)=>{var f=await owner.StorageProvider.SaveFilePickerAsync(new(){Title="Save SDKFix log",SuggestedFileName="sdkfix-build.log"});if(f?.TryGetLocalPath() is string p)await File.WriteAllTextAsync(p,history.ToString());};actions.Children.Add(save);
        Grid.SetRow(actions,2);root.Children.Add(actions);
        var state=new StackPanel{Spacing=5};state.Children.Add(progress);state.Children.Add(status);state.Children.Add(metrics);Grid.SetRow(state,3);root.Children.Add(state);
        Grid.SetRow(log,4);root.Children.Add(log);Content=root;
        start.Click+=async(_,_)=>await Build();selectPkg.Click+=async(_,_)=>await ChoosePackage();verify.Click+=async(_,_)=>await Verify();extractPkg.Click+=async(_,_)=>await Extract();openFolder.Click+=(_,_)=>OpenResult();clearLog.Click+=(_,_)=>ClearLog();cancel.Click+=(_,_)=>Cancel();
    }
    void RefreshIcon()
    {
        Bitmap? next=null;
        try
        {
            string src=source.Text??"";
            string path=Path.Combine(src,"sce_sys","icon0.png");
            if(File.Exists(path)){using var stream=File.OpenRead(path);next=Bitmap.DecodeToWidth(stream,280);}
            else if(File.Exists(src))
            {
                byte[]? png=ImageSourceSession.Inspect(src)?.IconPng;
                if(png is {Length:>0}){using var stream=new MemoryStream(png);next=Bitmap.DecodeToWidth(stream,280);}
            }
        }
        catch { }
        icon.Source=next;iconBitmap?.Dispose();iconBitmap=next;
    }
    async Task ChooseFolder(TextBox box){var p=await owner.StorageProvider.OpenFolderPickerAsync(new(){Title="Choose folder",AllowMultiple=false});if(p.FirstOrDefault()?.TryGetLocalPath() is string s)box.Text=s;}
    async Task ChooseSource()
    {
        var files=await owner.StorageProvider.OpenFilePickerAsync(new(){
            Title="exFAT or FFPFSC (Cancel to pick a folder)",
            AllowMultiple=false,
            FileTypeFilter=[
                new("exFAT / FFPFSC"){Patterns=["*.exfat","*.ffpfsc","*.ffpfs","*.ffpfc"]},
                new("All files"){Patterns=["*"]}
            ]});
        if(files.FirstOrDefault()?.TryGetLocalPath() is string file){source.Text=file;return;}
        var folders=await owner.StorageProvider.OpenFolderPickerAsync(new(){Title="Source folder",AllowMultiple=false});
        if(folders.FirstOrDefault()?.TryGetLocalPath() is string folder)source.Text=folder;
    }
    async Task ChooseReference(){var p=await owner.StorageProvider.OpenFilePickerAsync(new(){Title="Reference PKG",AllowMultiple=false,FileTypeFilter=[new("PKG"){Patterns=["*.pkg"]}]});if(p.FirstOrDefault()?.TryGetLocalPath() is string s)reference.Text=s;}
    async Task ChoosePackage(){var p=await owner.StorageProvider.OpenFilePickerAsync(new(){Title="Select PKG",AllowMultiple=false,FileTypeFilter=[new("PKG"){Patterns=["*.pkg"]}]});if(p.FirstOrDefault()?.TryGetLocalPath() is string s){package.Text=s;if(string.IsNullOrWhiteSpace(extract.Text))extract.Text=Path.Combine(Path.GetDirectoryName(s)! ,Path.GetFileNameWithoutExtension(s)+"-extracted");}}
    void Busy(bool value){IsBusy=value;settings.IsEnabled=!value;start.IsEnabled=!value;verify.IsEnabled=!value;extractPkg.IsEnabled=!value;selectPkg.IsEnabled=!value;openFolder.IsEnabled=!value;clearLog.IsEnabled=!value;cancel.IsEnabled=value;BusyChanged?.Invoke(value);}
    public void ShowMessage(string message){status.Text=message;Append(message);}
    void Append(string line)
    {
        // Progress repaints never enter this history. Keep UI memory bounded.
        history.AppendLine($"[{DateTime.Now:HH:mm:ss}] {line}");if(history.Length>250000)history.Remove(0,history.Length-200000);
        int selectionStart=log.SelectionStart, selectionEnd=log.SelectionEnd;
        bool following=selectionStart==selectionEnd && log.CaretIndex>=Math.Max(0,(log.Text?.Length??0)-1);
        log.Text=history.ToString();if(following)log.CaretIndex=log.Text.Length;
    }
    void Consume(string line)
    {
        try{
            using var doc=JsonDocument.Parse(line);var r=doc.RootElement;string kind=r.GetProperty("kind").GetString()!;string text=r.TryGetProperty("text",out var t)?t.GetString()??"":"";
            if(kind=="progress"){progress.IsIndeterminate=false;progress.Value=r.GetProperty("value").GetDouble();sdkPercent=progress.Value;status.Text=$"{text} · {progress.Value:0.0}%";}
            else if(kind=="metrics"){sizeInfo=$" · Output size: {r.GetProperty("size").GetDouble()/1073741824:0.00} GiB · File growth: {r.GetProperty("growth").GetDouble()/1048576:0.0} MiB/s";}
            else if(kind=="stage"){progress.IsIndeterminate=true;sdkPercent=null;phaseStarted=DateTime.Now;sizeInfo="";status.Text=text;Append(text);}
            else if(kind=="done"){result=text;progress.IsIndeterminate=false;progress.Value=100;Append("Done: "+text);}
            else {Append(text);if(kind=="error")status.Text=text;}
        }catch{Append(line);}
    }
    void ClearLog(){history.Clear();log.Text="";status.Text="Ready";}
    void OpenResult()
    {
        string? path=lastFolder;
        if(string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(package.Text)) path=Path.GetDirectoryName(package.Text);
        if(string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)){ShowMessage("Nothing to open yet.");return;}
        Process.Start(new ProcessStartInfo("open"){Arguments="\""+path+"\"",UseShellExecute=false});
    }
    async Task Verify()
    {
        if(string.IsNullOrWhiteSpace(package.Text)){ShowMessage("Select an existing PKG first.");return;}
        await RunSdk("verify", extra=> { extra.Add("--package"); extra.Add(package.Text!); if(verification.SelectedIndex==1) extra.Add("--format-only"); }, success=> { lastFolder=Path.GetDirectoryName(success); });
    }
    async Task Extract()
    {
        if(string.IsNullOrWhiteSpace(package.Text)||string.IsNullOrWhiteSpace(extract.Text)){ShowMessage("Select a PKG file and an empty extraction folder first.");return;}
        bool sony=await RunSdk("extract", extra=> { extra.Add("--package"); extra.Add(package.Text!); extra.Add("--extract"); extra.Add(extract.Text!); }, success=> { lastFolder=success; });
        if(!sony && !requestedCancel) await ExtractWithPlainUnpacker();
    }
    async Task ExtractWithPlainUnpacker()
    {
        string pkg=package.Text!;
        string dest=extract.Text!;
        string code=passcode.Text??"";
        if(code.Length==0) code=new string('0',32);
        Append("Sony img_extract could not open this package. Trying the Plain unpacker (same as Inspect & Unpack).");
        Busy(true);status.Text="Extracting with Plain unpacker…";progress.IsIndeterminate=true;
        try
        {
            await Task.Run(()=>{
                Directory.CreateDirectory(dest);
                ProsperoPackageExtractor.Extract(pkg, dest, ProsperoExtractionKey.FromPasscode(code), null, line => Dispatcher.UIThread.Post(()=>Append(line)));
            });
            lastFolder=dest;
            status.Text="Done - "+dest;
            progress.IsIndeterminate=false;progress.Value=100;
            Append("Plain unpacker finished: "+dest);
        }
        catch(Exception e)
        {
            Append("Plain unpacker also failed: "+e.Message);
            status.Text="Extract failed – see log.";
            ShowMessage("SDK extract only opens packages built with this Sony toolchain. Other FPKGs: use Inspect & Unpack.\n\n"+e.Message);
        }
        finally{Busy(false);progress.IsIndeterminate=false;}
    }
    async Task Build()
    {
        if(IsBusy)return;
        if(string.IsNullOrWhiteSpace(source.Text)||string.IsNullOrWhiteSpace(output.Text)){ShowMessage("Choose a source folder or image, and an output folder first.");return;}
        ImageSourceSession? imageSession=null;
        string packRoot=source.Text!.Trim();
        requestedCancel=false;result=null;sdkPercent=null;sizeInfo="";phaseStarted=DateTime.Now;history.Clear();log.Text="";started=DateTime.Now;Busy(true);status.Text="Starting SDK Fix…";progress.IsIndeterminate=true;
        try
        {
            if(File.Exists(packRoot) && (ImageSourceSession.IsImagePath(packRoot) || ImageSourceSession.DetectKind(packRoot) is "exfat" or "ffpfsc"))
            {
                string tempRoot=string.IsNullOrWhiteSpace(temporary.Text)?output.Text!:temporary.Text!;
                Directory.CreateDirectory(tempRoot);
                Append("Preparing source image for SDK Fix 12…");
                status.Text="Preparing source image…";
                string kind=ImageSourceSession.DetectKind(packRoot);
                imageSession=await Task.Run(()=>ImageSourceSession.Prepare(packRoot,kind,tempRoot,line=>Dispatcher.UIThread.Post(()=>Append(line)),(done,total)=>Dispatcher.UIThread.Post(()=>{if(total<=0)return;progress.IsIndeterminate=false;progress.Value=100.0*done/total;status.Text=$"Preparing source image… {100.0*done/total:0}%";}),CancellationToken.None,forceExtract:false));
                packRoot=imageSession.AppFolder;
                Append("SDK source folder: "+packRoot);
                progress.IsIndeterminate=true;
                status.Text="Starting SDK Fix…";
            }
            if(!Directory.Exists(packRoot)){ShowMessage("Choose a source folder or an exFAT / FFPFSC image.");return;}
            string packageName=Path.GetFileName(packRoot.TrimEnd(Path.DirectorySeparatorChar));
            try {using var param=JsonDocument.Parse(File.ReadAllText(Path.Combine(packRoot,"sce_sys","param.json")));if(param.RootElement.TryGetProperty("contentId",out var cid)&&!string.IsNullOrWhiteSpace(cid.GetString()))packageName=cid.GetString()!;} catch { }
            packageName=string.Concat(packageName.Select(c=>char.IsLetterOrDigit(c)||c=='-'||c=='_'?c:'_'));
            if(string.IsNullOrEmpty(packageName))packageName="game";
            string packageOutput=Path.Combine(output.Text!,packageName+".pkg");
            string runtime=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../Resources/SDKRuntime"));
            var info=new ProcessStartInfo(Path.Combine(runtime,"python/bin/python3.12")){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
            foreach(var arg in new[]{"-I","-B","-u",Path.Combine(runtime,"sdk-runner.py"),"--mode","build","--source",packRoot,"--output",packageOutput,"--temp",temporary.Text??"","--reference",reference.Text??"","--compression",((int)(compression.Value??7)).ToString(),"--chunk-count",((int)(chunks.Value??100)).ToString(),"--passcode",passcode.Text??""})info.ArgumentList.Add(arg);
            if(keep.IsChecked==true)info.ArgumentList.Add("--keep");
            if(force.IsChecked==true)info.ArgumentList.Add("--force");
            info.Environment["PYTHONDONTWRITEBYTECODE"]="1";
            var queue=new System.Collections.Concurrent.ConcurrentQueue<string>();
            void Drain(){while(queue.TryDequeue(out var line))Consume(line);}
            timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(250)};timer.Tick+=(_,_)=>{Drain();var elapsed=DateTime.Now-started;metrics.Text="Elapsed: "+elapsed.ToString(@"hh\:mm\:ss")+sizeInfo;};timer.Start();
            process=new Process{StartInfo=info};if(!process.Start())throw new IOException("Could not start bundled Python.");
            async Task Pump(StreamReader reader){while(await reader.ReadLineAsync().ConfigureAwait(false) is string line)queue.Enqueue(line);}
            var stdout=Pump(process.StandardOutput);var stderr=Pump(process.StandardError);
            await process.WaitForExitAsync();await Task.WhenAll(stdout,stderr);Drain();
            if(requestedCancel){status.Text="Cancelled.";Append("Cancelled. Source files unchanged.");}
            else if(process.ExitCode==0 && result!=null){status.Text="Done – "+result;progress.IsIndeterminate=false;progress.Value=100;package.Text=result;lastFolder=Path.GetDirectoryName(result);}
            else{Append("Build failed (exit "+process.ExitCode+"). See the log above.");if(!status.Text!.Contains("failed",StringComparison.OrdinalIgnoreCase))status.Text="Build failed – see log.";}
        }
        catch(Exception e){ShowMessage(e.Message);}
        finally
        {
            timer?.Stop();
            imageSession?.Dispose();
            progress.IsIndeterminate=false;process?.Dispose();process=null;Busy(false);
        }
    }

    async Task<bool> RunSdk(string mode, Action<System.Collections.Generic.List<string>> extraArgs, Action<string> onSuccess)
    {
        if(IsBusy)return false;
        string runtime=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../Resources/SDKRuntime"));
        var info=new ProcessStartInfo(Path.Combine(runtime,"python/bin/python3.12")){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
        var args=new System.Collections.Generic.List<string>{"-I","-B","-u",Path.Combine(runtime,"sdk-runner.py"),"--mode",mode,"--passcode",passcode.Text??""};
        extraArgs(args);
        foreach(var arg in args)info.ArgumentList.Add(arg);
        info.Environment["PYTHONDONTWRITEBYTECODE"]="1";
        requestedCancel=false;result=null;sdkPercent=null;sizeInfo="";phaseStarted=DateTime.Now;Busy(true);status.Text="Starting SDK Fix…";progress.IsIndeterminate=true;
        var queue=new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Drain(){while(queue.TryDequeue(out var line))Consume(line);}
        timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(250)};timer.Tick+=(_,_)=>{Drain();var elapsed=DateTime.Now-started;metrics.Text="Elapsed: "+elapsed.ToString(@"hh\:mm\:ss")+sizeInfo;};
        started=DateTime.Now;timer.Start();
        bool ok=false;
        try{
            process=new Process{StartInfo=info};if(!process.Start())throw new IOException("Could not start bundled Python.");
            async Task Pump(StreamReader reader){while(await reader.ReadLineAsync().ConfigureAwait(false) is string line)queue.Enqueue(line);}
            var stdout=Pump(process.StandardOutput);var stderr=Pump(process.StandardError);
            await process.WaitForExitAsync();await Task.WhenAll(stdout,stderr);Drain();
            if(requestedCancel){status.Text="Cancelled.";Append("Cancelled.");}
            else if(process.ExitCode==0 && result!=null){status.Text="Done – "+result;progress.IsIndeterminate=false;progress.Value=100;onSuccess(result);ok=true;}
            else{Append("Operation failed (exit "+process.ExitCode+"). See the log above.");if(!status.Text!.Contains("failed",StringComparison.OrdinalIgnoreCase))status.Text="Operation failed – see log.";}
        }catch(Exception e){ShowMessage(e.Message);}
        finally{timer.Stop();Drain();progress.IsIndeterminate=false;process?.Dispose();process=null;Busy(false);}
        return ok;
    }

    [DllImport("libc",SetLastError=true)]static extern int kill(int pid,int signal);
    void Cancel(){if(process==null||process.HasExited)return;requestedCancel=true;cancel.IsEnabled=false;status.Text="Cancelling…";kill(process.Id,15);}
}
