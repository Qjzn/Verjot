using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Net;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

// Model-free export runs in an independent process. No Python, models, sound
// playback, system settings, clipboard or network is used by the exporter.
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
        string output=MeetingArtifacts.OutputDirectory(config,session,metadata);
        if(!string.Equals(Path.GetFullPath(MeetingFiles.Text(metadata,"audio_output_dir","")),Path.GetFullPath(output),StringComparison.OrdinalIgnoreCase))throw new IOException("录音保存位置已改变，请恢复录制时保存位置后重试");
        output=Path.Combine(output,MeetingArtifacts.AudioFilename(metadata));MeetingConfig.CheckOrdinaryPath(output);
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
                    {"path",MeetingArtifacts.AudioFilename(metadata)},{"gain",gain},{"mixed_peak_before_gain",peak},{"encoder","native_lame_3.100_v1"}};
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
        try {
            var config=MeetingConfig.Load(false);
            var audio=Export(MeetingFiles.Home,session,config);
            var metadata=MeetingFiles.ReadJson(Path.Combine(session,"session.json"));
            if(MeetingArtifacts.IsNew(metadata) && MeetingFiles.Text(metadata,"transcription_mode","")=="api" &&
                !MeetingFiles.Text(metadata,"auto_transcribe","False").Equals("True",StringComparison.OrdinalIgnoreCase)) {
                // Hashing/cleanup of long raw recordings stays in this worker,
                // never on the tray/quick-note UI thread.
                string cleanupError=null;
                try { MeetingArtifacts.CleanupRaw(config,session,metadata); }
                catch {cleanupError="临时音轨清理未完成，恢复资料已保留；请检查磁盘权限和文件占用";}
                WriteStatus(session,"MP3 已保存 · 仅录音","audio_ready","recording_only",audio);
                var status=MeetingFiles.ReadJson(Path.Combine(session,"status.json"));
                if(cleanupError!=null)status["cleanup_error"]=cleanupError;else status.Remove("cleanup_error");
                MeetingFiles.WriteJson(Path.Combine(session,"status.json"),status);
            }
            return 0;
        }
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

