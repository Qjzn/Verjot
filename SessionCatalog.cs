using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

// Only controller metadata is written here. Recorded audio and notes are never opened.
internal sealed class SessionRecord {
    public string DirectoryPath, Label, State, Message, NotePath, AudioPath, Fingerprint;
    public bool CanRetry, NeedsAttention, WorkerAlive;
    public int WorkerPid;
    public DateTime WorkerStartUtc;

    public Process TryOpenWorker() {
        if (WorkerPid <= 0 || WorkerStartUtc == DateTime.MinValue) return null;
        Process process = null;
        try {
            process = Process.GetProcessById(WorkerPid);
            if (!process.HasExited && process.StartTime.ToUniversalTime() == WorkerStartUtc.ToUniversalTime()) return process;
        } catch (ArgumentException) { } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        if (process != null) process.Dispose();
        return null;
    }
}

internal sealed class SessionCatalog {
    readonly string home, work, artifactRoot;
    readonly JavaScriptSerializer json = new JavaScriptSerializer();
    readonly Action<int> retryDelay;
    // A recently read/written file can be briefly absent during replacement.
    // Keep that observation across calls without slowing never-present files.
    // The small instance cache owns no handles and never locks around file IO.
    const int RecentPathCapacity = 128;
    static readonly long RecentPathLifetime = Stopwatch.Frequency * 5;
    readonly object recentPathGate = new object();
    readonly Dictionary<string, long> recentPaths = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

    public SessionCatalog(string home, string artifactRoot) : this(home, artifactRoot, System.Threading.Thread.Sleep) { }

    internal SessionCatalog(string home, string artifactRoot, Action<int> retryDelay) {
        if (retryDelay == null) throw new ArgumentNullException("retryDelay");
        this.retryDelay = retryDelay;
        this.home = Path.GetFullPath(home);
        work = Path.Combine(this.home, "work");
        this.artifactRoot = string.IsNullOrWhiteSpace(artifactRoot) ? "" : Path.GetFullPath(artifactRoot);
        CheckOrdinary(this.home);
        CheckOrdinary(work);
        if (this.artifactRoot.Length != 0) CheckOrdinary(this.artifactRoot);
    }

    public List<SessionRecord> Snapshot() {
        var result = new List<SessionRecord>();
        CheckOrdinary(work);
        if (!Directory.Exists(work)) return result;
        var legacy = ReadLegacy();
        foreach (string folder in Directory.GetDirectories(work)) {
            // Non-meeting scratch folders do not appear in the tray. Reparse points
            // are checked before looking inside, so a junction is never traversed.
            try {
                string session = CheckSession(folder);
                if (!File.Exists(Path.Combine(session, "session.json"))) continue;
                var record = ReadRecord(session, legacy);
                if (record.State != "removed") result.Add(record);
            } catch (Exception error) {
                result.Add(InvalidRecord(folder, error));
            }
        }
        result.Sort(delegate(SessionRecord left, SessionRecord right) {
            return StringComparer.OrdinalIgnoreCase.Compare(right.DirectoryPath, left.DirectoryPath);
        });
        return result;
    }

    public SessionRecord Get(string session) {
        string path = CheckSession(session);
        try { return ReadRecord(path, ReadLegacy()); }
        catch (Exception error) { return InvalidRecord(path, error); }
    }

    public void SaveWorker(string session, Process process) {
        session = CheckSession(session);
        string controllerPath = Path.Combine(session, "controller.json");
        var controller = ReadJson(controllerPath, true);
        controller["schema_version"] = 1;
        controller["worker_pid"] = process == null ? 0 : process.Id;
        string started = process == null ? "" : process.StartTime.ToUniversalTime().ToString("o");
        controller["worker_start_time_utc"] = started;
        controller["launch_time_utc"] = started;
        // A new attempt must be able to report its own final outcome.
        controller.Remove("notified_fingerprint");
        WriteJson(controllerPath, controller);
    }

