using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

// Model-free export runs in an independent process. No Python, models, sound
// playback, system settings, clipboard or network is used by this module.
internal static class NativeAudioExport {
    internal const int Rate = 48000;
    internal static string Hash(string path) { using(var sha=SHA256.Create()) using(var file=File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant(); }
    static double Number(Dictionary<string,object> row,string key,double fallback=0) {
        object value; double result = row.TryGetValue(key,out value) ? Convert.ToDouble(value,CultureInfo.InvariantCulture) : fallback;
        if(double.IsNaN(result)||double.IsInfinity(result)||result<0) throw new IOException("录音时间或格式无效："+key);
        return result;
    }
    internal sealed class Track : IDisposable {
        readonly List<string> paths=new List<string>();
        readonly List<long> lengths=new List<long>();
        readonly WaveFormat format;
        readonly WaveFormat sampleFormat;
        readonly long offset;
        readonly int rate;
        int index; long remaining, position; WaveFileReader reader;
        ISampleProvider samples;
        public readonly long Frames;
        public Track(string session,Dictionary<string,object> entry,Dictionary<string,object> offsets) {
            string source=MeetingFiles.Text(entry,"source","");
            if(!System.Text.RegularExpressions.Regex.IsMatch(source,@"\A(?:mic|system)(?:_\d{2,})?\z") || MeetingFiles.Text(entry,"path","")!=source+".wav" ||
                MeetingFiles.Text(entry,"kind","")!=(source.StartsWith("mic")?"microphone":"loopback")) throw new IOException("录音来源清单无效");
            object parts; long total=0;
            if(entry.TryGetValue("segments",out parts)) {
                var list=parts as IEnumerable; if(list==null)throw new IOException("录音分段清单无效");
                int n=0;
                foreach(object item in list) {
                    var part=item as Dictionary<string,object>;n++;
                    if(part==null || MeetingFiles.Text(part,"path","")!=source+".seg"+n.ToString("D6")+".wav" || Number(part,"start_frame")!=total) throw new IOException("录音分段不连续");
                    long frames=checked((long)Number(part,"frames"));
                    if(Number(part,"frames")!=frames) throw new IOException("录音分段帧数无效");
                    Add(session,MeetingFiles.Text(part,"path",""),frames,ref total);
                }
                if(n==0)throw new IOException("录音分段为空");
            } else Add(session,source+".wav",-1,ref total);
            using(var header=new WaveFileReader(paths[0])) format=header.WaveFormat;
            sampleFormat=format;
            if(format.Encoding==WaveFormatEncoding.Extensible) {
                // WaveFileReader 1.10 exposes extensible WAV as ExtraData,
                // rather than WaveFormatExtensible. Preserve source bytes and
                // normalize only the decoder's format descriptor.
                var extra=format as WaveFormatExtraData;
                if(extra==null||extra.ExtraData.Length<22)throw new IOException("扩展录音格式不完整");
                byte[] guid=new byte[16];Buffer.BlockCopy(extra.ExtraData,6,guid,0,16);
                Guid sub=new Guid(guid);
                if(sub==new Guid("00000003-0000-0010-8000-00aa00389b71")&&format.BitsPerSample==32)
                    sampleFormat=WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate,format.Channels);
                else if(sub==new Guid("00000001-0000-0010-8000-00aa00389b71"))
                    sampleFormat=new WaveFormat(format.SampleRate,format.BitsPerSample,format.Channels);
                else throw new IOException("不支持的扩展录音格式");
            }
            rate=format.SampleRate;
            if(entry.ContainsKey("segments") && (Number(entry,"sample_rate")!=rate || Number(entry,"channels")!=format.Channels || Number(entry,"bits_per_sample")!=format.BitsPerSample)) throw new IOException("录音格式与清单不符");
            foreach(string path in paths) using(var header=new WaveFileReader(path)) if(!header.WaveFormat.Equals(format)) throw new IOException("录音分段格式发生变化");
            offset=checked((long)Math.Round(Number(offsets,source)*Rate/1000));
            Frames=checked(offset+(total*Rate+rate-1)/rate);
        }
        void Add(string session,string name,long expected,ref long total) {
            string path=Path.Combine(session,name); MeetingConfig.CheckOrdinaryPath(path);
            using(var header=new WaveFileReader(path)) {
                long frames=header.Length/header.WaveFormat.BlockAlign;
                if(header.Length%header.WaveFormat.BlockAlign!=0 || (expected>=0&&frames!=expected))throw new IOException("录音分段被截断："+name);
                paths.Add(path); lengths.Add(header.Length); total=checked(total+frames);
            }
        }
        internal IEnumerable<string> Paths { get { return paths; } }
        sealed class Concat : IWaveProvider {
            readonly Track owner;
            public Concat(Track owner) { this.owner=owner; }
            public WaveFormat WaveFormat {get{return owner.sampleFormat;}}
            public int Read(byte[] buffer,int start,int count) {
                int done=0;
                while(done<count&&owner.index<owner.paths.Count) {
                    if(owner.reader==null) { owner.reader=new WaveFileReader(owner.paths[owner.index]); owner.remaining=owner.lengths[owner.index]; }
                    int want=(int)Math.Min(count-done,owner.remaining);
                    int got=owner.reader.Read(buffer,start+done,want);
                    if(got<want)throw new IOException("录音分段读取不完整");
                    done+=got;owner.remaining-=got;
                    if(owner.remaining==0) { owner.reader.Dispose();owner.reader=null;owner.index++; }
                }
                return done;
            }
        }
        public void AddBlock(float[] output,int frames) {
            if(samples==null) { samples=new Concat(this).ToSampleProvider(); if(rate!=Rate)samples=new WdlResamplingSampleProvider(samples,Rate); }
            int before=(int)Math.Min(frames,Math.Max(0,offset-position));
            int count=(int)Math.Min(frames-before,Math.Max(0,Frames-Math.Max(position,offset)));
            if(count>0) {
                float[] input=new float[count*format.Channels]; int got=0,n;
                while(got<input.Length&&(n=samples.Read(input,got,input.Length-got))>0)got+=n;
                if(got<input.Length && Frames-(position+before+got/format.Channels)>64)throw new IOException("重采样录音长度不完整");
                for(int i=0;i<count;i++) {
                    float left=input[i*format.Channels], right=format.Channels==1?left:input[i*format.Channels+1];
                    if(format.Channels>2) { double sum=0;for(int c=0;c<format.Channels;c++)sum+=input[i*format.Channels+c];left=right=(float)(sum/format.Channels); }
                    if(float.IsNaN(left)||float.IsInfinity(left)||float.IsNaN(right)||float.IsInfinity(right))throw new IOException("录音包含无效样本");
                    output[(i+before)*2]+=left;output[(i+before)*2+1]+=right;
                }
            }
            position+=frames;
        }
        public void Dispose() { if(reader!=null)reader.Dispose(); }
    }
    static List<Track> Tracks(string session,Dictionary<string,object> metadata) {
        var tracks=new List<Track>();
        try {
            object sourcesValue,offsetValue;
            var offsets=metadata.TryGetValue("capture_offsets_ms",out offsetValue)?offsetValue as Dictionary<string,object>:null;
            offsets=offsets??new Dictionary<string,object>();
            if(!metadata.TryGetValue("sources",out sourcesValue)||!(sourcesValue is IEnumerable))throw new IOException("新录音缺少来源清单");
            var seen=new HashSet<string>();
            foreach(object item in (IEnumerable)sourcesValue) {
                var entry=item as Dictionary<string,object>;
                if(entry==null||!seen.Add(MeetingFiles.Text(entry,"source","")))throw new IOException("录音来源重复或无效");
                tracks.Add(new Track(session,entry,offsets));
            }
            if(tracks.Count==0)throw new IOException("没有可合并的录音");
            return tracks;
        } catch { foreach(var track in tracks)track.Dispose();throw; }
    }
    static List<Dictionary<string,object>> Inputs(List<Track> tracks) {
        var files=new List<Dictionary<string,object>>();
        foreach(var track in tracks)foreach(string path in track.Paths)files.Add(new Dictionary<string,object>{{"name",Path.GetFileName(path)},{"size",new FileInfo(path).Length},{"sha256",Hash(path)}});
        return files;
    }
    static void WriteStatus(string session,string message,string state,string stage,Dictionary<string,object> audio=null,string error=null) {
        var status=MeetingFiles.ReadJson(Path.Combine(session,"status.json"));
        status["state"]=state;status["stage"]=stage;status["message"]=message;status["updated_at"]=DateTimeOffset.Now.ToString("o");
        status.Remove("error");if(error!=null)status["error"]=error;
        if(audio!=null)status["published_audio"]=new Dictionary<string,object>{{"path",audio["published_path"]},{"size",audio["output_size"]},{"sha256",audio["output_sha256"]}};
        MeetingFiles.WriteJson(Path.Combine(session,"status.json"),status);
    }
    internal static Dictionary<string,object> Export(string home,string session,Dictionary<string,object> config) {
        session=Path.GetFullPath(session);
        var catalog=new SessionCatalog(home,MeetingFiles.VaultRoot(config));
        if(catalog.Get(session)==null)throw new IOException("录音不属于当前程序");
        string metadataPath=Path.Combine(session,"session.json");
        var metadata=MeetingFiles.ReadJson(metadataPath);
        string captureState=MeetingFiles.Text(metadata,"state","");
        if(Number(metadata,"audio_export_version")!=1 || (captureState!="recorded"&&captureState!="recorded_with_error"))throw new IOException("仅导出新版已结束的录音，不修改已有录音");
        string output=Path.Combine(MeetingFiles.VaultRoot(config),MeetingFiles.Text(config,"meeting_folder","会议记录"),"附件",Path.GetFileName(session));
        if(!string.Equals(Path.GetFullPath(MeetingFiles.Text(metadata,"audio_output_dir","")),Path.GetFullPath(output),StringComparison.OrdinalIgnoreCase))throw new IOException("录音保存位置已改变，请恢复录制时保存位置后重试");
        output=Path.Combine(output,"meeting.mp3");MeetingConfig.CheckOrdinaryPath(output);
        string manifest=Path.Combine(session,"native_audio.json"),lockPath=Path.Combine(session,"audio-export.lock");
        MeetingConfig.CheckOrdinaryPath(lockPath);
        using(var gate=new FileStream(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)) {
            string metadataHash=Hash(metadataPath);var tracks=Tracks(session,metadata);
            string temporary=null;
            try {
                var inputs=Inputs(tracks);
                if(File.Exists(manifest)) {
                    var saved=MeetingFiles.ReadJson(manifest);
                    var audio=saved["merged_audio"] as Dictionary<string,object>;
                    if(MeetingFiles.Text(saved,"metadata_sha256","")!=metadataHash || MeetingFiles.Json.Serialize(saved["inputs"])!=MeetingFiles.Json.Serialize(inputs) ||
                        audio==null||MeetingFiles.Text(audio,"published_path","")!=output)throw new IOException("录音或导出文件已改变，保留现有文件，不自动覆盖");
                    if(File.Exists(output)) {
                        if(Hash(output)!=MeetingFiles.Text(audio,"output_sha256",""))throw new IOException("已导出 MP3 被修改，不自动覆盖");
                        NativeMp3Encoder.Validate(output,Convert.ToInt64(audio["frames"]),Convert.ToInt32(audio["bitrate_kbps"]));
                        WriteStatus(session,"MP3 已保存，准备转写","audio_ready","encode",audio);return audio;
                    }
                }
                if(File.Exists(output))throw new IOException("目标 MP3 已存在且无法确认归属，不自动覆盖");
                WriteStatus(session,"正在合并并导出 MP3，原始分轨保留","processing","encode");
                long frames=checked((long)Math.Round(Number(metadata,"duration_seconds")*Rate));
                foreach(var track in tracks)frames=Math.Max(frames,track.Frames);
                if(frames<=0)throw new IOException("录音为空");
                double peak=0;
                for(long p=0;p<frames;p+=4096) {
                    int n=(int)Math.Min(4096,frames-p);float[] block=new float[n*2];foreach(var track in tracks)track.AddBlock(block,n);
                    foreach(float value in block)peak=Math.Max(peak,Math.Abs(value));
                }
                foreach(var track in tracks)track.Dispose();tracks=Tracks(session,metadata);
                double gain=peak>0?Math.Min(1,.98/peak):1;
                Directory.CreateDirectory(Path.GetDirectoryName(output));MeetingConfig.CheckOrdinaryPath(output);
                temporary=Path.Combine(Path.GetDirectoryName(output),".meeting-"+Guid.NewGuid().ToString("N")+".tmp");
                int bitrate=checked((int)Number(metadata,"mp3_bitrate_kbps",192));
                using(var encoder=new NativeMp3Encoder(home,temporary,bitrate)) {
                    for(long p=0;p<frames;p+=4096) {
                        int n=(int)Math.Min(4096,frames-p);float[] block=new float[n*2];foreach(var track in tracks)track.AddBlock(block,n);
                        short[] pcm=new short[n*2];for(int i=0;i<pcm.Length;i++)pcm[i]=(short)Math.Round(Math.Max(-1,Math.Min(1,block[i]*gain))*32767);
                        encoder.Write(pcm,n);
                    }
                }
                int mpegFrames=NativeMp3Encoder.Validate(temporary,frames,bitrate);
                if(Hash(metadataPath)!=metadataHash || MeetingFiles.Json.Serialize(Inputs(tracks))!=MeetingFiles.Json.Serialize(inputs))throw new IOException("导出期间原始录音发生变化，已保留资料");
                string digest=Hash(temporary);MeetingConfig.CheckOrdinaryPath(output);
                var mixed=new Dictionary<string,object>{{"complete",true},{"format","mp3"},{"container","MP3"},{"subtype","MPEG_LAYER_III"},
                    {"sample_rate",Rate},{"channels",2},{"frames",frames},{"decoded_frames",frames},{"duration_seconds",frames/(double)Rate},
                    {"bitrate_kbps",bitrate},{"mpeg_frames",mpegFrames},{"output_size",new FileInfo(temporary).Length},{"output_sha256",digest},{"published_path",output},
                    {"path","meeting.mp3"},{"gain",gain},{"mixed_peak_before_gain",peak},{"encoder","native_lame_3.100_v1"}};
                MeetingFiles.WriteJson(manifest,new {schema_version=1,metadata_sha256=metadataHash,inputs=inputs,merged_audio=mixed});
                // Persist ownership before publishing: an interruption may
                // leave a missing output or an owned output, never an
                // unidentifiable MP3 that a retry would have to overwrite.
                MeetingConfig.CheckOrdinaryPath(output);File.Move(temporary,output);temporary=null;
                WriteStatus(session,"MP3 已保存，准备转写","audio_ready","encode",mixed);return mixed;
            } finally { foreach(var track in tracks)track.Dispose();if(temporary!=null&&File.Exists(temporary))File.Delete(temporary); }
        }
    }
    internal static int Run(string session) {
        try { Export(MeetingFiles.Home,session,MeetingConfig.Load());return 0; }
        catch(Exception error) {
            try {
                var catalog=new SessionCatalog(MeetingFiles.Home,"");catalog.Get(session);
                if(MeetingFiles.Text(MeetingFiles.ReadJson(Path.Combine(session,"session.json")),"audio_export_version","")=="1")
                    WriteStatus(session,"MP3 导出失败，无损录音已保留；可从最近会议重试","error","encode",null,error.Message);
            }catch { }
            MeetingFiles.Log(error);return 1;
        }
    }
}

