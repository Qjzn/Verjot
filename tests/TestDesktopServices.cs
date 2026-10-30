using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;

internal static class TestDesktopServices {
    static void LocalReadiness() {
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "engine-receipt-fixture"); Directory.CreateDirectory(root);
        Check(!LocalEngineState.Read(root).Ready, "A new installation is not marked ready");
        string[] files = { ".venv/Scripts/python.exe", "check_local_engine.py", "asr_engine.py", "prepare_audio.py", "assets/engine-check.wav",
            "models/Qwen3-ASR-1.7B-hf/config.json", "models/Qwen3-ASR-1.7B-hf/model.safetensors", "models/Qwen3-ASR-1.7B-hf/processor_config.json",
            "models/Qwen3-ASR-1.7B-hf/tokenizer_config.json", "models/Qwen3-ASR-1.7B-hf/tokenizer.json", "models/3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx",
            ".venv/Lib/site-packages/torch/__init__.py", ".venv/Lib/site-packages/transformers/__init__.py", ".venv/Lib/site-packages/sherpa_onnx/__init__.py", "models/segmentation/model.onnx" };
        var manifest = new System.Collections.Generic.List<object>();
        foreach (string name in files) {
            string path = Path.Combine(root, name); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, "synthetic file metadata only");
            var info = new FileInfo(path); manifest.Add(new { path = name, size = info.Length, mtime_ns = (info.LastWriteTimeUtc.Ticks - 621355968000000000L) * 100 });
        }
        Directory.CreateDirectory(Path.Combine(root, "installation"));
        string receipt = Path.Combine(root, "installation", "local-ready.json");
        MeetingFiles.WriteJson(receipt, new { version = 1, state = "ready", files = manifest });
        Check(LocalEngineState.Read(root).Ready, "A matching smoke-test receipt is reused without loading a model");
        string setupLock = Path.Combine(root, "recommended-model.lock");
        using (var handle = new FileStream(setupLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Check(LocalEngineState.Read(root).Busy && !LocalEngineState.Read(root).Ready, "Active setup overrides a prior ready receipt");
        MeetingFiles.WriteJson(Path.Combine(root, "installation", "recommended-model.json"), new { state = "verifying" });
        Check(!LocalEngineState.Read(root).Ready && LocalEngineState.Read(root).Action == "继续准备", "Interrupted verification is recoverable and never ready");
        MeetingFiles.WriteJson(Path.Combine(root, "installation", "recommended-model.json"), new { state = "completed" });
        File.AppendAllText(Path.Combine(root, files[6]), "changed");
        Check(!LocalEngineState.Read(root).Ready, "Changed model files invalidate readiness");
        File.WriteAllText(receipt, "{broken");
        Check(!LocalEngineState.Read(root).Ready, "A damaged receipt never enables local transcription");
    }
    static int count;
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
    static void Reject(Action action, string message) { bool rejected = false; try { action(); } catch (InvalidDataException) { rejected = true; } Check(rejected, message); }
    [STAThread] static int Main() {
        try {
            foreach (int dpi in new[] { 96, 120, 144, 192, 240 })
                foreach (var work in new[] { new System.Drawing.Rectangle(0, 0, 1024, 560), new System.Drawing.Rectangle(0, 0, 1920, 1040), new System.Drawing.Rectangle(-1280, -100, 1280, 680) }) {
                    var desired = new System.Drawing.Rectangle(work.Right - 50, work.Bottom - 50, 600 * dpi / 96, 700 * dpi / 96);
                    var fit = DesktopServices.FitWindowBounds(desired, work);
                    Check(work.Contains(fit) && fit.Width > 0 && fit.Height > 0, "Dialog fits small/offset work areas at " + dpi + " DPI");
                    Check(DesktopServices.FitWindowBounds(fit, work) == fit, "Work-area clamping is stable");
                }
            string setupRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "model-setup-gate"); Directory.CreateDirectory(setupRoot);
            using (DesktopServices.EnterModelSetupGate(setupRoot)) {
                bool blocked = false; try { using (DesktopServices.EnterModelSetupGate(setupRoot)) { } } catch (IOException) { blocked = true; }
                Check(blocked, "A model preparation gate blocks capture/another preparation without touching audio");
            }
            using (DesktopServices.EnterModelSetupGate(setupRoot)) { Check(true, "Gate releases after setup and is reusable"); }
            Check(typeof(DesktopServices).GetMethod("RecommendedModelStartInfo") == null && typeof(DesktopServices).GetMethod("PrepareLocalTranscription") == null,
                "The desktop no longer exposes a launcher for recommended model downloads");
            Check(DesktopServices.Newer("v0.1.0-beta.10", "0.1.0-beta.4"), "Numeric prerelease order");
            Check(!DesktopServices.Newer("v0.1.0-beta.2", "0.1.0-beta.4"), "No prerelease downgrade");
            Check(DesktopServices.Newer("v0.1.0", "0.1.0-rc.3"), "Stable follows RC");
            Check(!DesktopServices.Newer("v0.1.0-rc.5", "0.1.0"), "Stable never downgrades to RC");
            Check(DesktopServices.Newer("v0.2.0-alpha.1", "0.1.0"), "Higher minor version");
            Check(!DesktopServices.Newer("v0.1.0-beta.4", "0.1.0-beta.4"), "Same version");
            Check(DesktopServices.ReleaseSummary("[{\"tag_name\":\"v0.2.0-beta.1\",\"draft\":false}]", "0.1.0").Contains("没有更高"), "Stable installs must not be offered prereleases");
            Check(DesktopServices.ReleaseSummary("[{\"tag_name\":\"v0.2.0\",\"draft\":false,\"prerelease\":true}]", "0.1.0").Contains("没有更高"), "GitHub prerelease flag is honored even with a stable-looking tag");
            Reject(delegate { DesktopServices.Newer("https://untrusted.invalid", "0.1.0"); }, "Unrecognized version is not trusted");
            string release = "[{\"tag_name\":\"v0.1.0-beta.10\",\"draft\":false},{\"tag_name\":\"v99.0.0\",\"draft\":true}]";
            Check(DesktopServices.ReleaseSummary(release, "0.1.0-beta.4").Contains("beta.10"), "Draft excluded; beta checked without latest endpoint");
            Check(DesktopServices.ReleaseSummary("[]", "0.1.0-beta.4-dev").Contains("没有更高"), "No releases and dev builds supported");
            Reject(delegate { DesktopServices.ReleaseSummary("{}", "0.1.0"); }, "Malformed metadata rejected");
            LocalReadiness();
            System.Windows.Forms.Application.EnableVisualStyles();
            string document = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "synthetic-changelog.md");
            File.WriteAllText(document, "# 版本说明\r\n中文离线说明，无需文件关联。", new System.Text.UTF8Encoding(false));
            DesktopServices.Open(document);
            var viewer = System.Windows.Forms.Application.OpenForms[0];
            var documentText = (System.Windows.Forms.TextBox)viewer.Controls["document_text"];
            Check(documentText.ReadOnly && documentText.Text == File.ReadAllText(document), "Actual offline-document entry opens a read-only native reader without external association");
            Check(documentText.SelectionLength == 0, "Offline reader initially shows unselected readable text");
            using (var bitmap = new System.Drawing.Bitmap(viewer.Width, viewer.Height)) {
                viewer.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, viewer.Width, viewer.Height));
                bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "offline-reader.png"));
            }
            viewer.Close(); viewer.Dispose();
            string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "synthetic-startup");
            Directory.CreateDirectory(folder); DesktopServices.StartupFolder = folder;
            Check(!DesktopServices.StartsWithWindows, "Fresh isolated startup disabled");
            DesktopServices.SetStartup(true); Check(DesktopServices.StartsWithWindows, "Real COM shortcut enables autostart in fixture");
            DesktopServices.SetStartup(true); Check(Directory.GetFiles(folder).Length == 1, "Autostart is idempotent");
            File.Move(Path.Combine(folder,"Verlog installed.lnk"),Path.Combine(folder,"verbit installed.lnk"));
            Check(DesktopServices.StartsWithWindows,"Existing brand startup setting is recognized");
            DesktopServices.SetStartup(true);
            Check(File.Exists(Path.Combine(folder,"Verlog installed.lnk"))&&!File.Exists(Path.Combine(folder,"verbit installed.lnk")),"Owned legacy shortcut migrates to the current name without duplicate startup");
            DesktopServices.SetStartup(false); Check(!DesktopServices.StartsWithWindows && Directory.GetFiles(folder).Length == 0, "Disable removes owned shortcut");
            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            object link = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { Path.Combine(folder, "Verlog installed.lnk") });
            link.GetType().InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { Path.Combine(folder, "other.exe") });
            link.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            Marshal.FinalReleaseComObject(link); Marshal.FinalReleaseComObject(shell);
            byte[] before = File.ReadAllBytes(Path.Combine(folder, "Verlog installed.lnk"));
            DesktopServices.SetStartup(false); Check(File.Exists(Path.Combine(folder, "Verlog installed.lnk")), "Does not remove another installation's entry");
            bool collision = false; try { DesktopServices.SetStartup(true); } catch (IOException) { collision = true; }
            Check(collision && Convert.ToBase64String(before) == Convert.ToBase64String(File.ReadAllBytes(Path.Combine(folder, "Verlog installed.lnk"))), "Foreign entry is never overwritten");
            Console.WriteLine("PASS TestDesktopServices: " + count + " assertions; no network, user startup, clipboard or audio."); return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
