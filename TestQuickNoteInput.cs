using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using W = System.Windows;
using WC = System.Windows.Controls;
using WH = System.Windows.Interop;
using WI = System.Windows.Input;
using WT = System.Windows.Threading;

// Run through TestDesktopRunner with the production manifest and exe.config.
// The Unicode messages target this fixture's HWND; no global input or clipboard.
internal static class TestQuickNoteInput {
    sealed class Sample { internal long Sent, Dispatched, Changed; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
    static readonly object gate = new object();
    static readonly List<object> results = new List<object>();
    static ApplicationContext context;
    static Control dispatch;
    static QuickNoteWindow window;
    static QuickNoteStore store;
    static WC.RichTextBox editor;
    static WH.HwndSource source;
    static WH.HwndSourceHook hook;
    static System.Windows.Forms.Timer control;
    static System.Threading.Timer producer;
    static string root, expected;
    static int language, sent, dispatched, changed, postErrors, assertions;
    static Sample[] samples;
    static long phaseStarted, allChanged;
    static bool measuring, checkingCancellation, allFast=true;
    static double latencyLimit = 20;
    static double Milliseconds(long ticks) { return ticks * 1000d / Stopwatch.Frequency; }
    static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    static double P(List<double> values, double fraction) {
        if (values.Count == 0) return 0;
        values.Sort(); return values[Math.Min(values.Count - 1, (int)(values.Count * fraction))];
    }
    static object Stats(List<double> values) { return new { actual_count=values.Count,p50_ms=P(values,.5),p95_ms=P(values,.95),max_ms=P(values,1) }; }
    static string Text() { return QuickNoteMarkdown.Text(editor.Document, "\n"); }
    static void Fail(Exception error) {
        Console.Error.WriteLine(error); Environment.ExitCode=1;
        if(producer!=null) producer.Dispose(); producer=null;
        if(control!=null) control.Stop();
        context.ExitThread();
    }
    static void BeginLanguage() {
        try {
            string seed=language==0?"abcdefghijklmnopqrstuvwxyz":"这是听记输入回归测试，中文文字和标点应完整保存。";
            var text=new StringBuilder(); for(int i=0;i<80;i++) text.Append(seed[i%seed.Length]); expected=text.ToString();
            string vault=Path.Combine(root,"vault"); Directory.CreateDirectory(vault);
            store=new QuickNoteStore(vault,"sample-"+language,Path.Combine(root,"recovery"));
            window=new QuickNoteWindow(store,Path.Combine(root,"state-"+language));
            foreach(Control child in window.Controls) {
                var host=child as ElementHost;
                if(host!=null) editor=host.Child as WC.RichTextBox;
            }
            Check(editor!=null,"The public ElementHost child must be the real quick-note editor.");
            window.Toggle(); phaseStarted=Stopwatch.GetTimestamp();
            control=new System.Windows.Forms.Timer { Interval=25 };
            control.Tick+=delegate {
                try {
                    if(producer==null) {
                        Check(Milliseconds(Stopwatch.GetTimestamp()-phaseStarted)<8000,"The fixture must load and handle cancelled input.");
                        if(checkingCancellation) return;
                        if(editor.IsReadOnly) { Check(Milliseconds(Stopwatch.GetTimestamp()-phaseStarted)<8000,"The fixture must load."); return; }
                        if(Milliseconds(Stopwatch.GetTimestamp()-phaseStarted)<350) return;
                        StartInput(); return;
                    }
                    bool done; lock(gate) done=changed==80 && Milliseconds(Stopwatch.GetTimestamp()-allChanged)>250;
                    if(done) FinishInput();
                    else Check(Milliseconds(Stopwatch.GetTimestamp()-phaseStarted)<12000,"All directed characters must arrive before timeout.");
                } catch(Exception error) { Fail(error); }
            };
            control.Start();
        } catch(Exception error) { Fail(error); }
    }
    static void StartInput() {
        Check(Text()=="","Each input language starts with an empty note.");
        editor.Focus(); WI.Keyboard.Focus(editor); editor.UpdateLayout();
        Check(editor.IsKeyboardFocused,"The fixture editor must own keyboard focus.");
        source=(WH.HwndSource)W.PresentationSource.FromVisual(editor);
        Check(source!=null,"The real ElementHost must expose a test HWND.");
        checkingCancellation=true;
        WI.TextCompositionEventHandler cancel=null;
        cancel=delegate(object sender,WI.TextCompositionEventArgs args) {
            args.Handled=true; editor.PreviewTextInput-=cancel;
            // Wait until this complete routed input event has returned before
            // checking that its cancelled preview left no scheduling hook.
            dispatch.BeginInvoke((Action)delegate {
                try {
                    Check(Text()=="","A character cancelled in PreviewTextInput must never be inserted.");
                    CheckIdlePriority(delegate { checkingCancellation=false; StartMeasuredInput(); });
                } catch(Exception error) { Fail(error); }
            });
        };
        editor.PreviewTextInput+=cancel;
        Check(PostMessage(source.Handle,0x102,new IntPtr('x'),new IntPtr(1)),"The cancelled fixture character must post.");
    }
    static void StartMeasuredInput() {
        sent=dispatched=changed=postErrors=0; allChanged=0; samples=new Sample[80]; measuring=true;
        hook=delegate(IntPtr hwnd,int message,IntPtr wp,IntPtr lp,ref bool handled) {
            if(message==0x102) lock(gate) { if(dispatched<samples.Length) samples[dispatched++].Dispatched=Stopwatch.GetTimestamp(); }
            return IntPtr.Zero;
        };
        source.AddHook(hook); editor.TextChanged+=Changed;
        IntPtr target=source.Handle;
        Console.WriteLine("START language="+(language==0?"English":"Chinese BMP Unicode")+"; dpi="+window.DeviceDpi+"; limit="+latencyLimit+"ms");
        producer=new System.Threading.Timer(delegate {
            lock(gate) {
                if(!measuring || sent>=samples.Length) return;
                int index=sent++; samples[index]=new Sample { Sent=Stopwatch.GetTimestamp() };
                if(!PostMessage(target,0x102,new IntPtr(expected[index]),new IntPtr(1))) postErrors++;
            }
        },null,32,32);
    }
    static void Changed(object sender, WC.TextChangedEventArgs args) {
        long timestamp=Stopwatch.GetTimestamp();
        lock(gate) if(measuring && changed<samples.Length) {
            samples[changed++].Changed=timestamp;
            if(changed==samples.Length) allChanged=timestamp;
        }
    }
    static void FinishInput() {
        measuring=false; control.Stop(); control.Dispose(); control=null;
        producer.Dispose(); producer=null; source.RemoveHook(hook); editor.TextChanged-=Changed;
        Check(sent==80 && dispatched==80 && changed==80 && postErrors==0,"Exactly 80 targeted Unicode characters must dispatch and change text.");
        Check(Text()==expected,"Typing must preserve every character in order.");
        var latency=new List<double>(); var queue=new List<double>(); var intervals=new List<double>();
        var raw=new List<object>(); long previous=0;
        foreach(Sample sample in samples) {
            Check(sample.Changed>=sample.Dispatched && sample.Dispatched>=sample.Sent,"Every input timestamp must be ordered.");
            latency.Add(Milliseconds(sample.Changed-sample.Dispatched)); queue.Add(Milliseconds(sample.Dispatched-sample.Sent));
            if(previous!=0) intervals.Add(Milliseconds(sample.Sent-previous)); previous=sample.Sent;
            raw.Add(new { sent_ticks=sample.Sent,dispatched_ticks=sample.Dispatched,text_changed_ticks=sample.Changed });
        }
        object summary=new { language=language==0?"English":"Chinese BMP Unicode",actual_count=changed,dispatch_to_text_changed=Stats(latency),post_to_dispatch=Stats(queue),actual_send_interval=Stats(intervals) };
        results.Add(new { summary=summary,stopwatch_frequency=Stopwatch.Frequency,event_ticks=raw });
        Console.WriteLine(new JavaScriptSerializer().Serialize(summary));
        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"quick-note-input-report.json"),new JavaScriptSerializer().Serialize(results),new UTF8Encoding(false));
        Check(editor.CanUndo,"Real directed typing must remain undoable."); editor.Undo();
        Check(Text()!=expected,"Undo must change the typed document.");
        Check(editor.CanRedo,"Typed content must remain redoable."); editor.Redo();
        Check(Text()==expected,"Redo must restore the exact typed document.");
        // A cancelled preview never reaches RichTextBox's TextInput class
        // handler. An already handled bubble still needs to leave its scope.
        var composition=new WI.TextComposition(WI.InputManager.Current,editor,"不应插入");
        var preview=new WI.TextCompositionEventArgs(WI.Keyboard.PrimaryDevice,composition) {
            RoutedEvent=WI.TextCompositionManager.PreviewTextInputEvent,Handled=true
        };
        editor.RaiseEvent(preview);
        var handled=new WI.TextCompositionEventArgs(WI.Keyboard.PrimaryDevice,composition) {
            RoutedEvent=WI.TextCompositionManager.TextInputEvent,Handled=true
        };
        editor.RaiseEvent(handled);
        Check(Text()==expected,"Cancelled or handled input must not insert text.");
        editor.IsReadOnly=true;
        editor.RaiseEvent(new WI.TextCompositionEventArgs(WI.Keyboard.PrimaryDevice,composition) {
            RoutedEvent=WI.TextCompositionManager.TextInputEvent
        });
        editor.IsReadOnly=false;
        Check(Text()==expected,"Read-only input must not edit the document or keep a scheduling scope open.");
        // The latency assertion is deferred until storage/Undo and idle-priority
        // checks also run, so a RED report still verifies content safety.
        allFast=allFast && P(latency,.95)<latencyLimit;
        CheckIdlePriority(delegate {
            Action closed=delegate {
                try {
                    Check(window.IsDisposed,"The close continuation runs after disposal.");
                    Check(store.Load(DateTime.Today).Text==expected,"Closing must durably save exact input after Undo/Redo.");
                    language++;
                    if(language<2) BeginLanguage();
                    else {
                        Console.WriteLine("Content safety and idle-priority assertions passed="+assertions+" for both languages.");
                        Check(allFast,"Input p95 dispatch-to-TextChanged must remain below "+latencyLimit+"ms in both languages.");
                        Console.WriteLine("PASS assertions="+assertions+"; English and Chinese exact input, Undo/Redo, durable close and unchanged idle Background scheduling."); context.ExitThread();
                    }
                } catch(Exception error) { Fail(error); }
            };
            if(window.FlushAndClose(closed)) closed();
        });
    }
    static void CheckIdlePriority(Action done) {
        var order=new List<string>(); WT.DispatcherOperation background=null;
        background=editor.Dispatcher.BeginInvoke(WT.DispatcherPriority.Background,(Action)delegate {
            try {
                order.Add("background");
                Check(background.Priority==WT.DispatcherPriority.Background,"Unrelated idle Background work must keep its original priority.");
                Check(order.Count==2 && order[0]=="normal","Idle scheduling must still execute Normal before Background.");
                done();
            } catch(Exception error) { Fail(error); }
        });
        editor.Dispatcher.BeginInvoke(WT.DispatcherPriority.Normal,(Action)delegate { order.Add("normal"); });
    }
    [STAThread] static int Main(string[] args) {
        for(int i=0;i<args.Length;i++) if(args[i]=="--latency-limit" && i+1<args.Length)
            latencyLimit=double.Parse(args[++i],System.Globalization.CultureInfo.InvariantCulture);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        root=Path.Combine(Path.GetTempPath(),"tingji-input-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); context=new ApplicationContext(); dispatch=new Control(); IntPtr ignored=dispatch.Handle;
        dispatch.BeginInvoke((Action)BeginLanguage); Application.Run(context);
        if(window!=null) window.Dispose(); dispatch.Dispose(); context.Dispose();
        string full=Path.GetFullPath(root),parent=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        if(full.StartsWith(parent,StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("tingji-input-",StringComparison.Ordinal))
            try { Directory.Delete(full,true); } catch { }
        return Environment.ExitCode;
    }
}