internal sealed class NativeMp3Encoder : IDisposable {
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr LoadLibraryEx(string name,IntPtr file,uint flags);
    [DllImport("kernel32.dll",CharSet=CharSet.Ansi)]static extern IntPtr GetProcAddress(IntPtr module,string name);
    [DllImport("kernel32.dll")]static extern bool FreeLibrary(IntPtr module);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate IntPtr Init();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int Set(IntPtr state,int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int Finish(IntPtr state);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int Encode(IntPtr state,short[] pcm,int frames,byte[] output,int size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate int Flush(IntPtr state,byte[] output,int size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]delegate UIntPtr Tag(IntPtr state,byte[] output,UIntPtr size);
    readonly byte[] buffer=new byte[65536];IntPtr module,state;FileStream stream;
    Encode encode;Flush flush;Tag tag;Finish close;
    T Function<T>(string name) where T:class { IntPtr address=GetProcAddress(module,name);if(address==IntPtr.Zero)throw new IOException("MP3 编码器接口不完整");return Marshal.GetDelegateForFunctionPointer(address,typeof(T)) as T; }
    void Option(string name,int value) { if(Function<Set>(name)(state,value)<0)throw new IOException("MP3 编码参数无效："+name); }
    public NativeMp3Encoder(string home,string path,int bitrate) {
        if(bitrate!=128&&bitrate!=192&&bitrate!=256&&bitrate!=320)throw new IOException("MP3 码率无效");
        string dll=Path.Combine(home,IntPtr.Size==8?"libmp3lame.64.dll":"libmp3lame.32.dll");MeetingConfig.CheckOrdinaryPath(dll);
        // Restore/build verifies pinned binaries. Dynamic replacement remains
        // possible for compatible LGPL library builds; never change PATH.
        if(!File.Exists(dll))throw new IOException("轻量 MP3 编码组件缺失，请修复安装；原始录音已保留");
        try {
            module=LoadLibraryEx(dll,IntPtr.Zero,8);if(module==IntPtr.Zero)throw new IOException("无法加载 MP3 编码组件，原始录音已保留");
            state=Function<Init>("lame_init")();if(state==IntPtr.Zero)throw new IOException("无法初始化 MP3 编码器");
            close=Function<Finish>("lame_close");
            Option("lame_set_num_channels",2);Option("lame_set_in_samplerate",48000);Option("lame_set_out_samplerate",48000);
            Option("lame_set_brate",bitrate);Option("lame_set_quality",2);Option("lame_set_bWriteVbrTag",1);
            if(Function<Finish>("lame_init_params")(state)<0)throw new IOException("无法初始化 MP3 编码参数");
            encode=Function<Encode>("lame_encode_buffer_interleaved");flush=Function<Flush>("lame_encode_flush");tag=Function<Tag>("lame_get_lametag_frame");
            stream=new FileStream(path,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None);
        } catch { Release();throw; }
    }
    void Bytes(int count) { if(count<0)throw new IOException("MP3 编码失败，原始录音已保留");stream.Write(buffer,0,count); }
    public void Write(short[] pcm,int frames) { Bytes(encode(state,pcm,frames,buffer,buffer.Length)); }
    public void Dispose() {
        try { if(stream!=null) { Bytes(flush(state,buffer,buffer.Length));int count=checked((int)tag(state,buffer,(UIntPtr)buffer.Length).ToUInt64());if(count<=0||count>buffer.Length)throw new IOException("MP3 缺少完整时间信息");stream.Position=0;stream.Write(buffer,0,count);stream.Flush(true); } }
        finally { Release(); }
    }
    void Release() { if(stream!=null){stream.Dispose();stream=null;}if(state!=IntPtr.Zero){if(close!=null)close(state);state=IntPtr.Zero;}if(module!=IntPtr.Zero){FreeLibrary(module);module=IntPtr.Zero;} }
    internal static int Validate(string path,long frames,int bitrate) {
        // Check every frame and the encoder delay/padding, then decode the
        // entire stream through Windows' independent ACM decoder.
        int count=0,delay=0,padding=0;
        int[] rates={0,32,40,48,56,64,80,96,112,128,160,192,224,256,320};
        using(var file=File.OpenRead(path)) {
            while(file.Position<file.Length) {
                long start=file.Position;byte[] h=new byte[4];if(file.Read(h,0,4)!=4)throw new IOException("MP3 尾帧不完整");
                uint v=((uint)h[0]<<24)|((uint)h[1]<<16)|((uint)h[2]<<8)|h[3];int b=(int)((v>>12)&15);
                if((v>>21)!=2047||((v>>19)&3)!=3||((v>>17)&3)!=1||((v>>10)&3)!=1||b<1||b>14||rates[b]!=bitrate)throw new IOException("MP3 音频帧或码率无效");
                int length=144000*bitrate/48000+(int)((v>>9)&1);
                if(start+length>file.Length)throw new IOException("MP3 被截断");
                if(count==0) {
                    byte[] first=new byte[length];file.Position=start;if(file.Read(first,0,length)!=length)throw new IOException("MP3 首帧不完整");
                    int info=((v>>6)&3)==3?21:36;
                    if(System.Text.Encoding.ASCII.GetString(first,info,4)!="Info"||System.Text.Encoding.ASCII.GetString(first,info+120,4)!="LAME")throw new IOException("MP3 缺少时间校验标签");
                    int packed=(first[info+141]<<16)|(first[info+142]<<8)|first[info+143];delay=packed>>12;padding=packed&4095;
                }
                file.Position=start+length;count++;
            }
        }
        if(count<2 || (count-1L)*1152-delay-padding!=frames)throw new IOException("MP3 解码时间线与录音长度不符");
        using(var decoded=new Mp3FileReader(path)) {
            byte[] block=new byte[65536];long bytes=0;int got;
            while((got=decoded.Read(block,0,block.Length))>0)bytes+=got;
            if(decoded.WaveFormat.SampleRate!=48000||decoded.WaveFormat.Channels!=2||bytes<frames*4||bytes>(frames+4608)*4)throw new IOException("MP3 无法完整解码");
        }
        return count;
    }
}