    public void MarkNotified(SessionRecord record) {
        if (record == null || string.IsNullOrEmpty(record.Fingerprint)) return;
        string session = CheckSession(record.DirectoryPath);
        string path = Path.Combine(session, "controller.json");
        var controller = ReadJson(path, true);
        controller["schema_version"] = 1;
        // Preserve a legacy last-session identity when adopting its notification.
        // Otherwise the newly created controller would hide that identity and
        // change the final fingerprint again on the next launch.
        int controllerPid;
        if (record.WorkerPid > 0 && (!int.TryParse(Text(controller, "worker_pid", "0"), out controllerPid) || controllerPid <= 0)) {
            controller["worker_pid"] = record.WorkerPid;
            controller["worker_start_time_utc"] = record.WorkerStartUtc.ToString("o");
            controller["launch_time_utc"] = record.WorkerStartUtc.ToString("o");
        }
        controller["notified_fingerprint"] = record.Fingerprint;
        WriteJson(path, controller);
    }

    SessionRecord ReadRecord(string session, Dictionary<string, object> legacy) {
        var metadata = ReadJson(Path.Combine(session, "session.json"), false);
        string supplied = Text(metadata, "session_dir", "");
        if (supplied.Length != 0 && !Same(CheckSession(supplied), session))
            throw new IOException("会议元数据指向其他目录");
        string controllerPath = Path.Combine(session, "controller.json");
        bool hasController = File.Exists(controllerPath);
        var controller = ReadJson(controllerPath, true);
        var identity = controller;
        // Older releases only saved the last meeting's worker identity.
        // A preflight controller with PID 0 can also use the fallback identity
        // saved after the worker started but its controller update failed.
        int controllerPid;
        bool hasIdentity = int.TryParse(Text(controller, "worker_pid", "0"), out controllerPid) && controllerPid > 0;
        if ((!hasController || !hasIdentity) && Same(Text(legacy, "session_dir", ""), session)) identity = legacy;
        var record = new SessionRecord {
            DirectoryPath = session, Label = Label(session, metadata), NotePath = "", AudioPath = "",
            Message = "", WorkerStartUtc = DateTime.MinValue
        };
        int pid;
        DateTime started;
        if (int.TryParse(Text(identity, "worker_pid", "0"), out pid) && pid > 0 &&
            DateTime.TryParse(Text(identity, "worker_start_time_utc", ""), CultureInfo.InvariantCulture,
                              DateTimeStyles.RoundtripKind, out started)) {
            record.WorkerPid = pid;
            record.WorkerStartUtc = started.ToUniversalTime();
            using (var process = record.TryOpenWorker()) record.WorkerAlive = process != null;
        }
        string statusPath = Path.Combine(session, "status.json");
        CheckOrdinary(statusPath);
        Dictionary<string, object> status;
        try { status = ReadJson(statusPath, true); }
        catch {
            if (!record.WorkerAlive) throw;
            // A live identity proves the job is still running even if a status
            // update is temporarily unreadable. Never offer a duplicate retry.
            status = new Dictionary<string, object>();
        }
        record.Message = Text(status, "message", "");
        string metadataState = Text(metadata, "state", "");
        string statusState = Text(status, "state", Text(status, "status", ""));
        bool fresh = record.WorkerPid <= 0 || (File.Exists(statusPath) && File.GetLastWriteTimeUtc(statusPath) >= record.WorkerStartUtc);
        if (record.WorkerAlive) {
            record.State = fresh && statusState == "queued" ? "queued" : "processing";
            if (!fresh || record.Message.Length == 0) record.Message = "录音已保存，后台转写中";
        } else if (metadataState == "discarded") {
            record.State = "discarded";
            record.Message = "短录音已取消，临时资料清理未完成；不会转录";
        } else if (metadataState == "recording") {
            record.State = "interrupted";
            record.Message = "上次录音中断，发现保留资料；请先检查，不能保证音频完整";
        } else if (fresh && (statusState == "completed" || statusState == "done" || statusState == "success")) {
            record.NotePath = ArtifactPath(Text(status, "note_path", ""), session, ".md");
            string audio = Text(status, "published_audio_path", Text(status, "audio_path", ""));
            object mergedValue;
            if (audio.Length == 0 && status.TryGetValue("merged_audio", out mergedValue)) {
                var merged = mergedValue as Dictionary<string, object>;
                if (merged != null) {
                    audio = Text(merged, "published_path", "");
                    if (audio.Length == 0) {
                        audio = Text(merged, "path", "");
                        // Older minimal-storage releases kept the published
                        // folder in session metadata and only the WAV basename
                        // here. Resolve that explicit pair, never guess a folder
                        // or fall back from an invalid published path.
                        if (audio.Length != 0 && !Path.IsPathRooted(audio)) {
                            string output = Text(metadata, "audio_output_dir", "");
                            if (audio != Path.GetFileName(audio) || !Path.IsPathRooted(output))
                                throw new IOException("旧会议录音路径缺少明确的保存目录或文件名无效");
                            audio = Path.Combine(output, audio);
                        }
                    }
                }
            }
            record.AudioPath = ArtifactPath(audio, session, ".wav", ".mp3");
            bool declaredArtifacts = record.NotePath.Length != 0 && record.AudioPath.Length != 0;
            bool noteExists = File.Exists(record.NotePath), audioExists = File.Exists(record.AudioPath);
            bool verifiedStatus = Text(status, "merge_error", "").Length == 0 && Text(status, "artifact_error", "").Length == 0;
            bool hasArtifacts = declaredArtifacts && noteExists && audioExists && verifiedStatus;
            bool captureNeedsReview = metadataState == "recorded_with_error" ||
                !string.IsNullOrWhiteSpace(Text(metadata, "capture_error", "")) ||
                !string.IsNullOrWhiteSpace(Text(status, "capture_error", "")) ||
                string.Equals(Text(status, "capture_needs_review", ""), "true", StringComparison.OrdinalIgnoreCase);
            // Completed files can be independently removed by the user. Keep a
            // neutral entry for a remaining file, hide fully cleared results,
            // and retain warnings for errors or an unavailable save directory.
            bool removableArtifacts = declaredArtifacts && verifiedStatus &&
                Text(status, "cleanup_error", "").Length == 0 &&
                !captureNeedsReview &&
                !Directory.Exists(record.NotePath) && !Directory.Exists(record.AudioPath) &&
                (Within(record.NotePath, session) || Directory.Exists(artifactRoot)) &&
                (Within(record.AudioPath, session) || Directory.Exists(artifactRoot));
            record.State = captureNeedsReview ? "capture_warning" : hasArtifacts ? "completed" : !removableArtifacts ? "missing" :
                noteExists ? "transcript_only" : audioExists ? "audio_only" : "removed";
            if (record.State == "capture_warning") record.Message = "采集异常，请检查录音与转录；不能保证录音完整";
            else if (record.State == "removed") record.Message = "会议成品已移除";
            else if (record.State == "transcript_only") record.Message = "仅保留转录，录音文件已移除";
            else if (record.State == "audio_only") record.Message = "仅保留录音，转录文件已移除";
            else if (!hasArtifacts) record.Message = "会议录音或转录成品需要检查；保留资料不会自动重新转写";
            else if (record.Message.Length == 0) record.Message = "录音和转录笔记已保存";
            if (hasArtifacts && Text(status, "cleanup_error", "").Length != 0)
                record.Message += "；临时资料清理未完成，已保留";
            if (!captureNeedsReview && noteExists && !audioExists &&
                    string.Equals(Text(status, "result_complete", ""), "true", StringComparison.OrdinalIgnoreCase) &&
                    Text(status, "artifact_error", "").Length == 0 && Text(status, "merge_error", "").Length != 0 &&
                    HasOriginalAudio(session, metadata)) {
                record.State = "failed";
                record.CanRetry = true;
                record.Message = "转录已保存，录音生成失败；可重试生成成品";
            }
        } else if (fresh && (statusState == "failed" || statusState == "error")) {
            record.State = "failed";
            record.CanRetry = true;
            if (record.Message.Length == 0) record.Message = "转写失败，保留资料可重试";
        } else if (record.WorkerPid > 0) {
            record.State = "failed";
            record.CanRetry = true;
            record.Message = "转写进程已退出，未发现本次完成结果；保留资料可重试";
        } else if (metadataState == "recorded" || metadataState == "recorded_with_error") {
            record.State = "retry";
            record.CanRetry = true;
            record.Message = "录音已保存，转写未启动或已中断；可重试对应会议";
        } else {
            throw new IOException("会议状态无法识别，请检查保留资料");
        }
        if (record.CanRetry) {
            record.NotePath = ArtifactPath(Text(status, "note_path", ""), session, ".md");
            record.CanRetry = HasOriginalAudio(session, metadata);
            if (!record.CanRetry) record.Message = "未发现可重试的原始录音，请检查保留资料；不会自动重新转写";
        }
        record.Fingerprint = Fingerprint(record, status);
        record.NeedsAttention = !record.WorkerAlive && record.State != "removed" &&
            record.State != "transcript_only" && record.State != "audio_only" &&
            Text(controller, "notified_fingerprint", "") != record.Fingerprint;
        return record;
    }