// Explicit metadata opts new meetings into the public Verlog folder layout.
// Old meetings retain their original filenames, paths and lossless workflow.
internal static class MeetingArtifacts {
    internal static bool IsNew(Dictionary<string,object> metadata) {
        string layout=MeetingFiles.Text(metadata,"output_layout","");
        return layout=="Verlog-v1"||layout=="verbit-v1";
    }
    internal static string Basename(Dictionary<string,object> metadata) {
        string name=MeetingFiles.Text(metadata,"output_basename","");
        string prefix=MeetingFiles.Text(metadata,"output_layout","")=="verbit-v1"?"verbit":"Verlog";
        if(!Regex.IsMatch(name,@"\A"+prefix+@"[0-9]{12}(?:-[0-9]{2,})?\z"))throw new IOException("会议文件名无效，未修改文件");
        return name;
    }
    internal static string AvailableName(Dictionary<string,object> config,DateTimeOffset started) {
        string root=Path.Combine(MeetingFiles.VaultRoot(config),MeetingFiles.Text(config,"meeting_folder","会议记录"));
        MeetingConfig.CheckOrdinaryPath(root);
        string stem="Verlog"+started.ToString("yyyyMMddHHmm",CultureInfo.InvariantCulture);
        var reserved=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string work=Path.Combine(MeetingFiles.Home,"work");MeetingConfig.CheckOrdinaryPath(work);
        if(Directory.Exists(work))foreach(string folder in Directory.GetDirectories(work)) {
            string file=Path.Combine(folder,"session.json");MeetingConfig.CheckOrdinaryPath(file);
            if(!File.Exists(file))continue;
            var pending=MeetingFiles.ReadJson(file);
            if(!IsNew(pending))continue;
            string name=Basename(pending);
            if(string.Equals(MeetingFiles.Text(pending,"audio_output_dir",""),Path.Combine(root,name),StringComparison.OrdinalIgnoreCase))reserved.Add(name);
        }
        for(int index=1;index<int.MaxValue;index++) {
            string name=stem+(index==1?"":"-"+index.ToString("D2",CultureInfo.InvariantCulture));
            string path=Path.Combine(root,name);MeetingConfig.CheckOrdinaryPath(path);
            if(!reserved.Contains(name)&&!Directory.Exists(path)&&!File.Exists(path))return name;
        }
        throw new IOException("同一分钟会议过多，请稍后重试");
    }
    internal static string OutputDirectory(Dictionary<string,object> config,string session,Dictionary<string,object> metadata) {
        string root=Path.Combine(MeetingFiles.VaultRoot(config),MeetingFiles.Text(config,"meeting_folder","会议记录"));
        string path=IsNew(metadata)?Path.Combine(root,Basename(metadata)):Path.Combine(root,"附件",Path.GetFileName(session));
        MeetingConfig.CheckOrdinaryPath(path);return Path.GetFullPath(path);
    }
    internal static string AudioFilename(Dictionary<string,object> metadata) { return (IsNew(metadata)?Basename(metadata):"meeting")+".mp3"; }
    internal static string AudioPath(Dictionary<string,object> config,string session,Dictionary<string,object> metadata) {
        string directory=OutputDirectory(config,session,metadata);
        if(!string.Equals(directory,Path.GetFullPath(MeetingFiles.Text(metadata,"audio_output_dir","")),StringComparison.OrdinalIgnoreCase))
            throw new IOException("录音保存目录已变化，请恢复原保存目录后重试");
        return Path.Combine(directory,AudioFilename(metadata));
    }
    internal static Dictionary<string,object> VerifiedAudio(Dictionary<string,object> config,string session,Dictionary<string,object> metadata) {
        string path=AudioPath(config,session,metadata),manifest=Path.Combine(session,"native_audio.json");
        MeetingConfig.CheckOrdinaryPath(path);MeetingConfig.CheckOrdinaryPath(manifest);
        var saved=MeetingFiles.ReadJson(manifest);object value;
        var audio=saved.TryGetValue("merged_audio",out value)?value as Dictionary<string,object>:null;
        if(audio==null||MeetingFiles.Text(audio,"published_path","")!=path||
            MeetingFiles.Text(saved,"metadata_sha256","")!=NativeAudioExport.Hash(Path.Combine(session,"session.json"))||
            !File.Exists(path)||MeetingFiles.Text(audio,"output_sha256","")!=NativeAudioExport.Hash(path))
            throw new IOException("录音校验不一致，已保留恢复资料；不会上传或清理");
        return audio;
    }
    internal static bool HasPublishedAudio(string session,Dictionary<string,object> metadata) {
        if(!IsNew(metadata)||MeetingFiles.Text(metadata,"transcription_mode","")!="api")return false;
        string directory=MeetingFiles.Text(metadata,"audio_output_dir","");
        if(!Path.IsPathRooted(directory))return false;
        string path=Path.Combine(directory,AudioFilename(metadata));MeetingConfig.CheckOrdinaryPath(path);
        return File.Exists(path)&&File.Exists(Path.Combine(session,"native_audio.json"));
    }
    internal static void CleanupRaw(Dictionary<string,object> config,string session,Dictionary<string,object> metadata,string note=null) {
        if(!IsNew(metadata)||MeetingFiles.Text(metadata,"state","")!="recorded"||!string.IsNullOrWhiteSpace(MeetingFiles.Text(metadata,"capture_error","")))return;
        // The catalog guards ownership and every declared source path first.
        if(new SessionCatalog(MeetingFiles.Home,MeetingFiles.VaultRoot(config)).Get(session)==null)throw new IOException("会议不属于当前程序");
        VerifiedAudio(config,session,metadata);
        var manifest=MeetingFiles.ReadJson(Path.Combine(session,"native_audio.json"));
        var expected=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(Dictionary<string,object> input in (IList)manifest["inputs"])expected[(string)input["name"]]=(string)input["sha256"];
        string noteHash=null;
        if(note!=null) { MeetingConfig.CheckOrdinaryPath(note);noteHash=NativeAudioExport.Hash(note); }
        var files=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var sources=metadata["sources"] as IList;
        if(sources==null||sources.Count==0)throw new IOException("录音来源缺失，不清理原始文件");
        foreach(Dictionary<string,object> source in sources) {
            object segments;var entries=source.TryGetValue("segments",out segments)?segments as IList:null;
            var names=new List<string>();
            if(entries!=null)foreach(Dictionary<string,object> entry in entries)names.Add(MeetingFiles.Text(entry,"path",""));
            else names.Add(MeetingFiles.Text(source,"path",""));
            foreach(string name in names) {
                if(!Regex.IsMatch(name,@"\A(?:mic|system)(?:_[0-9]{2,})?(?:\.seg[0-9]{6})?\.wav\z"))throw new IOException("清理文件名无效");
                string file=Path.Combine(session,name);MeetingConfig.CheckOrdinaryPath(file);
                if(File.Exists(file)) {
                    string digest=NativeAudioExport.Hash(file),original;
                    if(!expected.TryGetValue(name,out original)||digest!=original)throw new IOException("无损录音与导出凭据不一致，已停止清理");
                    files[file]=digest;
                }
            }
        }
        // Verify every candidate before the first deletion. Never scan a parent
        // directory, recursively delete, or treat unrelated files as disposable.
        VerifiedAudio(config,session,metadata);
        if(note!=null&&NativeAudioExport.Hash(note)!=noteHash)throw new IOException("转录笔记发生变化，已停止清理");
        foreach(var file in files)if(NativeAudioExport.Hash(file.Key)!=file.Value)throw new IOException("无损录音发生变化，已停止清理");
        foreach(var file in files) { MeetingConfig.CheckOrdinaryPath(file.Key);File.Delete(file.Key); }
    }
}

