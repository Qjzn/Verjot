using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using NAudio.Wave;
internal static class TestRecordingExport {
    static int assertions;
    static void Check(bool value,string message) { assertions++;if(!value)throw new Exception(message); }
    static void Reject(Action action,string message) { bool rejected=false;try{action();}catch(IOException){rejected=true;}Check(rejected,message); }
    static float[] ReadTrack(string session,Dictionary<string,object> entry,Dictionary<string,object> offsets) {
        using(var track=new NativeAudioExport.Track(session,entry,offsets)) {
            var output=new float[checked((int)track.Frames*2)];long p=0;
            while(p<track.Frames) {int n=(int)Math.Min(4096,track.Frames-p);var block=new float[n*2];track.AddBlock(block,n);Array.Copy(block,0,output,p*2,block.Length);p+=n;}
            return output;
        }
    }
    static void Timeline() {
        string session=Path.Combine(MeetingFiles.Home,"timeline");Directory.CreateDirectory(session);
        const int rate=44100,total=rate*3;var values=new float[total];
        // Markers straddle segment boundaries, including a one-frame tail.
        foreach(int at in new[]{rate-30,rate,rate+30,2*rate-20,2*rate,2*rate+20}) values[at]=.5f;
        var parts=new List<object>();
        for(int index=0,start=0;start<total;index++) {
            int count=Math.Min(rate-1,total-start);string name="mic.seg"+(index+1).ToString("D6")+".wav";
            using(var w=new WaveFileWriter(Path.Combine(session,name),WaveFormat.CreateIeeeFloatWaveFormat(rate,1)))for(int i=0;i<count;i++)w.WriteSample(values[start+i]);
            parts.Add(new {path=name,start_frame=start,frames=count});start+=count;
        }
        using(var w=new WaveFileWriter(Path.Combine(session,"mic.wav"),WaveFormat.CreateIeeeFloatWaveFormat(rate,1)))foreach(float value in values)w.WriteSample(value);
        var segmented=MeetingFiles.Json.Deserialize<Dictionary<string,object>>(MeetingFiles.Json.Serialize(new {source="mic",path="mic.wav",kind="microphone",sample_rate=rate,channels=1,bits_per_sample=32,segments=parts}));
        var continuous=new Dictionary<string,object>{{"source","mic"},{"path","mic.wav"},{"kind","microphone"}};
        var a=ReadTrack(session,segmented,new Dictionary<string,object>());var b=ReadTrack(session,continuous,new Dictionary<string,object>());
        double max=0;for(int i=0;i<a.Length;i++)max=Math.Max(max,Math.Abs(a[i]-b[i]));
        Check(a.Length==288000&&max==0,"Segment joins preserve resampler state, zero sample difference against continuous source");
        var shifted=ReadTrack(session,segmented,new Dictionary<string,object>{{"mic",137}});int delay=6576;
        // Read block shapes can differ when an offset is added; compare with a small numerical tolerance.
        max=0;for(int i=0;i<a.Length;i++)max=Math.Max(max,Math.Abs(a[i]-shifted[i+delay*2]));
        Check(max<.000001,"Source offset preserves every marker at exactly 137 milliseconds");
        Console.WriteLine("PASS timeline: segment error=0 samples; offset=6576 samples at 48000 Hz; maximum sample difference="+max);
    }
    static Dictionary<string,object> Fixture(string name,bool valid=true) {
        string home=MeetingFiles.Home,session=Path.Combine(home,"work",name),vault=Path.Combine(home,"vault");Directory.CreateDirectory(session);Directory.CreateDirectory(vault);
        var sources=new List<object>();
        foreach(string key in new[]{"mic","system"}) {
            int rate=key=="mic"?44100:48000,channels=key=="mic"?1:2,total=rate*6+rate/4;
            var parts=new List<object>();
            for(int index=0,start=0;start<total;index++) {
                int n=Math.Min(rate,total-start);string file=key+".seg"+(index+1).ToString("D6")+".wav";
                WaveFormat format=key=="system"?(WaveFormat)new WaveFormatExtensible(rate,32,channels):WaveFormat.CreateIeeeFloatWaveFormat(rate,channels);
                using(var writer=new WaveFileWriter(Path.Combine(session,file),format)) {
                    for(int i=0;i<n;i++) {
                        double t=(i+start)/(double)rate;
                        float v=(float)(.15*Math.Sin(2*Math.PI*(key=="mic"?500:1000)*t));
                        if(key=="mic"&&t>=3&&t<4)v=0;
                        for(int c=0;c<channels;c++) { byte[] value=BitConverter.GetBytes(v);writer.Write(value,0,value.Length); }
                    }
                }
                parts.Add(new {path=file,start_frame=start,frames=n});start+=n;
            }
            sources.Add(new {source=key,path=key+".wav",kind=key=="mic"?"microphone":"loopback",sample_rate=rate,channels=channels,bits_per_sample=32,segments=parts});
        }
        var metadata=new Dictionary<string,object>{{"state","recorded"},{"session_dir",session},{"duration_seconds",6.25},{"storage_mode","minimal"},
            {"start_time","2026-10-22T12:00:00+08:00"},{"audio_format","mp3"},{"mp3_bitrate_kbps",192},{"sources",sources},
            {"audio_output_dir",Path.Combine(vault,"会议记录","附件",name)}};
        if(valid)metadata["audio_export_version"]=1;
        MeetingFiles.WriteJson(Path.Combine(session,"session.json"),metadata);
        return new Dictionary<string,object>{{"vault_root",vault},{"meeting_folder","会议记录"}};
    }
    static void Handoff() {
        Directory.CreateDirectory(Path.Combine(MeetingFiles.Home,"vault"));
        var config=MeetingConfig.Validate(new Dictionary<string,object>{{"vault_root",Path.Combine(MeetingFiles.Home,"vault")},{"open_in_obsidian",false},{"check_updates",false},
            {"hotkey","Ctrl+Alt+Shift+F22"},{"pause_hotkey","Ctrl+Alt+Shift+F23"},{"quick_note_hotkey","Ctrl+Alt+Shift+F24"},{"show_processing_window",false}},false);
        MeetingFiles.WriteJson(Path.Combine(MeetingFiles.Home,"config.json"),config);
        int starts=0;Process child=null;
        var context=new MeetingContext(config,delegate(string session) {
            starts++;
            Check(File.Exists(Path.Combine(MeetingFiles.Home,"vault","会议记录","附件",Path.GetFileName(session),"meeting.mp3")),"Normal controller publishes MP3 before invoking transcription");
            Check(File.Exists(Path.Combine(session,"mic.seg000001.wav")),"Normal controller preserves lossless ASR inputs");
            child=Process.Start(new ProcessStartInfo{FileName=Application.ExecutablePath,Arguments="--wait-child",UseShellExecute=false,CreateNoWindow=true});return child;
        },delegate(string path,bool auto){});
        try {
            string session=Path.Combine(MeetingFiles.Home,"work","20261022-115900");Fixture("20261022-115900");
            typeof(MeetingContext).GetMethod("LaunchWorker",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(context,new object[]{session});
            var clock=Stopwatch.StartNew();
            while(starts==0&&clock.ElapsedMilliseconds<20000) {Thread.Sleep(40);Application.DoEvents();typeof(MeetingContext).GetMethod("Tick",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(context,null);}
            Check(starts==1,"Native export completion hands off to transcription exactly once");
        } finally {
            if (child != null) child.WaitForExit(5000);
            // Match the controller's normal exit path so native windows and
            // their callbacks are gone before this console fixture returns.
            context.ExitThread();
            context.Dispose();
            var receipt = (RecorderNotice)typeof(MeetingContext).GetField("receipt", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(context);
            Check(receipt == null || receipt.IsDisposed, "Export fixture closes its completion notice before process shutdown");
            if (child != null) child.Dispose();
        }
    }
    [STAThread]
    public static int Main(string[] args) {
        if(args.Length==2&&args[0]=="--export-audio")return NativeAudioExport.Run(args[1]);
        if(args.Length==1&&args[0]=="--wait-child"){Thread.Sleep(250);return 0;}
        try {
            Timeline();
            Handoff();
            string session=Path.Combine(MeetingFiles.Home,"work","20261022-120000");var config=Fixture("20261022-120000");
            string original=NativeAudioExport.Hash(Path.Combine(session,"mic.seg000001.wav"));
            var result=NativeAudioExport.Export(MeetingFiles.Home,session,config);string output=(string)result["published_path"];
            Check(File.Exists(output),"Recording must export MP3 without Python or a model");
            Check(!Directory.Exists(Path.Combine(MeetingFiles.Home,".venv"))&&!Directory.Exists(Path.Combine(MeetingFiles.Home,"models")),"Export must not install inference dependencies");
            Check(Convert.ToInt64(result["frames"])==300000,"Mixed frame count follows the shared timeline");
            Check(NativeAudioExport.Hash(Path.Combine(session,"mic.seg000001.wav"))==original,"Lossless sources remain byte-identical for first ASR");
            Check(!File.Exists(Path.Combine(session,"meeting.wav")),"Export streams without a duplicate full-length WAV");
            Check(Directory.GetFiles(Path.GetDirectoryName(output)).Length==1,"Daily output contains one MP3 only before ASR");
            DateTime stamp=File.GetLastWriteTimeUtc(output);string digest=NativeAudioExport.Hash(output);
            NativeAudioExport.Export(MeetingFiles.Home,session,config);
            Check(File.GetLastWriteTimeUtc(output)==stamp&&NativeAudioExport.Hash(output)==digest,"Retry reuses verified MP3 without re-encoding");
            File.Delete(output);NativeAudioExport.Export(MeetingFiles.Home,session,config);
            Check(NativeAudioExport.Hash(output)==digest,"Interrupted publication can recover from its journal without changing audio");
            byte[] bytes=File.ReadAllBytes(output);File.WriteAllBytes(output,new byte[]{1,2,3});
            Reject(delegate{NativeAudioExport.Export(MeetingFiles.Home,session,config);},"Changed public MP3 must never be overwritten");File.WriteAllBytes(output,bytes);
            string raw=Path.Combine(session,"system.seg000001.wav");byte[] wave=File.ReadAllBytes(raw);wave[wave.Length-1]^=1;File.WriteAllBytes(raw,wave);
            Reject(delegate{NativeAudioExport.Export(MeetingFiles.Home,session,config);},"Changed lossless input blocks reuse");
            string old=Path.Combine(MeetingFiles.Home,"work","20261022-120001");var oldConfig=Fixture("20261022-120001",false);
            Reject(delegate{NativeAudioExport.Export(MeetingFiles.Home,old,oldConfig);},"Old recordings must not be converted automatically");
            string broken=Path.Combine(MeetingFiles.Home,"work","20261022-120002");var brokenConfig=Fixture("20261022-120002");File.Delete(Path.Combine(broken,"mic.seg000002.wav"));
            Reject(delegate{NativeAudioExport.Export(MeetingFiles.Home,broken,brokenConfig);},"Missing raw segment blocks publishing");
            Check(Directory.GetFiles(broken,"*.wav").Length==13,"Failure retains all remaining recovery tracks");
            Console.WriteLine("PASS recording export: "+assertions+" assertions; fixture="+MeetingFiles.Home);return 0;
        }catch(Exception e){Console.Error.WriteLine(e.GetType().Name+": "+e.Message);return 1;}
    }
}
