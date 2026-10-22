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
        Set(session,"failedDevices",new CaptureRetryState());
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
        Set(session,"failedDevices",new CaptureRetryState());
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
    static void RetiredSourceFailureDoesNotRemoveReplacement() {
        var type=typeof(RecordingSession);var sourceType=type.GetNestedType("Source",BindingFlags.NonPublic);
        var session=(RecordingSession)FormatterServices.GetUninitializedObject(type);
        using(var changed=new AutoResetEvent(false)) {
            Set(session,"gate",new object());Set(session,"clock",Stopwatch.StartNew());Set(session,"devicesChanged",changed);
            Set(session,"captureProblems",new List<string>());Set(session,"notices",new Queue<string>());
            var retry=new CaptureRetryState();Set(session,"failedDevices",retry);
            var map=(IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string),sourceType));
            Set(session,"active",map);
            object retired=Activator.CreateInstance(sourceType,true),replacement=Activator.CreateInstance(sourceType,true);
            Set(retired,"Name","retired");Set(retired,"Ended",true);Set(retired,"EndMs",123.0);Set(retired,"EndReason","device_removed_or_route_changed");
            map["loopback:same-id"]=replacement;
            type.GetMethod("CaptureFailed",Private).Invoke(session,new object[]{retired,"loopback:same-id",new IOException("late driver callback")});
            Check(map.Contains("loopback:same-id")&&ReferenceEquals(map["loopback:same-id"],replacement),"retired source callback must not remove the healthy replacement with the same endpoint ID");
            Check(!(bool)Get(replacement,"Ended"),"retired source failure cannot end replacement lifecycle");
            Check((double)Get(retired,"EndMs")==123.0,"retired source failure must retain its original close boundary");
            Check(retry.CanRetry("loopback:same-id",0),"retired source failure cannot blacklist the replacement endpoint");
            Check((string)Get(retired,"Error")=="late driver callback","retired source error remains available for review");
        }
    }
    static void PacketClockBounds(string root) {
        bool estimated;
        double first = CaptureTrack.ResolvePacketStart(5508816, 5000000, 480, 48000, 480000, 479520, 4999980, 480, false, out estimated);
        Check(estimated && Math.Abs(first - 4999990) < .001, "a future driver timestamp must not shift already captured audio minutes ahead");
        double gap = CaptureTrack.ResolvePacketStart(0, 120, 160, 16000, 1600, 0, 0, 160, true, out estimated);
        Check(estimated && Math.Abs(gap - 100) < .001, "invalid QPC must preserve a reliable device-position gap, not compress it into one packet");
        double startup = CaptureTrack.ResolvePacketStart(0, 100, 160, 16000, 0, 0, double.NaN, 0, true, out estimated);
        Check(estimated && startup == 90, "first invalid timestamp must not call optional driver StreamLatency");
        double valid = CaptureTrack.ResolvePacketStart(10, 500, 160, 16000, 160, 0, 0, 160, false, out estimated);
        Check(!estimated && valid == 10, "valid delayed packets must retain hardware time, not callback time");
        double reset = CaptureTrack.ResolvePacketStart(1, 120, 160, 16000, 0, 1600, 100, 160, false, out estimated);
        Check(estimated && reset >= 110 && reset <= 120, "counter/timestamp reset must use bounded monotonic fallback");
        double ahead = CaptureTrack.ResolvePacketStart(110, 100, 160, 16000, 0, 0, double.NaN, 0, false, out estimated);
        Check(!estimated && ahead==110,"A small driver timestamp lead remains within existing tolerance");
        double next = CaptureTrack.ResolvePacketStart(0, 101, 160, 16000, 160, 0, ahead, 160, true, out estimated);
        Check(estimated && next>=120,"Invalid next packet cannot move behind the previous packet or truncate its samples");
        double positionReset = CaptureTrack.ResolvePacketStart(120, 130, 160, 16000, 0, 1600, 100, 160, false, out estimated);
        Check(estimated && positionReset>=110&&positionReset<=130,"A device-position reset is uncertain even when its QPC still increases");
        string leadPath=Path.Combine(root,"small-lead-followed-by-invalid.wav");
        using(var writer=new TimelineWriter(leadPath,new WaveFormat(16000,16,1))) {
            byte[] a=new byte[320],b=new byte[320];for(int i=0;i<160;i++){Buffer.BlockCopy(BitConverter.GetBytes((short)1000),0,a,i*2,2);Buffer.BlockCopy(BitConverter.GetBytes((short)2000),0,b,i*2,2);}
            writer.AddTimestamped(a,a.Length,ahead,0,false);writer.AddTimestamped(b,b.Length,next,160,true);writer.Finish(140);
        }
        using(var reader=new WaveFileReader(leadPath)) {
            byte[] sample=new byte[2];reader.Position=1760*2;reader.Read(sample,0,2);Check(BitConverter.ToInt16(sample,0)==1000,"Accepted preceding packet retains its first sample");
            reader.Position=1919*2;reader.Read(sample,0,2);Check(BitConverter.ToInt16(sample,0)==1000,"Accepted preceding packet retains its final sample");
            reader.Position=1920*2;reader.Read(sample,0,2);Check(BitConverter.ToInt16(sample,0)==2000,"Estimated next packet retains its exact adjacent boundary");
        }
        string path = Path.Combine(root,"bounded-driver-clock.wav");
        using(var writer = new TimelineWriter(path,new WaveFormat(16000,16,1))) {
            byte[] packet = new byte[320]; for(int i=0;i<160;i++) Buffer.BlockCopy(BitConverter.GetBytes((short)1234),0,packet,i*2,2);
            double at = CaptureTrack.ResolvePacketStart(5508816, 120, 160, 16000, 1600, 0, 0, 160, false, out estimated);
            writer.AddTimestamped(packet,packet.Length,at,1600,true); writer.Finish(120);
        }
        using(var reader = new WaveFileReader(path)) {
            reader.Position = 1600*2; byte[] value = new byte[2]; reader.Read(value,0,2);
            Check(BitConverter.ToInt16(value,0)==1234,"future-QPC packet must survive the actual stop boundary in the WAV");
        }
    }
    public static int Main(string[] args) {
        try {
            var retry = new CaptureRetryState();
            Check(retry.CanRetry("output", 0), "new endpoint is eligible");
            Check(retry.Fail("output", 0), "first failure can notify");
            Check(!retry.CanRetry("output", 2999), "failed endpoint has cooldown");
            Check(retry.CanRetry("output", 3000), "transient failure retries without endpoint notification");
            Check(!retry.Fail("output", 3000), "repeated open failure does not spam notifications");
            Check(!retry.CanRetry("output", 8999), "repeated failures back off");
            Check(retry.CanRetry("output", 9000), "repeated failure is not permanently blocked");
            Check(retry.CanRetry("mic", 0), "failure does not block another endpoint");
            retry.Succeeded("output");
            retry.Fail("output", 10000);
            Check(retry.CanRetry("output", 13000), "successful reopen resets backoff");
            for (int i=0;i<12;i++) retry.Fail("output", 0);
            Check(retry.CanRetry("output", 60000), "backoff is bounded");
            RetiredSourceFailureDoesNotRemoveReplacement();
            string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
            string selected=args.Length>1?args[1]:"all";
            if(selected=="all"||selected=="packet-clock")PacketClockBounds(root);
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
