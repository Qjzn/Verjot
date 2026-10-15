using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

// Real production JSON entry points, isolated files only. No COM, capture or UI.
internal static class TestJsonReadSharing {
    static int assertions;
    static string root;
    static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
    static readonly List<object> results = new List<object>();
    static readonly MethodInfo catalogRead = typeof(SessionCatalog).GetMethod("ReadJson", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly MethodInfo catalogWrite = typeof(SessionCatalog).GetMethod("WriteJson", BindingFlags.Instance | BindingFlags.NonPublic);
    static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    static Dictionary<string, object> Snapshot(int revision) {
        return new Dictionary<string, object> { { "revision", revision }, { "payload", new string('x', 2048) }, { "tail", revision } };
    }
    static Dictionary<string, object> CatalogRead(SessionCatalog catalog, string path, bool optional) {
        try { return (Dictionary<string, object>)catalogRead.Invoke(catalog, new object[] { path, optional }); }
        catch (TargetInvocationException error) { throw error.InnerException; }
    }
    static void CatalogWrite(SessionCatalog catalog, string path, Dictionary<string, object> value) {
        try { catalogWrite.Invoke(catalog, new object[] { path, value }); }
        catch (TargetInvocationException error) { throw error.InnerException; }
    }
    static void Contract(string label, Func<string, Dictionary<string, object>> read, Action<string, Dictionary<string, object>> write) {
        string path = Path.Combine(root, label + "-contract.json"); write(path, Snapshot(7));
        byte[] original = File.ReadAllBytes(path);
        var first = read(path); first["revision"] = -1;
        Check(Convert.ToInt32(read(path)["revision"]) == 7, label + ": each read must return a new immutable file snapshot");
        Check(Convert.ToBase64String(File.ReadAllBytes(path)) == Convert.ToBase64String(original), label + ": reading never changes the JSON bytes");
        var locked = new ManualResetEvent(false);
        var release = new Thread(delegate() {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                locked.Set(); Thread.Sleep(25);
            }
        });
        release.Start(); locked.WaitOne();
        Dictionary<string, object> recovered = null; Exception transientError = null;
        var watch = Stopwatch.StartNew();
        try { recovered = read(path); } catch (Exception error) { transientError = error; }
        watch.Stop(); release.Join(); locked.Dispose();
        Check(transientError == null && recovered != null && Convert.ToInt32(recovered["revision"]) == 7,
            label + ": transient lock must recover through bounded production retries; got " + transientError);
        long transientMs = watch.ElapsedMilliseconds;
        bool persistentFailed = false; watch.Restart();
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            try { read(path); } catch (IOException) { persistentFailed = true; }
        }
        watch.Stop();
        Check(persistentFailed && watch.ElapsedMilliseconds < 500, label + ": persistent lock must keep bounded IOException failure semantics");
        long persistentMs = watch.ElapsedMilliseconds;
        string missing = Path.Combine(root, label + "-missing.json"); watch.Restart();
        for (int i = 0; i < 20; i++) Check(read(missing).Count == 0, label + ": genuinely missing optional file stays empty");
        watch.Stop(); long missingMs = watch.ElapsedMilliseconds;
        File.WriteAllText(path, "{broken-json", Utf8);
        bool invalidFailed = false;
        try { read(path); } catch (ArgumentException) { invalidFailed = true; }
        Check(invalidFailed, label + ": JSON parse errors are not swallowed or replaced by empty metadata");
        write(path, Snapshot(8));
        var disappearing = new ManualResetEvent(false);
        var remover = new Thread(delegate() {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete)) {
                disappearing.Set(); Thread.Sleep(10); File.Delete(path); Thread.Sleep(15);
            }
        });
        remover.Start(); disappearing.WaitOne();
        bool disappearanceFailed = false;
        try { read(path); } catch (IOException) { disappearanceFailed = true; }
        remover.Join(); disappearing.Dispose();
        Check(disappearanceFailed, label + ": a known existing file disappearing during retries is not returned as empty metadata");
        results.Add(new { kind = "contract", source = label, transient_lock_ms = transientMs,
            persistent_lock_ms = persistentMs, missing_reads = 20, missing_reads_ms = missingMs });
    }
    static void Race(string label, int round, Func<string, Dictionary<string, object>> read, Action<string, Dictionary<string, object>> write) {
        string path = Path.Combine(root, label + "-race-" + round + ".json"); write(path, Snapshot(0));
        int reads = 0, readErrors = 0, writes = 0, writeErrors = 0, invalidSnapshots = 0;
        string firstError = ""; var gate = new object();
        var start = new ManualResetEvent(false); var done = new ManualResetEvent(false); var watch = Stopwatch.StartNew();
        var writer = new Thread(delegate() {
            start.WaitOne();
            for (int revision = 1; revision <= 100 && watch.ElapsedMilliseconds < 5000; revision++) {
                try { write(path, Snapshot(revision)); writes++; }
                catch (Exception error) { writeErrors++; lock (gate) if (firstError.Length == 0) firstError = error.ToString(); }
            }
            done.Set();
        });
        var reader = new Thread(delegate() {
            start.WaitOne();
            while ((!done.WaitOne(0) || reads < 250) && watch.ElapsedMilliseconds < 5000) {
                try {
                    var value = read(path); reads++;
                    if (!value.ContainsKey("revision") || !value.ContainsKey("tail") || !value.ContainsKey("payload") ||
                        Convert.ToInt32(value["revision"]) != Convert.ToInt32(value["tail"]) || ((string)value["payload"]).Length != 2048)
                        invalidSnapshots++;
                } catch (Exception error) { readErrors++; lock (gate) if (firstError.Length == 0) firstError = error.ToString(); }
            }
        });
        writer.Start(); reader.Start(); start.Set(); writer.Join(); reader.Join(); watch.Stop();
        start.Dispose(); done.Dispose();
        results.Add(new { kind = "race", source = label, round = round, elapsed_ms = watch.ElapsedMilliseconds,
            reads = reads, read_errors = readErrors, writes = writes, write_errors = writeErrors, invalid_snapshots = invalidSnapshots, first_error = firstError });
        Check(writes == 100 && reads >= 250, label + ": bounded race must complete both sides");
        Check(readErrors == 0 && writeErrors == 0 && invalidSnapshots == 0,
            label + ": atomic replacement must produce complete snapshots without read/write errors or empty states; got " + firstError + " empty=" + invalidSnapshots);
    }
    public static int Main(string[] args) {
        try {
            root = Path.Combine(Path.GetFullPath(args[0]), "json-read-sharing-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "work"));
            var catalog = new SessionCatalog(root, root);
            Func<string, Dictionary<string, object>> meetingReader = MeetingFiles.ReadJson;
            Action<string, Dictionary<string, object>> meetingWriter = delegate(string path, Dictionary<string, object> value) { MeetingFiles.WriteJson(path, value); };
            Func<string, Dictionary<string, object>> catalogReader = delegate(string path) { return CatalogRead(catalog, path, true); };
            Action<string, Dictionary<string, object>> catalogWriter = delegate(string path, Dictionary<string, object> value) { CatalogWrite(catalog, path, value); };
            Contract("MeetingFiles", meetingReader, meetingWriter);
            Contract("SessionCatalog", catalogReader, catalogWriter);
            bool requiredMissingFailed = false;
            try { CatalogRead(catalog, Path.Combine(root, "required-missing.json"), false); }
            catch (FileNotFoundException) { requiredMissingFailed = true; }
            Check(requiredMissingFailed, "SessionCatalog: required missing metadata remains a clear failure after bounded retries");
            string nullJson = Path.Combine(root, "required-null.json"); File.WriteAllText(nullJson, "null", Utf8);
            bool nullFailed = false;
            try { CatalogRead(catalog, nullJson, false); } catch (IOException) { nullFailed = true; }
            Check(nullFailed, "SessionCatalog: null top-level JSON is not converted to empty metadata");
            for (int round = 1; round <= 1; round++) {
                Race("MeetingFiles", round, meetingReader, meetingWriter);
                Race("SessionCatalog", round, catalogReader, catalogWriter);
            }
            string report = new JavaScriptSerializer().Serialize(new { assertions = assertions, passed = true, results = results });
            File.WriteAllText(Path.Combine(root, "report.json"), report, Utf8);
            Console.WriteLine(report); Console.WriteLine("PASS: " + assertions + " assertions; no audio, device, model or UI operations; " + root); return 0;
        } catch (Exception error) {
            if (root != null) File.WriteAllText(Path.Combine(root, "failure.json"), new JavaScriptSerializer().Serialize(new { error = error.ToString(), results = results }), Utf8);
            Console.Error.WriteLine(error); return 1;
        }
    }
}