// The optional native API path sends one verified MP3 to the configured
// OpenAI-compatible /audio/transcriptions endpoint. No Python or model needed.
internal static class NativeApiTranscription {
    internal static Dictionary<string,object> ParseResponse(string content) {
        Dictionary<string,object> result;
        try { result=MeetingFiles.Json.Deserialize<Dictionary<string,object>>(content); }
        catch { throw new IOException("服务未返回有效 JSON；需支持 /audio/transcriptions 音频转写接口"); }
        if(result==null)throw new IOException("服务返回空结果，录音已保留");
        string text=MeetingFiles.Text(result,"text","").Trim();
        object value;var segments=result.TryGetValue("segments",out value)?value as IList:null;
        var rows=new List<object>();double last=0;
        if(segments!=null)foreach(object item in segments) {
            var row=item as Dictionary<string,object>;if(row==null)throw new IOException("服务转写分段格式无效");
            double start,end;
            if(!double.TryParse(MeetingFiles.Text(row,"start",""),NumberStyles.Float,CultureInfo.InvariantCulture,out start)||
                !double.TryParse(MeetingFiles.Text(row,"end",""),NumberStyles.Float,CultureInfo.InvariantCulture,out end)||
                double.IsNaN(start)||double.IsNaN(end)||double.IsInfinity(start)||double.IsInfinity(end)||start<last||end<start)
                throw new IOException("服务返回无效时间轴，录音与结果保留供检查");
            last=start;string speech=MeetingFiles.Text(row,"text","").Trim();if(speech.Length==0)continue;
            string speaker=MeetingFiles.Text(row,"speaker",MeetingFiles.Text(row,"speaker_id",""));
            rows.Add(new Dictionary<string,object>{{"start",start},{"end",end},{"text",speech},{"speaker",speaker}});
        }
        if(text.Length==0&&rows.Count==0)throw new IOException("服务未返回可用文字，录音已保留；请核对音量或模型");
        return new Dictionary<string,object>{{"text",text},{"segments",rows},{"complete",true},{"native_api_version",1}};
    }
    static void Bytes(Stream stream,string value) { byte[] bytes=Encoding.UTF8.GetBytes(value);stream.Write(bytes,0,bytes.Length); }
    internal static Dictionary<string,object> Request(string audio,Dictionary<string,object> config) {
        string key=MeetingConfig.ApiKeyValue((string)config["api_key_env"]);
        if(string.IsNullOrEmpty(key)||Regex.IsMatch(key,@"[^\x21-\x7e]"))throw new IOException("API 密钥缺失或格式无效，请检查所配置的环境变量");
        string boundary="Verlog"+Guid.NewGuid().ToString("N");
        var request=(HttpWebRequest)WebRequest.Create(((string)config["api_base_url"]).TrimEnd('/')+"/audio/transcriptions");
        request.Method="POST";request.AllowAutoRedirect=false;request.KeepAlive=false;
        request.Timeout=request.ReadWriteTimeout=Convert.ToInt32(config["api_timeout_seconds"])*1000;
        request.ContentType="multipart/form-data; boundary="+boundary;request.Headers[HttpRequestHeader.Authorization]="Bearer "+key;
        request.AllowWriteStreamBuffering=false;request.SendChunked=false;
        string prompt=(MeetingFiles.Text(config,"asr_context","")+"\n"+MeetingFiles.Text(config,"asr_terms","")).Trim();
        string fields="--"+boundary+"\r\nContent-Disposition: form-data; name=\"model\"\r\n\r\n"+config["api_model"]+
            "\r\n--"+boundary+"\r\nContent-Disposition: form-data; name=\"response_format\"\r\n\r\njson";
        if(prompt.Length>0)fields+="\r\n--"+boundary+"\r\nContent-Disposition: form-data; name=\"prompt\"\r\n\r\n"+prompt;
        fields+="\r\n--"+boundary+"\r\nContent-Disposition: form-data; name=\"file\"; filename=\"recording.mp3\"\r\nContent-Type: audio/mpeg\r\n\r\n";
        string tail="\r\n--"+boundary+"--\r\n";
        using(var file=new FileStream(audio,FileMode.Open,FileAccess.Read,FileShare.Read)) {
            request.ContentLength=Encoding.UTF8.GetByteCount(fields)+file.Length+Encoding.UTF8.GetByteCount(tail);
            try {
                using(var upload=request.GetRequestStream()) { Bytes(upload,fields);file.CopyTo(upload,65536);Bytes(upload,tail); }
                using(var response=(HttpWebResponse)request.GetResponse()) {
                    if((int)response.StatusCode<200||(int)response.StatusCode>=300)throw new IOException("API 未成功响应（HTTP "+(int)response.StatusCode+"），未跟随重定向，录音已保留");
                    using(var body=response.GetResponseStream())using(var bytes=new MemoryStream()) {
                        byte[] buffer=new byte[8192];int count;
                        while((count=body.Read(buffer,0,buffer.Length))>0) { if(bytes.Length+count>4*1024*1024)throw new IOException("API 结果过大，已停止读取并保留录音");bytes.Write(buffer,0,count); }
                        return ParseResponse(new UTF8Encoding(false,true).GetString(bytes.ToArray()));
                    }
                }
            }catch(WebException error) {
                using(var response=error.Response as HttpWebResponse) {
                    if(response!=null)throw new IOException("音频转写 API 返回 HTTP "+(int)response.StatusCode+"；请检查接口、权限、模型和服务商文件大小限制。录音已保留");
                    throw new IOException(error.Status==WebExceptionStatus.Timeout?"API 转写超时，录音已保留；可提高超时后重试（服务商可能已计费）":"无法连接音频转写 API，录音已保留；请检查网络和服务地址");
                }
            }
        }
    }
    static void Status(string session,string state,string message,Dictionary<string,object> audio=null,string note=null,string cleanupError=null) {
        var value=MeetingFiles.ReadJson(Path.Combine(session,"status.json"));
        value["state"]=state;value["message"]=message;value["stage"]="api";value["updated_at"]=DateTimeOffset.Now.ToString("o");value.Remove("error");
        if(state=="error")value["error"]=message;
        if(audio!=null) { value["merged_audio"]=audio;value["published_audio_path"]=audio["published_path"]; }
        if(note!=null) {value["note_path"]=note;value["result_complete"]=true;}
        if(cleanupError!=null)value["cleanup_error"]=cleanupError;else value.Remove("cleanup_error");
        string path=Path.Combine(session,"status.json");MeetingConfig.CheckOrdinaryPath(path);MeetingConfig.CheckOrdinaryPath(path+".tmp");MeetingFiles.WriteJson(path,value);
    }
    internal static int Run(string session) {
        try {
            var config=MeetingConfig.Load();
            session=Path.GetFullPath(session);
            var record=new SessionCatalog(MeetingFiles.Home,MeetingFiles.VaultRoot(config)).Get(session);
            if(record==null || (record.WorkerAlive && record.WorkerPid!=System.Diagnostics.Process.GetCurrentProcess().Id))throw new IOException("会议正由其他进程处理，未上传音频");
            if((string)config["transcription_mode"]!="api")throw new IOException("未选择 API 转写，未上传音频");
            string processing=Path.Combine(MeetingFiles.Home,"processing.lock");MeetingConfig.CheckOrdinaryPath(processing);
            using(var gate=new FileStream(processing,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.ReadWrite)) {
                while(true) {
                    try { gate.Lock(0,1);break; }
                    catch(IOException) {Status(session,"queued","MP3 已保存，等待前一场会议转写完成");System.Threading.Thread.Sleep(2000);}
                }
                try { Process(session,config); }finally {gate.Unlock(0,1);}
            }
            return 0;
        }catch(Exception) {
            // Transport response bodies and credentials never enter logs/status.
            // Process exposes only explicitly safe public messages below.
            try { var catalog=new SessionCatalog(MeetingFiles.Home,"");if(catalog.Get(session)!=null && MeetingFiles.Text(MeetingFiles.ReadJson(Path.Combine(session,"status.json")),"state","")!="error")Status(session,"error","API 转写未完成，录音与恢复资料已保留；请检查设置并重试"); }catch { }
            return 1;
        }
    }
    internal static void Process(string session,Dictionary<string,object> config) {
        try { ProcessCore(session,config); }
        catch(IOException error) { Status(session,"error",error.Message);throw; }
    }
    static void ProcessCore(string session,Dictionary<string,object> config) {
        string metaPath=Path.Combine(session,"session.json");var metadata=MeetingFiles.ReadJson(metaPath);
        string metaHash=NativeAudioExport.Hash(metaPath);
        string captureState=MeetingFiles.Text(metadata,"state","");
        if(captureState!="recorded"&&captureState!="recorded_with_error")throw new IOException("录音尚未正常结束，未上传音频");
        if(!MeetingArtifacts.IsNew(metadata))throw new IOException("旧会议继续使用原转写流程，未上传或修改原文件");
        var binding=MeetingFiles.ReadJson(Path.Combine(session,"transcription-settings.json"));
        if(MeetingFiles.Text(binding,"transcription_mode","")!="api"||MeetingFiles.Text(binding,"transcription_model","")!=(string)config["api_model"]||
            MeetingFiles.Text(binding,"transcription_api_base_url","")!=((string)config["api_base_url"]).TrimEnd('/')||
            !MeetingFiles.Text(binding,"api_upload_confirmed","").Equals("True",StringComparison.OrdinalIgnoreCase))
            throw new IOException("API 设置与已确认的提交不一致，请从最近会议重新确认；未上传音频");
        var audio=MeetingArtifacts.VerifiedAudio(config,session,metadata);string path=(string)audio["published_path"];
        string name=MeetingFiles.Text(metadata,"note_filename",MeetingArtifacts.Basename(metadata)+".md");
        if(MeetingRename.Filename(name)!=name)throw new IOException("笔记文件名无效，未上传音频");
        string note=Path.Combine(Path.GetDirectoryName(path),name);MeetingConfig.CheckOrdinaryPath(note);
        string checkpoint=Path.Combine(session,"result.json");MeetingConfig.CheckOrdinaryPath(checkpoint);
        string identity=MeetingFiles.Json.Serialize(new object[]{audio["output_sha256"],config["api_base_url"],config["api_model"],config["asr_terms"],config["asr_context"]});
        Dictionary<string,object> result;
        if(File.Exists(checkpoint)) {
            result=MeetingFiles.ReadJson(checkpoint);
            if(MeetingFiles.Text(result,"native_api_version","")!="1"||MeetingFiles.Text(result,"request_identity","")!=identity)
                throw new IOException("已有转写结果属于其他配置，未覆盖、未重复上传；请恢复原设置");
        }else {
            if(File.Exists(note)||Directory.Exists(note))throw new IOException("已有同名笔记，未覆盖、未上传；请检查保存位置");
            Status(session,"processing","正在提交音频转写 API，MP3 已保存在本机");
            result=Request(path,config);result["request_identity"]=identity;
            MeetingArtifacts.VerifiedAudio(config,session,metadata);
            MeetingConfig.CheckOrdinaryPath(checkpoint+".tmp");MeetingFiles.WriteJson(checkpoint,result);
        }
        if(!MeetingFiles.Text(result,"complete","").Equals("True",StringComparison.OrdinalIgnoreCase))throw new IOException("转写结果不完整，录音已保留");
        var content=new StringBuilder();content.Append("[会议录音](").Append(Uri.EscapeDataString(Path.GetFileName(path))).Append(")\n\n");
        object marks;if(metadata.TryGetValue("bookmarks",out marks)&&marks is IList)foreach(Dictionary<string,object> mark in (IList)marks) {
            double time=Convert.ToDouble(mark["time_seconds"],CultureInfo.InvariantCulture);MeetingPlaybackRequest.CheckSeconds(time);
            content.Append("- 重点 ").Append(TimeSpan.FromSeconds(time).ToString()).Append('\n');
        }
        var rows=result["segments"] as IList;
        if(rows!=null&&rows.Count>0)foreach(Dictionary<string,object> row in rows) {
            double start=Convert.ToDouble(row["start"],CultureInfo.InvariantCulture),end=Convert.ToDouble(row["end"],CultureInfo.InvariantCulture);
            if(end>Convert.ToDouble(audio["duration_seconds"],CultureInfo.InvariantCulture)+1)throw new IOException("服务返回的时间超出录音长度，结果留在恢复资料中，未发布或清理");
            string speaker=MeetingFiles.Text(row,"speaker","");
            content.Append('\n').Append(TimeSpan.FromSeconds(start).ToString()).Append(speaker.Length>0?" · 说话人 "+speaker:"").Append("\n\n").Append(MeetingFiles.Text(row,"text","")).Append('\n');
        }else content.Append(MeetingFiles.Text(result,"text","")).Append('\n');
        if(MeetingFiles.Text(metadata,"state","")=="recorded_with_error")content.Append("\n采集存在异常，请核对原录音。\n");
        string text=content.ToString(),temporary=note+"."+Guid.NewGuid().ToString("N")+".tmp";
        MeetingArtifacts.VerifiedAudio(config,session,metadata);
        if(NativeAudioExport.Hash(metaPath)!=metaHash)throw new IOException("会议资料发生变化，已停止发布并保留结果");
        if(File.Exists(note)) { if(File.ReadAllText(note,Encoding.UTF8)!=text)throw new IOException("笔记已修改，未覆盖；API 结果已保留"); }
        else {
            try { using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {byte[] bytes=Encoding.UTF8.GetBytes(text);stream.Write(bytes,0,bytes.Length);stream.Flush(true);} File.Move(temporary,note); }
            finally { if(File.Exists(temporary))File.Delete(temporary); }
        }
        Status(session,"completed","录音与转录文件已保存",audio,note);
        try { MeetingArtifacts.CleanupRaw(config,session,metadata,note); }
        catch { Status(session,"completed","录音与转录已保存；临时资料清理未完成",audio,note,"恢复资料已保留，请检查保存磁盘和文件占用"); }
    }
}
