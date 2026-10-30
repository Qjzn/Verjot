using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NAudio.Wave;

internal static class TestNativeApiTranscription {
    static int assertions;
    static void Check(bool value,string message) { assertions++;if(!value)throw new Exception(message); }
    static void Reject(Action action,string message) { bool rejected=false;try{action();}catch(IOException){rejected=true;}Check(rejected,message); }
    sealed class Server : IDisposable {
        readonly TcpListener listener=new TcpListener(IPAddress.Loopback,0);
        readonly Thread thread;
        public readonly string Url;
        public string Headers,Body;
        public Exception Error;
        public Server(string content,int status=200) {
            listener.Start();Url="http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port+"/v1";
            thread=new Thread(delegate() {
                try {
                    using(var client=listener.AcceptTcpClient())using(var stream=client.GetStream()) {
                        stream.ReadTimeout=15000;var head=new StringBuilder();int next;
                        while((next=stream.ReadByte())>=0) {head.Append((char)next);if(head.ToString().EndsWith("\r\n\r\n"))break;}
                        Headers=head.ToString();long count=0;
                        foreach(string line in Headers.Split('\n'))if(line.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase))count=long.Parse(line.Substring(15).Trim());
                        if(Headers.IndexOf("Expect: 100-continue",StringComparison.OrdinalIgnoreCase)>=0) {var interim=Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");stream.Write(interim,0,interim.Length);}
                        using(var body=new MemoryStream()) {
                            byte[] block=new byte[8192];while(count>0) {int n=stream.Read(block,0,(int)Math.Min(block.Length,count));if(n==0)throw new IOException("Incomplete request");body.Write(block,0,n);count-=n;}Body=Encoding.UTF8.GetString(body.ToArray());
                        }
                        byte[] bytes=Encoding.UTF8.GetBytes(content);
                        byte[] response=Encoding.ASCII.GetBytes("HTTP/1.1 "+status+" Test\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: "+bytes.Length+"\r\n"+(status==302?"Location: http://127.0.0.1:1/never-follow\r\n":"")+"\r\n");
                        stream.Write(response,0,response.Length);stream.Write(bytes,0,bytes.Length);
                    }
                }catch(Exception error){Error=error;}
            });thread.IsBackground=true;thread.Start();
        }
        public void Dispose() {listener.Stop();thread.Join(15000);if(Error!=null)throw Error;}
    }
    static Dictionary<string,object> Fixture(string id,out string session,bool legacy=false) {
        string vault=Path.Combine(MeetingFiles.Home,"vault");Directory.CreateDirectory(vault);
        var config=MeetingConfig.Validate(new Dictionary<string,object>{{"vault_root",vault},{"meeting_folder","Verlog"},{"open_in_obsidian",false},
            {"transcription_mode","api"},{"api_key_env","VERLOG_TEST_ONLY_API_KEY"}},false);
        session=Path.Combine(MeetingFiles.Home,"work",id);Directory.CreateDirectory(session);
        if(legacy)config["meeting_folder"]="verbit";
        string name=legacy?"verbit202610271430":MeetingArtifacts.AvailableName(config,new DateTimeOffset(2026,10,27,14,30,0,TimeSpan.FromHours(8)));
        using(var writer=new WaveFileWriter(Path.Combine(session,"mic.wav"),WaveFormat.CreateIeeeFloatWaveFormat(16000,1)))
            for(int i=0;i<100000;i++)writer.WriteSample((float)(.1*Math.Sin(2*Math.PI*440*i/16000)));
        MeetingFiles.WriteJson(Path.Combine(session,"session.json"),new Dictionary<string,object>{{"state","recorded"},{"start_time","2026-10-27T14:30:00+08:00"},
            {"session_dir",session},{"duration_seconds",6.25},{"storage_mode","minimal"},{"audio_format","mp3"},{"audio_export_version",1},
            {"output_layout",legacy?"verbit-v1":"Verlog-v1"},{"output_basename",name},{"transcription_mode","api"},
            {"audio_output_dir",Path.Combine(vault,(string)config["meeting_folder"],name)},{"sources",new object[]{new Dictionary<string,object>{{"source","mic"},{"path","mic.wav"},{"kind","microphone"}}}}});
        NativeAudioExport.Export(MeetingFiles.Home,session,config);return config;
    }
    static void Bind(string session,Dictionary<string,object> config) {
        MeetingFiles.WriteJson(Path.Combine(session,"transcription-settings.json"),new {transcription_mode="api",transcription_model=config["api_model"],
            transcription_api_base_url=config["api_base_url"],api_upload_confirmed=true});
    }
    [STAThread] public static int Main(string[] args) {
        if(args.Length==2&&args[0]=="--transcribe-api")return NativeApiTranscription.Run(args[1]);
        if(args.Length==2&&args[0]=="--export-audio")return NativeAudioExport.Run(args[1]);
        try {
            Environment.SetEnvironmentVariable("VERLOG_TEST_ONLY_API_KEY","synthetic-test-key");
            Check((string)NativeApiTranscription.ParseResponse("{\"text\":\"中文测试\"}")["text"]=="中文测试","Plain JSON text is preserved");
            Reject(delegate{NativeApiTranscription.ParseResponse("{\"text\":\"\"}");},"Empty text cannot masquerade as a completed transcription");
            Reject(delegate{NativeApiTranscription.ParseResponse("{\"segments\":[{\"start\":2,\"end\":1,\"text\":\"bad\"}]}");},"Invalid provider timeline is rejected");
            string session;var config=Fixture("20261027-143001",out session);
            var metadata=MeetingFiles.ReadJson(Path.Combine(session,"session.json"));
            string output=MeetingArtifacts.AudioPath(config,session,metadata);
            Check(output.EndsWith("Verlog202610271430\\Verlog202610271430.mp3"),"New public folder and MP3 follow Verlog+year-month-day-hour-minute");
            Check(MeetingArtifacts.AvailableName(config,new DateTimeOffset(2026,10,27,14,30,59,TimeSpan.FromHours(8)))=="Verlog202610271430-02","Same-minute recordings receive a readable collision suffix");
            string pending=Path.Combine(MeetingFiles.Home,"work","20261027-143059");Directory.CreateDirectory(pending);
            MeetingFiles.WriteJson(Path.Combine(pending,"session.json"),new {output_layout="Verlog-v1",output_basename="Verlog202610271430-02",audio_output_dir=Path.Combine((string)config["vault_root"],"Verlog","Verlog202610271430-02")});
            Check(MeetingArtifacts.AvailableName(config,new DateTimeOffset(2026,10,27,14,30,59,TimeSpan.FromHours(8)))=="Verlog202610271430-03","A recording still awaiting export reserves its public name before the directory exists");
            string original=NativeAudioExport.Hash(output);
            MeetingFiles.WriteJson(Path.Combine(MeetingFiles.Home,"config.json"),config);
            using(var child=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                FileName=System.Windows.Forms.Application.ExecutablePath, Arguments="--export-audio \""+session+"\"",UseShellExecute=false,CreateNoWindow=true }))
                Check(child.WaitForExit(20000)&&child.ExitCode==0,"Recording-only export and cleanup run in an actual child without API configuration or Python");
            Check(!File.Exists(Path.Combine(session,"mic.wav"))&&NativeAudioExport.Hash(output)==original,"Verified recording-only MP3 can release temporary lossless storage");
            Check(new SessionCatalog(MeetingFiles.Home,(string)config["vault_root"]).Get(session).CanRetry,"API transcription remains available after recording-only raw cleanup");
            using(var server=new Server("{\"text\":\"今天检查录音。\",\"segments\":[{\"start\":0.1,\"end\":1.2,\"speaker\":\"A\",\"text\":\"今天检查录音。\"}]}")) {
                config["api_base_url"]=server.Url;config["api_model"]="custom-third-party-asr";Bind(session,config);
                MeetingFiles.WriteJson(Path.Combine(MeetingFiles.Home,"config.json"),config);
                using(var child=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                    FileName=System.Windows.Forms.Application.ExecutablePath, Arguments="--transcribe-api \""+session+"\"",UseShellExecute=false,CreateNoWindow=true })) {
                    new SessionCatalog(MeetingFiles.Home,(string)config["vault_root"]).SaveWorker(session,child);
                    Check(child.WaitForExit(20000)&&child.ExitCode==0,"Actual native worker process accepts its own saved identity and finishes without Python");
                }
                Check(server.Headers.StartsWith("POST /v1/audio/transcriptions HTTP/1.1"),"Actual loopback multipart request uses the audio transcription endpoint");
                Check(server.Body.Contains("custom-third-party-asr")&&server.Body.Contains("filename=\"recording.mp3\""),"Custom model ID is submitted without exposing the local filename");
            }
            string note=Path.ChangeExtension(output,".md"),content=File.ReadAllText(note);
            Check(content.Contains("今天检查录音。")&&content.Contains("说话人 A")&&content.Contains(Path.GetFileName(output)),"Provider text, provided identity and the sole recording link survive publishing");
            Check(Directory.GetFiles(Path.GetDirectoryName(output)).Length==2,"Only one MP3 and one Markdown are publicly visible after success");
            Check(!File.ReadAllText(Path.Combine(session,"result.json")).Contains("synthetic-test-key"),"Checkpoint never persists the API key");
            DateTime stamp=File.GetLastWriteTimeUtc(note);NativeApiTranscription.Process(session,config);
            Check(File.GetLastWriteTimeUtc(note)==stamp,"Retry resumes a verified checkpoint without a second upload or rewriting the note");
            config["api_model"]="different-model";Reject(delegate{NativeApiTranscription.Process(session,config);},"Changed settings cannot reuse a previous upload authorization");
            string failed;var failedConfig=Fixture("20261027-143002",out failed);
            using(var server=new Server("PROVIDER-SECRET-MUST-NOT-LEAK",413)) {
                failedConfig["api_base_url"]=server.Url;failedConfig["api_model"]="file-limit-test";Bind(failed,failedConfig);
                Reject(delegate{NativeApiTranscription.Process(failed,failedConfig);},"Provider file size errors retain recovery audio");
            }
            string failure=File.ReadAllText(Path.Combine(failed,"status.json"));
            Check(failure.Contains("413")&&!failure.Contains("PROVIDER-SECRET")&&File.Exists(Path.Combine(failed,"mic.wav")),"Failure explains HTTP code without leaking response body or deleting raw tracks");
            using(var server=new Server("{}",302)) {
                failedConfig["api_base_url"]=server.Url;Bind(failed,failedConfig);
                Reject(delegate{NativeApiTranscription.Process(failed,failedConfig);},"Redirects cannot forward credentials or recordings");
            }
            var failedMetadata=MeetingFiles.ReadJson(Path.Combine(failed,"session.json"));
            byte[] raw=File.ReadAllBytes(Path.Combine(failed,"mic.wav"));raw[raw.Length-1]^=1;File.WriteAllBytes(Path.Combine(failed,"mic.wav"),raw);
            Reject(delegate{MeetingArtifacts.CleanupRaw(failedConfig,failed,failedMetadata);},"Modified raw tracks are never silently discarded");
            Check(File.Exists(Path.Combine(failed,"mic.wav")),"Cleanup rejection preserves modified recovery audio");
            string wrongOutput=MeetingArtifacts.AudioPath(failedConfig,failed,failedMetadata);File.AppendAllText(wrongOutput,"modified");
            Reject(delegate{MeetingArtifacts.VerifiedAudio(failedConfig,failed,failedMetadata);},"Modified MP3 cannot be submitted or authorize raw cleanup");
            Check(!Directory.Exists(Path.Combine(MeetingFiles.Home,".venv"))&&!Directory.Exists(Path.Combine(MeetingFiles.Home,"models")),"All native API scenarios run without Python or a model");
            string legacySession;var legacyConfig=Fixture("20261027-legacy",out legacySession,true);
            var legacyMetadata=MeetingFiles.ReadJson(Path.Combine(legacySession,"session.json"));
            string legacyOutput=MeetingArtifacts.AudioPath(legacyConfig,legacySession,legacyMetadata);
            Check(legacyOutput.EndsWith("verbit202610271430\\verbit202610271430.mp3"),"Existing meeting filenames and output folder remain unchanged after rename");
            Check(MeetingArtifacts.VerifiedAudio(legacyConfig,legacySession,legacyMetadata)!=null,"Legacy MP3 export credentials remain valid");
            using(var server=new Server("{\"text\":\"旧会议继续转写。\"}")) {
                legacyConfig["api_base_url"]=server.Url;legacyConfig["api_model"]="legacy-compatible-asr";Bind(legacySession,legacyConfig);
                NativeApiTranscription.Process(legacySession,legacyConfig);
            }
            Check(File.ReadAllText(Path.ChangeExtension(legacyOutput,".md")).Contains("verbit202610271430.mp3"),"Legacy transcription keeps its original relative audio reference");
            Check(!File.Exists(Path.Combine(legacySession,"mic.wav")),"Legacy successful processing still safely cleans its verified temporary track");
            Console.WriteLine("PASS native API: "+assertions+" assertions; loopback synthetic audio only; no external upload");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
