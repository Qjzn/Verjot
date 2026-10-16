using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using NAudio.Wave;

// Fault injection into the actual production classes. No COM/audio devices,
// playback, global settings, model, user vault or large disk allocation.
internal static class TestCaptureReliability {
    static int assertions;
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    static void Set(object value, string name, object field) { value.GetType().GetField(name, Private | BindingFlags.Public).SetValue(value, field); }
    static object Get(object value, string name) { return value.GetType().GetField(name, Private | BindingFlags.Public).GetValue(value); }
    sealed class FaultStream : Stream {
        public readonly MemoryStream Bytes = new MemoryStream();
        public bool Closed, FailData, FailHeaders;
        public long DataFailureOffset, LengthBias;
        public override bool CanRead { get { return !Closed; } }
        public override bool CanSeek { get { return !Closed; } }
        public override bool CanWrite { get { return !Closed; } }
        public override long Length { get { return Bytes.Length + LengthBias; } }
        public override long Position { get { return Bytes.Position; } set { Bytes.Position = value; } }
        public override void Flush() { Bytes.Flush(); }
        public override int Read(byte[] buffer, int offset, int count) { return Bytes.Read(buffer, offset, count); }
        public override long Seek(long offset, SeekOrigin origin) { return Bytes.Seek(offset, origin); }
        public override void SetLength(long length) { Bytes.SetLength(length); }
        public override void Write(byte[] buffer, int offset, int count) {
            if (FailData && Position >= DataFailureOffset) throw new IOException("Injected disk-full data write");
            if (FailHeaders && Position < DataFailureOffset) throw new IOException("Injected secondary header write failure");
            Bytes.Write(buffer, offset, count);
        }
        protected override void Dispose(bool disposing) { Closed = true; if (disposing) Bytes.Dispose(); base.Dispose(disposing); }
    }
    static TimelineWriter WriterWithStream(string path, FaultStream stream) {
        var writer = new TimelineWriter(path, new WaveFormat(16000, 16, 1));
        ((WaveFileWriter)Get(writer, "writer")).Dispose();
        Set(writer, "writer", new WaveFileWriter(stream, new WaveFormat(16000, 16, 1)));
        return writer;
    }
    static CaptureTrack Track(string path, bool stoppedAlready) {
        var track = (CaptureTrack)FormatterServices.GetUninitializedObject(typeof(CaptureTrack));
        Set(track,"lifecycleGate",new object());
        Set(track,"deviceId","offline-endpoint"); Set(track,"DeviceName","offline-endpoint"); Set(track,"PathName",path);
        Set(track,"writer",new TimelineWriter(path,new WaveFormat(16000,16,1)));
        Set(track,"clock",Stopwatch.StartNew()); Set(track,"ready",new ManualResetEvent(true));
        Set(track,"stopped",new ManualResetEvent(stoppedAlready)); Set(track,"requestedStop",true); Set(track,"started",true);
        Set(track,"diagnostics",new List<Dictionary<string,object>>());
        return track;
    }
    static void FailedFinishClosesWriter(string root,bool failHeaders) {
        var stream = new FaultStream(); var writer = WriterWithStream(Path.Combine(root,"fault-finish.wav"),stream);
        byte[] packet = new byte[320];
        writer.AddTimestamped(packet,packet.Length,0,0,false);
        writer.AddTimestamped(packet,packet.Length,10,160,false);
        stream.DataFailureOffset=stream.Position;stream.FailData=true;stream.FailHeaders=failHeaders;
        Exception failure=null;
        try {writer.Finish(20);}catch(IOException error){failure=error;}
        try {
            Check(failure!=null,"finish must expose the injected storage failure");
            Check(failure.GetBaseException().Message=="Injected disk-full data write","secondary WAV header errors must not replace the original data-write error");
            Check(stream.Closed,"failed final packet/tail write must still close the WAV writer");
            if(!failHeaders) {
                byte[] bytes=stream.Bytes.ToArray();
                using (var prefix = new WaveFileReader(new MemoryStream(bytes)))
                    Check(prefix.Length==320,"failed finish must finalize the successfully written prefix header");
            }
        } finally {stream.FailData=false;writer.Dispose();}
    }
    static void TimedOutDisposeCanRetry(string root) {
        var track=Track(Path.Combine(root,"delayed-stop.wav"),false);
        var done=(ManualResetEvent)Get(track,"stopped");var ready=(ManualResetEvent)Get(track,"ready");
        bool timeout=false;
        try {track.Dispose();}catch(TimeoutException){timeout=true;}
        done.Set();track.Dispose();
        try {
            Check(timeout,"fixture must actually reach the bounded stop timeout");
            Check(done.SafeWaitHandle.IsClosed&&ready.SafeWaitHandle.IsClosed,"dispose must remain retryable when a timed-out recording thread later stops");
            using(var exclusive=new FileStream((string)Get(track,"PathName"),FileMode.Open,FileAccess.ReadWrite,FileShare.None)) {}
            Check(true,"a successful disposal retry must release the track file");
        } finally {
            if(!done.SafeWaitHandle.IsClosed){((TimelineWriter)Get(track,"writer")).Dispose();done.Dispose();ready.Dispose();}
        }
    }
    static void EndedSourceReleasesHandles(string root,bool failFinish) {
        var track=Track(Path.Combine(root,"ended-source.wav"),true);
        FaultStream fault=null;
        if(failFinish) {
            ((TimelineWriter)Get(track,"writer")).Dispose();fault=new FaultStream();
            Set(track,"writer",WriterWithStream(Path.Combine(root,"ended-source-failure.wav"),fault));
            fault.DataFailureOffset=fault.Position;fault.FailData=true;
        }
        var done=(ManualResetEvent)Get(track,"stopped");var ready=(ManualResetEvent)Get(track,"ready");
        var type=typeof(RecordingSession);var sourceType=type.GetNestedType("Source",BindingFlags.NonPublic);
        var session=(RecordingSession)FormatterServices.GetUninitializedObject(type);
        object source=Activator.CreateInstance(sourceType,true);
        Set(source,"Key","system");Set(source,"Kind","loopback");Set(source,"DeviceId","offline-endpoint");Set(source,"Name","offline-endpoint");
        Set(source,"Track",track);Set(source,"Ended",true);Set(source,"EndMs",20.0);
        var sources=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(sourceType));sources.Add(source);
        Set(session,"gate",new object());Set(session,"sources",sources);Set(session,"captureProblems",new List<string>());
        try {
            type.GetMethod("FinishEndedSources",Private).Invoke(session,null);
            Check((bool)Get(source,"Finished"),"ended source must be finalized");
            Check(done.SafeWaitHandle.IsClosed&&ready.SafeWaitHandle.IsClosed,"ended endpoint must release its waiting handles during the meeting");
            if(failFinish)Check(((List<string>)Get(session,"captureProblems")).Count==1,"failed end-of-source write must remain recorded as a capture problem");
            Check(track.Diagnostics.Length==0,"ended-source diagnostics remain readable after handle disposal");
            Check(track.Peak==0,"ended-source input level remains safe after handle disposal");
            track.SetPaused(true,20);track.SetEndBoundary(20);track.RequestStop();track.Finish(20);
            Check(true,"late idempotent source operations must not touch disposed wait handles");
        } finally {track.Dispose();}
    }
    static void ConcurrentDisposeIsIdempotent(string root) {
        var track=Track(Path.Combine(root,"concurrent-dispose.wav"),true);
        var errors=new List<Exception>();var threads=new List<Thread>();
        for(int i=0;i<8;i++)threads.Add(new Thread(delegate(){try{track.Dispose();}catch(Exception error){lock(errors)errors.Add(error);}}));
        foreach(var thread in threads)thread.Start();foreach(var thread in threads)thread.Join();
        Check(errors.Count==0,"concurrent disposal must not race a disposed waiting handle");
        Check(((ManualResetEvent)Get(track,"stopped")).SafeWaitHandle.IsClosed,"concurrent disposal must actually release handles");
    }
    static void RiffLimitUsesActualWriter(string root) {
        var stream=new FaultStream();var writer=WriterWithStream(Path.Combine(root,"riff-limit.wav"),stream);
        stream.LengthBias=uint.MaxValue-stream.Length-100;
        bool failed=false;
        try {writer.Add(new byte[320],320,10);}catch(Exception error){failed=error.GetBaseException() is ArgumentException&&error.GetBaseException().Message.IndexOf("WAV file too large",StringComparison.OrdinalIgnoreCase)>=0;}
        finally {stream.LengthBias=0;writer.Dispose();}
        Check(failed,"the bundled production WaveFileWriter has an actual UInt32 RIFF-size limit");
    }
    static Exception ActualWriteFailure(string root,bool capacity) {
        var stream=new FaultStream();var writer=WriterWithStream(Path.Combine(root,capacity?"capacity-failure.wav":"disk-failure.wav"),stream);
        if(capacity)stream.LengthBias=uint.MaxValue-stream.Length-100;
        else {stream.DataFailureOffset=stream.Position;stream.FailData=true;}
        Exception failure=null;
        try {writer.Add(new byte[320],320,10);}catch(Exception error){failure=error;}
        finally {stream.LengthBias=0;stream.FailData=false;writer.Dispose();}
        Check(failure!=null,"fixture must reach the real WaveFileWriter write failure");return failure;
    }
    static void StorageFailureNotifiesOnce(string root,bool capacity) {
        Exception failure=ActualWriteFailure(root,capacity);
        var type=typeof(RecordingSession);var sourceType=type.GetNestedType("Source",BindingFlags.NonPublic);
        var session=(RecordingSession)FormatterServices.GetUninitializedObject(type);
        var gate=new object();var changed=new AutoResetEvent(false);var completed=new ManualResetEvent(false);
        Set(session,"gate",gate);Set(session,"clock",Stopwatch.StartNew());Set(session,"devicesChanged",changed);
        Set(session,"captureProblems",new List<string>());Set(session,"notices",new Queue<string>());
        Set(session,"failedDevices",new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var dictionaryType=typeof(Dictionary<,>).MakeGenericType(typeof(string),sourceType);
        var active=(IDictionary)Activator.CreateInstance(dictionaryType);Set(session,"active",active);
        int callbacks=0;bool outsideCaptureThread=false,outsideGate=false;
        var threads=new List<Thread>();
        Action<Exception> callback=delegate(Exception error){
            Interlocked.Increment(ref callbacks);
            outsideCaptureThread=!threads.Contains(Thread.CurrentThread);
            outsideGate=!Monitor.IsEntered(gate);
            completed.Set();
        };
        // Prior production discarded this constructor argument entirely. Keep
        // that baseline runnable so failure is the missing notification itself.
        var failedField=type.GetField("failed",Private);if(failedField!=null)failedField.SetValue(session,callback);
        var method=type.GetMethod("CaptureFailed",Private);
        for(int i=0;i<8;i++) {
            string key="loopback:offline-"+i;
            object source=Activator.CreateInstance(sourceType,true);
            Set(source,"Name",key);active[key]=source;
            var thread=new Thread(delegate(){method.Invoke(session,new object[]{source,key,failure});});threads.Add(thread);
        }
        foreach(var thread in threads)thread.Start();foreach(var thread in threads)thread.Join();
        bool notified=completed.WaitOne(1500);
        try {
            Check(notified,"unrecoverable "+(capacity?"WAV-capacity":"disk-write")+" failure must notify the existing stop/save callback");
            Check(callbacks==1,"concurrent failed audio tracks must schedule exactly one whole-session failure callback");
            Check(outsideCaptureThread&&outsideGate,"fatal callback must run outside capture threads and the session lock");
        } finally {changed.Dispose();completed.Dispose();}
    }
    static void OrdinaryEndpointFailureContinues() {
        var type=typeof(RecordingSession);var sourceType=type.GetNestedType("Source",BindingFlags.NonPublic);
        var session=(RecordingSession)FormatterServices.GetUninitializedObject(type);
        var changed=new AutoResetEvent(false);int callbacks=0;
        Set(session,"gate",new object());Set(session,"clock",Stopwatch.StartNew());Set(session,"devicesChanged",changed);
        Set(session,"captureProblems",new List<string>());Set(session,"notices",new Queue<string>());
        Set(session,"failedDevices",new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var dictionaryType=typeof(Dictionary<,>).MakeGenericType(typeof(string),sourceType);
        var active=(IDictionary)Activator.CreateInstance(dictionaryType);Set(session,"active",active);
        object source=Activator.CreateInstance(sourceType,true);Set(source,"Name","ordinary endpoint");active["loopback:offline"]=source;
        var failedField=type.GetField("failed",Private);
        if(failedField!=null)failedField.SetValue(session,new Action<Exception>(delegate{Interlocked.Increment(ref callbacks);}));
        type.GetMethod("CaptureFailed",Private).Invoke(session,new object[]{source,"loopback:offline",new IOException("device was disconnected")});
        Check(callbacks==0&&(int)Get(session,"storageFailureNotified")==0,"ordinary endpoint I/O failures must not schedule whole-meeting termination");
        Check((bool)Get(source,"Ended")&&active.Count==0,"ordinary endpoint failure must still close the affected source lifecycle");
        changed.Dispose();
    }
    static void FloatSampleWritesDoNotAllocate(string root) {
        using(var writer=new TimelineWriter(Path.Combine(root,"float-sample-allocations.wav"),WaveFormat.CreateIeeeFloatWaveFormat(48000,2))) {
            var write=(Action<byte[],int,int,double>)Delegate.CreateDelegate(typeof(Action<byte[],int,int,double>),writer,typeof(TimelineWriter).GetMethod("WriteSample",Private));
            var allocationMethod=typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread",BindingFlags.Public|BindingFlags.Static);
            if(allocationMethod==null){Console.WriteLine("Allocation count is unavailable on this runtime; sample allocation check not measured.");return;}
            var allocated=(Func<long>)Delegate.CreateDelegate(typeof(Func<long>),allocationMethod);
            byte[] samples=new byte[8];
            double[] values={0,-0.0,.125,-.375,1.0,-1.0,double.NaN,double.PositiveInfinity};
            foreach(double value in values) {
                write(samples,0,1,value);
                Check(BitConverter.ToInt32(samples,4)==BitConverter.ToInt32(BitConverter.GetBytes((float)value),0),"float sample serialization must preserve the previous bit representation");
            }
            for(int i=0;i<100;i++)write(samples,0,i%2,.125);
            allocated();long before=allocated();
            for(int i=0;i<100000;i++)write(samples,0,i%2,.125);
            long delta=allocated()-before;
            Console.WriteLine("Float sample writes: 100000; managed allocation: "+delta+" bytes");
            Check(delta==0,"steady-state float sample serialization must not allocate an array for every sample");
        }
    }
    public static int Main(string[] args) {
        try {
            string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
            string selected=args.Length>1?args[1]:"all";
            if(selected=="all"||selected=="finish")FailedFinishClosesWriter(root,false);
            if(selected=="all"||selected=="header-fault")FailedFinishClosesWriter(root,true);
            if(selected=="all"||selected=="dispose")TimedOutDisposeCanRetry(root);
            if(selected=="all"||selected=="endpoint")EndedSourceReleasesHandles(root,false);
            if(selected=="all"||selected=="endpoint-fault")EndedSourceReleasesHandles(root,true);
            if(selected=="all"||selected=="concurrent")ConcurrentDisposeIsIdempotent(root);
            if(selected=="all"||selected=="riff")RiffLimitUsesActualWriter(root);
            if(selected=="all"||selected=="disk")StorageFailureNotifiesOnce(root,false);
            if(selected=="all"||selected=="capacity")StorageFailureNotifiesOnce(root,true);
            if(selected=="all"||selected=="ordinary")OrdinaryEndpointFailureContinues();
            if(selected=="all"||selected=="allocations")FloatSampleWritesDoNotAllocate(root);
            Console.WriteLine("Capture reliability: "+assertions+" assertions passed; "+selected);return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