    public static string DisplayTitle(Dictionary<string, object> status) {
        string state = Text(status, "state", Text(status, "status", ""));
        if (state == "queued") return "排队中";
        if (state == "completed" || state == "done" || state == "success") return "转写完成";
        if (state == "error" || state == "failed") return "转写失败";
        string stage = Text(status, "stage", "");
        if (stage == "merge") return "合并录音";
        if (stage == "encode") return "压缩录音";
        if (stage == "prepare" || stage == "speakers" || stage == "diarization") return "分析说话人";
        if (stage == "asr") {
            int done, total;
            if (int.TryParse(Text(status, "completed", ""), out done) && done >= 0 &&
                int.TryParse(Text(status, "total", ""), out total) && total > 0 && done <= total)
                return "转写 " + done.ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture) + " 段";
            return "转写中";
        }
        return "录音已保存 · 处理中";
    }

    string ArtifactPath(string path, string session, params string[] extensions) {
        if (string.IsNullOrWhiteSpace(path)) return "";
        if (!Path.IsPathRooted(path)) throw new IOException("会议成品路径必须为完整路径");
        path = Path.GetFullPath(path);
        bool allowed = false;
        foreach (string extension in extensions)
            if (string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase)) allowed = true;
        if (!allowed) throw new IOException("会议成品文件类型错误");
        // A configured vault may include work, but another meeting's private files
        // must still never be treated as this meeting's output.
        if (Within(path, work) && !Within(path, session)) throw new IOException("会议成品指向其他会议资料");
        if (!Within(path, session) && (artifactRoot.Length == 0 || !Within(path, artifactRoot)))
            throw new IOException("会议成品不在保存目录内");
        CheckOrdinary(path);
        return path;
    }

    string CheckSession(string path) {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new IOException("会议路径必须为完整路径");
        path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Same(Path.GetDirectoryName(path), work)) throw new IOException("会议路径不在 work 直属目录内");
        CheckOrdinary(path);
        if (!Directory.Exists(path)) throw new IOException("会议资料目录不存在");
        return path;
    }

    static bool HasOriginalAudio(string session, Dictionary<string, object> metadata) {
        object declared;
        if (metadata.TryGetValue("sources", out declared)) {
            var sources = declared as System.Collections.IList;
            if (sources == null || sources.Count == 0) throw new IOException("会议录音来源清单无效");
            var names = new HashSet<string>(StringComparer.Ordinal);
            bool complete = true;
            foreach (object item in sources) {
                var source = item as Dictionary<string, object>;
                if (source == null) throw new IOException("会议录音来源必须为对象");
                string name = Text(source, "source", "");
                if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"\A(?:mic|system)(?:_[0-9]{2,})?\z") || !names.Add(name))
                    throw new IOException("会议录音来源名称无效或重复");
                string kind = name == "mic" || name.StartsWith("mic_", StringComparison.Ordinal) ? "microphone" : "loopback";
                if (Text(source, "kind", "") != kind || Text(source, "path", "") != name + ".wav")
                    throw new IOException("会议录音来源类型或路径无效");
                object declaredSegments;
                if (source.TryGetValue("segments", out declaredSegments)) {
                    var segments = declaredSegments as System.Collections.IList;
                    if (segments == null || segments.Count == 0) throw new IOException("会议无损分段清单无效");
                    long nextFrame = 0;
                    for (int index = 0; index < segments.Count; index++) {
                        var segment = segments[index] as Dictionary<string, object>;
                        object first, count;
                        string expected = name + ".seg" + (index + 1).ToString("D6", CultureInfo.InvariantCulture) + ".wav";
                        if (segment == null || Text(segment, "path", "") != expected ||
                            !segment.TryGetValue("start_frame", out first) || !(first is int || first is long) ||
                            !segment.TryGetValue("frames", out count) || !(count is int || count is long) ||
                            Convert.ToInt64(first) != nextFrame || Convert.ToInt64(count) < 0)
                            throw new IOException("会议无损分段路径或连续帧坐标无效");
                        nextFrame = checked(nextFrame + Convert.ToInt64(count));
                        complete = OriginalExists(session, expected) && complete;
                    }
                } else complete = OriginalExists(session, name + ".wav") && complete;
            }
            return complete;
        }
        bool found = false;
        foreach (string name in new[] { "mic.wav", "system.wav" }) {
            string path = Path.Combine(session, name);
            CheckOrdinary(path);
            if (File.Exists(path)) found = true;
        }
        return found;
    }

    static bool OriginalExists(string session, string filename) {
        string path = Path.Combine(session, filename);
        CheckOrdinary(path);
        if (Directory.Exists(path)) throw new IOException("会议录音来源不能为目录");
        return File.Exists(path);
    }

    Dictionary<string, object> ReadLegacy() {
        try {
            var legacy = ReadJson(Path.Combine(home, "last-session.json"), true);
            string path = Text(legacy, "session_dir", "");
            if (path.Length != 0) CheckSession(path);
            return legacy;
        } catch { return new Dictionary<string, object>(); }
    }

    bool RecentlySucceeded(string path) {
        string fullPath = Path.GetFullPath(path); long now = Stopwatch.GetTimestamp(), stamp;
        lock (recentPathGate) {
            if (!recentPaths.TryGetValue(fullPath, out stamp)) return false;
            if (now - stamp <= RecentPathLifetime) return true;
            recentPaths.Remove(fullPath); return false;
        }
    }
    void RememberSuccess(string path) {
        string fullPath = Path.GetFullPath(path); long now = Stopwatch.GetTimestamp();
        lock (recentPathGate) {
            if (!recentPaths.ContainsKey(fullPath) && recentPaths.Count >= RecentPathCapacity) {
                string oldest = null; long oldestStamp = Int64.MaxValue;
                foreach (var item in recentPaths) if (item.Value < oldestStamp) { oldest = item.Key; oldestStamp = item.Value; }
                if (oldest != null) recentPaths.Remove(oldest);
            }
            recentPaths[fullPath] = now;
        }
    }
    Dictionary<string, object> ReadJson(string path, bool optional) {
        bool observed = File.Exists(path) || RecentlySucceeded(path);
        var retryClock = Stopwatch.StartNew();
        string snapshot;
        for (int attempt = 0; ; attempt++) {
            try {
                CheckOrdinary(path);
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8)) snapshot = reader.ReadToEnd();
                break;
            } catch (IOException error) {
                int code = error.HResult & 0xffff;
                bool sharing = code == 32 || code == 33;
                if (sharing) observed = true;
                bool missing = error is FileNotFoundException || error is DirectoryNotFoundException;
                // A never-observed optional file is absent, rather than an in-progress replacement.
                if (optional && !observed && missing) return new Dictionary<string, object>();
                bool replacing = observed && missing;
                // Keep an atomic-replace sharing conflict recoverable beyond
                // three scheduler ticks, including an already-observed path
                // briefly disappearing. Permanent conflicts still fail.
                if (attempt >= 3 && ((!sharing && !replacing) || retryClock.ElapsedMilliseconds >= 200)) {
                    throw;
                }
                retryDelay(10);
            } catch (UnauthorizedAccessException error) {
                // Include CheckOrdinary's attribute read in the bounded retry:
                // a delete-pending Windows file can deny both attributes/open.
                // Permanent permission failures still throw their original type.
                if ((error.HResult & 0xffff) != 5 || attempt >= 3) throw;
                observed = true;
                retryDelay(10);
            }
        }
        var data = json.Deserialize<Dictionary<string, object>>(snapshot);
        if (data == null) throw new IOException("会议状态 JSON 顶层必须为对象");
        RememberSuccess(path);
        return data;
    }

    void WriteJson(string path, Dictionary<string, object> value) {
        CheckOrdinary(path);
        string temporary = path + ".tmp";
        CheckOrdinary(temporary);
        File.WriteAllText(temporary, json.Serialize(value), new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
        RememberSuccess(path);
    }

    static void CheckOrdinary(string path) {
        string cursor = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(cursor)) {
            try {
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("会议路径包含链接或重解析点，已拒绝访问");
            } catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
            string parent = Path.GetDirectoryName(cursor);
            if (Same(parent, cursor)) break;
            cursor = parent;
        }
    }

    static bool Within(string path, string root) {
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
    static bool Same(string left, string right) {
        return string.Equals((left ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            (right ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }
    static string Text(Dictionary<string, object> data, string key, string fallback) {
        object value;
        return data != null && data.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : fallback;
    }
    static string Label(string session, Dictionary<string, object> metadata) {
        DateTimeOffset time;
        string label = Path.GetFileName(session);
        if (DateTimeOffset.TryParse(Text(metadata, "start_time", ""), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out time))
            label = time.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var title = new StringBuilder();
        foreach (char character in Text(metadata, "title", "")) {
            if (char.GetUnicodeCategory(character) == UnicodeCategory.Format) continue;
            title.Append(char.IsControl(character) ? ' ' : character);
            if (title.Length == 64) break;
        }
        string safeTitle = title.ToString().Trim();
        return safeTitle.Length == 0 ? label : label + " · " + safeTitle;
    }
    static string Fingerprint(SessionRecord record, Dictionary<string, object> status) {
        string text = record.State + "\n" + Text(status, "updated_at", "") + "\n" + record.NotePath + "\n" + record.AudioPath +
            "\n" + record.WorkerPid.ToString(CultureInfo.InvariantCulture) + "\n" + record.WorkerStartUtc.ToString("o") + "\n" + record.Message;
        using (var hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(text)));
    }
    static SessionRecord InvalidRecord(string session, Exception error) {
        return new SessionRecord { DirectoryPath = session, Label = Path.GetFileName(session), State = "invalid",
            Message = "这场会议的状态或路径需要检查；其他会议不受影响", NotePath = "", AudioPath = "",
            CanRetry = false, NeedsAttention = true, Fingerprint = "" };
    }
}
