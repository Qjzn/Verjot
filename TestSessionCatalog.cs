using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

// Isolated controller metadata tests. No real configuration, audio or API is read.
internal static class TestSessionCatalog {
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static string root, home, vault, outside;
    static int passed, skipped;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    static extern bool CreateSymbolicLink(string link, string target, int flags);

    static int Main(string[] args) {
        string fixtureBase = args.Length == 0 ? Path.GetTempPath() : Path.GetFullPath(args[0]);
        root = Path.Combine(fixtureBase, "tingji-catalog-" + Guid.NewGuid().ToString("N"));
        home = Path.Combine(root, "app");
        vault = Path.Combine(root, "notes");
        outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(Path.Combine(home, "work"));
        Directory.CreateDirectory(vault);
        Directory.CreateDirectory(outside);
        try {
            Run();
            Console.WriteLine("PASS: " + passed + " assertions; SKIP: " + skipped + "; no audio recorded, no API called");
            return 0;
        } catch (Exception error) {
            Console.Error.WriteLine("FAIL: " + error);
            return 1;
        } finally {
            // This is the exact GUID-created fixture, never a program/work folder.
            if (Path.GetDirectoryName(root) == fixtureBase.TrimEnd(Path.DirectorySeparatorChar))
                Directory.Delete(root, true);
        }
    }

    static void Run() {
        var catalog = new SessionCatalog(home, vault);
        Equal(catalog.Snapshot().Count, 0, "empty work directory");
        string recording = Session("20261014-090000", "recording");
        var interrupted = catalog.Get(recording);
        Equal(interrupted.State, "interrupted", "unowned recording is interrupted");
        True(!interrupted.CanRetry && interrupted.NeedsAttention, "interrupted only offers inspection");
        catalog.MarkNotified(interrupted);
        True(!new SessionCatalog(home, vault).Get(recording).NeedsAttention, "interrupted notification survives restart");

        string recorded = Session("20261014-100000", "recorded");
        Equal(catalog.Get(recorded).State, "retry", "recorded without worker can retry");
        True(catalog.Get(recorded).CanRetry, "pending metadata is retryable");
        string failed = Session("20261014-110000", "recorded");
        Write(Path.Combine(failed, "status.json"), new { state = "error", message = "fixture failure", updated_at = "attempt-one" });
        var failure = catalog.Get(failed);
        True(failure.State == "failed" && failure.CanRetry, "failed state can retry");
        catalog.MarkNotified(failure);
        True(!catalog.Get(failed).NeedsAttention, "same failure is not repeated");
        Write(Path.Combine(failed, "status.json"), new { state = "error", message = "fixture failure", updated_at = "attempt-two" });
        True(catalog.Get(failed).NeedsAttention, "new failed attempt can notify");
        string noRaw = Session("20261014-111000", "recorded");
        File.Delete(Path.Combine(noRaw, "mic.wav"));
        Write(Path.Combine(noRaw, "status.json"), new { state = "error", message = "fixture failure" });
        True(catalog.Get(noRaw).State == "failed" && !catalog.Get(noRaw).CanRetry,
            "failed meeting without original tracks only offers inspection");
        string numbered = Session("20261014-111100", "recorded");
        File.Delete(Path.Combine(numbered, "mic.wav"));
        string numberedAudio = Path.Combine(numbered, "system_02.wav");
        File.WriteAllText(numberedAudio, "numbered track existence only");
        Write(Path.Combine(numbered, "session.json"), new { state = "recorded", session_dir = numbered,
            sources = new[] { new { source = "system_02", path = "system_02.wav", kind = "loopback" } } });
        True(catalog.Get(numbered).State == "retry" && catalog.Get(numbered).CanRetry,
            "declared numbered-only capture can retry without legacy mic or system tracks");
        Write(Path.Combine(numbered, "session.json"), new { state = "recorded", session_dir = numbered,
            sources = new[] { new { source = "mic_02", path = "mic_02.wav", kind = "microphone" },
                              new { source = "system_02", path = "system_02.wav", kind = "loopback" } } });
        File.WriteAllText(Path.Combine(numbered, "mic.wav"), "unlisted legacy track");
        True(!catalog.Get(numbered).CanRetry, "missing declared track cannot fall back to unlisted legacy mic");
        File.WriteAllText(Path.Combine(numbered, "mic_02.wav"), "numbered microphone existence only");
        True(catalog.Get(numbered).CanRetry, "all declared numbered tracks permit retry");
        File.Delete(numberedAudio);
        True(!catalog.Get(numbered).CanRetry, "one retained declared track cannot hide another missing track");
        Directory.CreateDirectory(numberedAudio);
        Equal(catalog.Get(numbered).State, "invalid", "declared capture replaced by directory is rejected");
        Directory.Delete(numberedAudio);
        foreach (string unsafePath in new[] { @"..\system_02.wav", @"subdir\system_02.wav", "subdir/system_02.wav",
                                             Path.Combine(outside, "system_02.wav"), "system_02.wav:stream" }) {
            Write(Path.Combine(numbered, "session.json"), new { state = "recorded", session_dir = numbered,
                sources = new[] { new { source = "system_02", path = unsafePath, kind = "loopback" } } });
            True(catalog.Get(numbered).State == "invalid" && !catalog.Get(numbered).CanRetry,
                "declared capture path cannot escape its exact source basename: " + unsafePath);
        }
        foreach (object invalidSources in new object[] { null, new object[0], "system_02.wav",
            new { source = "system_02", path = "system_02.wav", kind = "loopback" },
            new[] { new { source = "system_02", path = "system_02.wav", kind = "microphone" } },
            new[] { new { source = "system_2", path = "system_2.wav", kind = "loopback" } },
            new[] { new { source = "system_02", path = "system_02.wav", kind = "loopback" },
                    new { source = "system_02", path = "system_02.wav", kind = "loopback" } } }) {
            Write(Path.Combine(numbered, "session.json"), new { state = "recorded", session_dir = numbered, sources = invalidSources });
            True(catalog.Get(numbered).State == "invalid" && !catalog.Get(numbered).CanRetry,
                "malformed or duplicated source manifest does not fall back to legacy tracks");
        }
        File.Delete(Path.Combine(numbered, "mic.wav"));
        File.WriteAllText(numberedAudio, "unlisted numbered track");
        Write(Path.Combine(numbered, "session.json"), new { state = "recorded", session_dir = numbered });
        True(!catalog.Get(numbered).CanRetry, "legacy sessions do not guess undeclared numbered tracks");
        File.WriteAllText(Path.Combine(numbered, "system.wav"), "legacy system track");
        True(catalog.Get(numbered).CanRetry, "legacy system-only capture remains retryable");
        string linkedCapture = Session("20261014-111200", "recorded");
        string linkedRaw = Path.Combine(linkedCapture, "system_02.wav");
        string outsideRaw = Path.Combine(outside, "original.wav");
        File.WriteAllText(outsideRaw, "outside raw existence only");
        if (CreateSymbolicLink(linkedRaw, outsideRaw, 2)) {
            try {
                Write(Path.Combine(linkedCapture, "session.json"), new { state = "recorded", session_dir = linkedCapture,
                    sources = new[] { new { source = "system_02", path = "system_02.wav", kind = "loopback" } } });
                True(catalog.Get(linkedCapture).State == "invalid" && !catalog.Get(linkedCapture).CanRetry,
                    "declared numbered track symlink is rejected before retry");
            } finally { File.Delete(linkedRaw); }
        } else { skipped++; Console.WriteLine("SKIP: symbolic-link capture creation unavailable on this Windows account"); }
        string partialNote = Path.Combine(vault, "partial.md");
        File.WriteAllText(partialNote, "partial fixture transcript");
        Write(Path.Combine(failed, "status.json"), new { state = "error", note_path = partialNote });
        True(catalog.Get(failed).NotePath == partialNote && catalog.Get(failed).State == "failed",
            "failed meeting can open safe partial note without claiming completion");

        string completed = Session("20261014-120000", "recorded");
        string note = Path.Combine(vault, "fixture.md");
        string audio = Path.Combine(vault, "fixture.wav");
        File.WriteAllText(note, "fixture note", new UTF8Encoding(false));
        // Intentionally not valid WAV: the catalog is required to check existence
        // and containment only, and must never open or decode audio.
        File.WriteAllText(audio, "not-audio", new UTF8Encoding(false));
        Write(Path.Combine(completed, "status.json"), new { state = "completed", note_path = note,
            merged_audio = new { published_path = audio }, updated_at = "finished" });
        var finished = catalog.Get(completed);
        True(finished.State == "completed" && !finished.CanRetry && finished.NotePath == note && finished.AudioPath == audio,
            "completed legacy merged_audio path resolves without reading audio");
        string mp3Meeting = Session("20261015-130000", "recorded");
        string mp3 = Path.Combine(vault, "meeting.mp3");
        File.WriteAllText(mp3, "MP3 existence only; catalog must not decode");
        Write(Path.Combine(mp3Meeting, "status.json"), new { state = "completed", note_path = note, audio_path = mp3 });
        True(catalog.Get(mp3Meeting).State == "completed" && catalog.Get(mp3Meeting).AudioPath == mp3,
            "new MP3 result resolves without opening audio");
        File.Delete(mp3);
        Equal(catalog.Get(mp3Meeting).State, "transcript_only", "deleted MP3 has neutral transcript-only state");
        Write(Path.Combine(mp3Meeting, "status.json"), new { state = "completed", note_path = note,
            result_complete = true, merge_error = "encoder unavailable", merged_audio = (object)null });
        True(catalog.Get(mp3Meeting).CanRetry && catalog.Get(mp3Meeting).State == "failed",
            "complete transcript with failed MP3 encoding offers retained-lossless retry");
        File.WriteAllText(mp3, "MP3 existence only");
        Write(Path.Combine(mp3Meeting, "status.json"), new { state = "completed", note_path = note, audio_path = mp3 + ".exe" });
        Equal(catalog.Get(mp3Meeting).State, "invalid", "MP3-like executable extension remains rejected");
        string segmented = Session("20261015-130100", "recorded");
        File.Delete(Path.Combine(segmented, "mic.wav"));
        File.WriteAllText(Path.Combine(segmented, "mic.seg000001.wav"), "segment existence only");
        File.WriteAllText(Path.Combine(segmented, "mic.seg000002.wav"), "segment existence only");
        Write(Path.Combine(segmented, "session.json"), new { state = "recorded", session_dir = segmented,
            sources = new[] { new { source = "mic", path = "mic.wav", kind = "microphone", segments = new[] {
                new { path = "mic.seg000001.wav", start_frame = 0, frames = 10 },
                new { path = "mic.seg000002.wav", start_frame = 10, frames = 5 } } } } });
        True(catalog.Get(segmented).CanRetry, "all declared lossless segments permit retry without logical mic.wav");
        File.Delete(Path.Combine(segmented, "mic.seg000002.wav"));
        True(!catalog.Get(segmented).CanRetry, "missing declared segment cannot be silently omitted");
        Write(Path.Combine(segmented, "session.json"), new { state = "recorded", session_dir = segmented,
            sources = new[] { new { source = "mic", path = "mic.wav", kind = "microphone", segments = new[] {
                new { path = "mic.seg000001.wav", start_frame = 1, frames = 10 } } } } });
        Equal(catalog.Get(segmented).State, "invalid", "segment gaps are rejected before retry");
        Write(Path.Combine(segmented, "session.json"), new { state = "recorded", session_dir = segmented,
            sources = new[] { new { source = "mic", path = "mic.wav", kind = "microphone", segments = new[] {
                new { path = "../mic.seg000001.wav", start_frame = 0, frames = 10 } } } } });
        Equal(catalog.Get(segmented).State, "invalid", "segment path traversal is rejected");
        catalog.MarkNotified(finished);
        True(!new SessionCatalog(home, vault).Get(completed).NeedsAttention, "completed is not repeatedly announced");
        string captureWarning = Session("20261014-120010", "recorded_with_error");
        string warningNote = Path.Combine(vault, "capture-warning.md");
        string warningAudio = Path.Combine(vault, "capture-warning.wav");
        File.WriteAllText(warningNote, "capture warning note");
        File.WriteAllText(warningAudio, "capture warning audio existence only");
        Write(Path.Combine(captureWarning, "session.json"), new { state = "recorded_with_error", session_dir = captureWarning,
            capture_error = "fixture capture discontinuity" });
        Write(Path.Combine(captureWarning, "status.json"), new { state = "completed", result_complete = true,
            note_path = warningNote, audio_path = warningAudio, message = "transcription completed" });
        var warning = catalog.Get(captureWarning);
        True(warning.State == "capture_warning" && warning.NeedsAttention && !warning.CanRetry,
            "completed ASR does not conceal a capture error or offer ineffective retranscription");
        True(warning.NotePath == warningNote && warning.AudioPath == warningAudio && warning.Message.Contains("采集异常"),
            "capture warning retains both output routes and explains the capture problem");
        catalog.MarkNotified(warning);
        True(!new SessionCatalog(home, vault).Get(captureWarning).NeedsAttention,
            "acknowledged capture warning does not alert again after restart");
        Write(Path.Combine(captureWarning, "session.json"), new { state = "recorded_with_error", session_dir = captureWarning });
        Equal(catalog.Get(captureWarning).State, "capture_warning", "recorded-with-error remains a warning without detailed error text");
        Write(Path.Combine(captureWarning, "session.json"), new { state = "recorded", session_dir = captureWarning,
            capture_error = "fixture capture failure" });
        Equal(catalog.Get(captureWarning).State, "capture_warning", "metadata capture error remains authoritative with ordinary recorded state");
        Write(Path.Combine(captureWarning, "session.json"), new { state = "recorded", session_dir = captureWarning });
        Write(Path.Combine(captureWarning, "status.json"), new { state = "completed", note_path = warningNote,
            audio_path = warningAudio, capture_needs_review = true });
        Equal(catalog.Get(captureWarning).State, "capture_warning", "worker capture-review flag is reflected in history");
        Write(Path.Combine(captureWarning, "status.json"), new { state = "completed", note_path = warningNote,
            audio_path = warningAudio, capture_error = "fixture worker capture error" });
        Equal(catalog.Get(captureWarning).State, "capture_warning", "worker capture error is reflected without a separate flag");
        File.Delete(warningAudio);
        True(catalog.Get(captureWarning).State == "capture_warning" && catalog.Get(captureWarning).NotePath == warningNote,
            "removing one output cannot hide a capture warning or its retained transcript");
        File.Delete(warningNote);
        True(catalog.Get(captureWarning).State == "capture_warning" && Contains(catalog.Snapshot(), captureWarning),
            "removing both outputs cannot hide known capture failure evidence");
        File.WriteAllText(warningNote, "capture warning note");
        File.WriteAllText(warningAudio, "capture warning audio existence only");
        Write(Path.Combine(captureWarning, "status.json"), new { state = "completed", note_path = warningNote,
            audio_path = warningAudio, capture_needs_review = false, capture_error = "" });
        Write(Path.Combine(captureWarning, "session.json"), new { state = "recorded", session_dir = captureWarning,
            diagnostics = new[] { new { timing_uncertain = true, message = "diagnostic alone" } } });
        Equal(catalog.Get(captureWarning).State, "completed", "diagnostics alone do not invent a capture failure");
        File.Delete(audio);
        var transcriptOnly = catalog.Get(completed);
        True(transcriptOnly.State == "transcript_only" && !transcriptOnly.CanRetry && !transcriptOnly.NeedsAttention,
            "user removed finished audio leaves a non-alerting transcript-only meeting");
        Equal(transcriptOnly.Message, "仅保留转录，录音文件已移除", "transcript-only state explains the retained result");
        True(transcriptOnly.NotePath == note && File.Exists(transcriptOnly.NotePath) && Contains(catalog.Snapshot(), completed),
            "transcript-only history retains its existing note entry");
        foreach (string errorKey in new[] { "merge_error", "artifact_error", "cleanup_error" }) {
            Write(Path.Combine(completed, "status.json"), new Dictionary<string, object> {
                { "state", "completed" }, { "note_path", note }, { "audio_path", audio }, { errorKey, "fixture failure" }
            });
            var partialFailure = catalog.Get(completed);
            True(partialFailure.State == "missing" && partialFailure.NeedsAttention && Contains(catalog.Snapshot(), completed),
                "partial output with " + errorKey + " remains an inspection case");
        }
        Write(Path.Combine(completed, "status.json"), new { state = "completed", note_path = note, audio_path = audio });
        Write(Path.Combine(completed, "session.json"), new { state = "recorded", session_dir = completed, capture_error = "fixture capture failure" });
        True(catalog.Get(completed).State == "capture_warning" && catalog.Get(completed).NeedsAttention,
            "capture failure with a retained transcript is not classified as ordinary user removal");
        Write(Path.Combine(completed, "session.json"), new { state = "recorded", session_dir = completed });
        File.Delete(note);
        var removed = catalog.Get(completed);
        True(removed.State == "removed" && !removed.NeedsAttention && !removed.CanRetry,
            "both explicitly published finished files removed by user are not a recoverable failure");
        True(!Contains(catalog.Snapshot(), completed), "removed completed meeting is omitted from history");
        True(File.Exists(Path.Combine(completed, "status.json")) && Directory.Exists(completed),
            "removed history never deletes its retained controller metadata");
        Write(Path.Combine(completed, "status.json"), new { status = "success", note_path = note, audio_path = audio });
        Equal(catalog.Get(completed).State, "removed", "legacy completed alias also recognizes cleared outputs");
        Write(Path.Combine(completed, "status.json"), new { state = "completed", note_path = note,
            audio_path = audio, artifact_error = "fixture verification error" });
        True(catalog.Get(completed).State == "missing" && Contains(catalog.Snapshot(), completed),
            "verification failure with absent files remains visible");
        Write(Path.Combine(completed, "status.json"), new { state = "completed", note_path = note,
            audio_path = audio, merge_error = "fixture merge error" });
        Equal(catalog.Get(completed).State, "missing", "merge failure with absent files remains visible");
        Write(Path.Combine(completed, "status.json"), new { state = "completed", note_path = note,
            audio_path = audio, cleanup_error = "fixture cleanup error" });
        Equal(catalog.Get(completed).State, "missing", "cleanup failure with absent files remains visible");
        Write(Path.Combine(completed, "status.json"), new { state = "completed", note_path = note });
        Equal(catalog.Get(completed).State, "missing", "undeclared audio path is not mistaken for deliberate removal");
        Write(Path.Combine(completed, "status.json"), new { state = "completed", note_path = note, audio_path = audio });
        Directory.CreateDirectory(audio);
        Equal(catalog.Get(completed).State, "missing", "replacement directory is not mistaken for deliberate removal");
        Directory.Delete(audio);
        File.WriteAllText(audio, "not-audio");
        var audioOnly = catalog.Get(completed);
        True(audioOnly.State == "audio_only" && !audioOnly.CanRetry && !audioOnly.NeedsAttention && Contains(catalog.Snapshot(), completed),
            "retaining only finished audio remains visible without a failure alert");
        True(audioOnly.AudioPath == audio && File.Exists(audioOnly.AudioPath), "audio-only history retains its existing recording entry");
        File.WriteAllText(note, "fixture note", new UTF8Encoding(false));
        Equal(catalog.Get(completed).State, "completed", "restoring both files restores completed meeting");

        string legacyRelative = Session("20261014-120050", "recorded");
        string legacyOutput = Path.Combine(vault, "attachments", "20261014-120050");
        Directory.CreateDirectory(legacyOutput);
        string legacyAudio = Path.Combine(legacyOutput, "meeting.wav");
        string legacyNote = Path.Combine(vault, "legacy-relative.md");
        Write(Path.Combine(legacyRelative, "session.json"), new { state = "recorded", session_dir = legacyRelative,
            audio_output_dir = legacyOutput });
        Write(Path.Combine(legacyRelative, "status.json"), new { state = "completed", note_path = legacyNote,
            merged_audio = new { path = "meeting.wav" } });
        File.WriteAllText(legacyAudio, "not-audio");
        File.WriteAllText(legacyNote, "legacy fixture note");
        var legacyFinished = catalog.Get(legacyRelative);
        True(legacyFinished.State == "completed" && legacyFinished.AudioPath == legacyAudio && legacyFinished.NotePath == legacyNote,
            "legacy merged basename resolves only through its declared absolute audio output directory");
        File.Delete(legacyAudio);
        True(catalog.Get(legacyRelative).State == "transcript_only" && !catalog.Get(legacyRelative).NeedsAttention,
            "legacy relative output also supports a user-retained transcript");
        File.Delete(legacyNote);
        True(catalog.Get(legacyRelative).State == "removed" && !Contains(catalog.Snapshot(), legacyRelative),
            "legacy relative output with both files removed is omitted from history");
        File.WriteAllText(legacyAudio, "not-audio");
        True(catalog.Get(legacyRelative).State == "audio_only" && !catalog.Get(legacyRelative).NeedsAttention,
            "legacy relative output also supports a user-retained recording");
        File.Delete(legacyAudio);
        Directory.CreateDirectory(legacyAudio);
        True(catalog.Get(legacyRelative).State == "missing" && catalog.Get(legacyRelative).NeedsAttention,
            "legacy output replaced by a directory is not treated as user file removal");
        Directory.Delete(legacyAudio);
        foreach (string relativePath in new[] { @"..\escape.wav", @"subdir\meeting.wav", "subdir/meeting.wav" }) {
            Write(Path.Combine(legacyRelative, "status.json"), new { state = "completed", note_path = legacyNote,
                merged_audio = new { path = relativePath } });
            Equal(catalog.Get(legacyRelative).State, "invalid", "legacy relative audio must be a basename: " + relativePath);
        }
        Write(Path.Combine(legacyRelative, "status.json"), new { state = "completed", note_path = legacyNote,
            merged_audio = new { path = "meeting.wav", published_path = Path.Combine(outside, "meeting.wav") } });
        Equal(catalog.Get(legacyRelative).State, "invalid", "unsafe published path must not fall back to a safe legacy basename");
        Write(Path.Combine(legacyRelative, "status.json"), new { state = "completed", note_path = legacyNote,
            merged_audio = new { path = "meeting.wav", published_path = "meeting.wav" } });
        Equal(catalog.Get(legacyRelative).State, "invalid", "relative explicit published path is not a legacy output basename");
        Write(Path.Combine(legacyRelative, "status.json"), new { state = "completed", note_path = legacyNote,
            audio_path = "meeting.wav", merged_audio = new { path = "meeting.wav" } });
        Equal(catalog.Get(legacyRelative).State, "invalid", "relative top-level audio path is not a legacy merged path fallback");
        Write(Path.Combine(legacyRelative, "status.json"), new { state = "completed", note_path = legacyNote,
            merged_audio = new { path = "meeting.wav" } });
        Write(Path.Combine(legacyRelative, "session.json"), new { state = "recorded", session_dir = legacyRelative,
            audio_output_dir = outside });
        Equal(catalog.Get(legacyRelative).State, "invalid", "legacy audio directory outside the vault is rejected");
        Write(Path.Combine(legacyRelative, "session.json"), new { state = "recorded", session_dir = legacyRelative,
            audio_output_dir = "attachments" });
        Equal(catalog.Get(legacyRelative).State, "invalid", "relative legacy audio directory cannot be resolved against the current directory");
        Write(Path.Combine(legacyRelative, "session.json"), new { state = "recorded", session_dir = legacyRelative });
        var unknownLegacy = catalog.Get(legacyRelative);
        True((unknownLegacy.State == "invalid" || unknownLegacy.State == "missing") && unknownLegacy.NeedsAttention && Contains(catalog.Snapshot(), legacyRelative),
            "legacy audio without a declared directory remains visible rather than guessing or hiding outputs");
        Write(Path.Combine(legacyRelative, "session.json"), new { state = "recorded", session_dir = legacyRelative,
            audio_output_dir = legacyOutput });
        Write(Path.Combine(legacyRelative, "status.json"), new { state = "completed", note_path = legacyNote,
            merged_audio = new { path = "meeting.wav" }, artifact_error = "fixture verification error" });
        True(catalog.Get(legacyRelative).State == "missing" && catalog.Get(legacyRelative).NeedsAttention,
            "legacy absent outputs with an artifact error remain visible");
        string offline = Session("20261014-120100", "recorded");
        string unavailableVault = Path.Combine(root, "offline-vault");
        Write(Path.Combine(offline, "status.json"), new { state = "completed",
            note_path = Path.Combine(unavailableVault, "fixture.md"), audio_path = Path.Combine(unavailableVault, "fixture.wav") });
        Equal(new SessionCatalog(home, unavailableVault).Get(offline).State, "missing",
            "unavailable vault must not hide every completed meeting as user removal");
        Write(Path.Combine(offline, "session.json"), new { state = "recorded", session_dir = offline,
            audio_output_dir = Path.Combine(unavailableVault, "attachments") });
        Write(Path.Combine(offline, "status.json"), new { state = "completed", note_path = Path.Combine(unavailableVault, "fixture.md"),
            merged_audio = new { path = "meeting.wav" } });
        var offlineLegacy = new SessionCatalog(home, unavailableVault).Get(offline);
        True(offlineLegacy.State == "missing" && offlineLegacy.NeedsAttention,
            "legacy relative outputs with an unavailable vault remain an inspection case");
        string incomplete = Session("20261014-120200", "recorded");
        Write(Path.Combine(incomplete, "status.json"), new { state = "error", note_path = note, audio_path = audio });
        True(catalog.Get(incomplete).State == "failed" && Contains(catalog.Snapshot(), incomplete),
            "failed session with absent outputs remains in history");
        Write(Path.Combine(incomplete, "status.json"), new { state = "queued", note_path = note, audio_path = audio });
        True(catalog.Get(incomplete).State == "retry" && Contains(catalog.Snapshot(), incomplete),
            "unfinished session with absent outputs remains recoverable");
        File.WriteAllText(audio, "not-audio");
        Write(Path.Combine(completed, "status.json"), new { state = "completed", note_path = note, published_audio_path = audio });
        Equal(catalog.Get(completed).State, "completed", "top-level published_audio_path supported");
        Write(Path.Combine(completed, "status.json"), new { state = "completed", note_path = note, audio_path = audio, merge_error = "fixture merge error" });
        True(catalog.Get(completed).State == "missing" && !catalog.Get(completed).CanRetry,
            "merge error cannot be reported as successful completed artifacts");
        Write(Path.Combine(completed, "status.json"), new { state = "completed", note_path = note, audio_path = audio, artifact_error = "fixture verification error" });
        Equal(catalog.Get(completed).State, "missing", "artifact verification error requires inspection");
        Write(Path.Combine(completed, "status.json"), new { state = "completed", note_path = note, audio_path = audio, cleanup_error = "fixture cleanup error" });
        True(catalog.Get(completed).State == "completed" && catalog.Get(completed).Message.Contains("清理未完成"),
            "cleanup error retains successful artifact state with explanation");
        string local = Session("20261014-121000", "recorded");
        string localAudio = Path.Combine(local, "meeting.wav");
        File.WriteAllText(localAudio, "not-audio");
        Write(Path.Combine(local, "status.json"), new { state = "completed", note_path = note, audio_path = localAudio });
        Equal(catalog.Get(local).State, "completed", "top-level audio_path supports own local artifact");
        string originalLabel = catalog.Get(local).Label;
        Write(Path.Combine(local, "session.json"), new { state = "recorded", session_dir = local,
            start_time = "2026-10-14T09:00:00+08:00", title = "  产品会议  " });
        Equal(catalog.Get(local).Label, originalLabel + " · 产品会议", "meeting title retains its chronological label");
        Write(Path.Combine(local, "session.json"), new { state = "recorded", session_dir = local,
            start_time = "2026-10-14T09:00:00+08:00", title = "产品\n会议\u202e\0" });
        Equal(catalog.Get(local).Label, originalLabel + " · 产品 会议", "display title removes control and format characters");
        Write(Path.Combine(local, "session.json"), new { state = "recorded", session_dir = local,
            start_time = "2026-10-14T09:00:00+08:00", title = new string('长', 1000) });
        True(catalog.Get(local).Label.Length <= originalLabel.Length + 3 + 64, "display title length is bounded");

        string live = Session("20261014-130000", "recorded");
        using (var process = Process.GetCurrentProcess()) {
            catalog.SaveWorker(live, process);
            Write(Path.Combine(live, "status.json"), new { state = "queued", message = "waiting" });
            var running = catalog.Get(live);
            True(running.State == "queued" && running.WorkerAlive && !running.CanRetry && !running.NeedsAttention,
                "verified live worker takes priority over recovery");
            using (var reopened = running.TryOpenWorker()) True(reopened != null, "worker identity can be reopened");
            Write(Path.Combine(live, "status.json"), new { state = "completed", note_path = note, audio_path = localAudio });
            True(catalog.Get(live).State == "processing", "live worker suppresses premature completed state");
            File.WriteAllText(Path.Combine(live, "status.json"), "{unreadable-update");
            True(catalog.Get(live).State == "processing" && !catalog.Get(live).CanRetry,
                "live worker remains processing when status is temporarily unreadable");

            string legacy = Session("20261014-140000", "recorded");
            Write(Path.Combine(home, "last-session.json"), new { session_dir = legacy, worker_pid = process.Id,
                worker_start_time_utc = process.StartTime.ToUniversalTime().ToString("o") });
            True(catalog.Get(legacy).WorkerAlive, "legacy last-session PID/starttime adopted");
            catalog.SaveWorker(legacy, null);
            True(catalog.Get(legacy).WorkerAlive && catalog.Get(legacy).State == "processing",
                "controller PID zero adopts matching legacy fallback identity");
            Write(Path.Combine(legacy, "controller.json"), new { worker_pid = process.Id,
                worker_start_time_utc = process.StartTime.ToUniversalTime().AddSeconds(-1).ToString("o") });
            True(!catalog.Get(legacy).WorkerAlive && catalog.Get(legacy).State == "failed", "reused PID rejected by starttime");
            var stopped = catalog.Get(legacy);
            catalog.MarkNotified(stopped);
            True(!catalog.Get(legacy).NeedsAttention, "dead worker failure persisted");
            catalog.SaveWorker(legacy, process);
            True(catalog.Get(legacy).WorkerAlive, "retry writes new worker identity");

            string oldCompleted = Session("20261014-143000", "recorded");
            Write(Path.Combine(oldCompleted, "status.json"), new { state = "completed", note_path = note, audio_path = audio });
            File.SetLastWriteTimeUtc(Path.Combine(oldCompleted, "status.json"), process.StartTime.ToUniversalTime().AddMinutes(-1));
            Write(Path.Combine(oldCompleted, "controller.json"), new { worker_pid = process.Id,
                worker_start_time_utc = process.StartTime.ToUniversalTime().AddSeconds(1).ToString("o") });
            True(catalog.Get(oldCompleted).State == "failed" && catalog.Get(oldCompleted).CanRetry,
                "prior completed status cannot satisfy a newer terminated attempt");
        }

        string legacyDead = Session("20261014-145000", "recorded");
        Write(Path.Combine(legacyDead, "status.json"), new { state = "error", message = "old process failed" });
        Write(Path.Combine(home, "last-session.json"), new { session_dir = legacyDead, worker_pid = int.MaxValue,
            worker_start_time_utc = DateTime.UtcNow.AddDays(-1).ToString("o") });
        var dead = catalog.Get(legacyDead);
        catalog.MarkNotified(dead);
        True(!catalog.Get(legacyDead).NeedsAttention, "legacy final identity survives controller adoption");

        string malformed = Session("20261014-150000", "recorded");
        File.WriteAllText(Path.Combine(malformed, "status.json"), "{invalid");
        var snapshots = catalog.Snapshot();
        True(Find(snapshots, malformed).State == "invalid" && Find(snapshots, recorded).State == "retry",
            "malformed single session does not block other sessions");
        string redirect = Session("20261014-160000", "recorded");
        Write(Path.Combine(redirect, "session.json"), new { state = "recorded", session_dir = recorded });
        True(catalog.Get(redirect).State == "invalid" && !catalog.Get(redirect).CanRetry, "metadata cannot redirect to sibling session");
        Throws(delegate { catalog.Get(outside); }, "outside work root rejected");
        Throws(delegate { catalog.SaveWorker(outside, null); }, "outside write rejected");
        string foreignNote = Path.Combine(outside, "foreign.md");
        File.WriteAllText(foreignNote, "private fixture");
        string pathJump = Session("20261014-170000", "recorded");
        Write(Path.Combine(pathJump, "status.json"), new { state = "completed", note_path = foreignNote, audio_path = audio });
        Equal(catalog.Get(pathJump).State, "invalid", "finished note outside configured vault rejected");
        Write(Path.Combine(pathJump, "status.json"), new { state = "completed", note_path = note, audio_path = localAudio });
        Equal(catalog.Get(pathJump).State, "invalid", "finished audio cannot point at sibling private work session");
        Write(Path.Combine(pathJump, "status.json"), new { state = "error", note_path = foreignNote });
        Equal(catalog.Get(pathJump).State, "invalid", "partial note outside configured vault rejected");
        Write(Path.Combine(home, "last-session.json"), new { session_dir = outside, worker_pid = 1 });
        True(catalog.Snapshot().Count >= 10, "bad legacy pointer does not block safe work scan");

        string link = Path.Combine(home, "work", "20261014-180000");
        if (CreateSymbolicLink(link, outside, 1 | 2)) {
            try {
                True(Find(catalog.Snapshot(), link).State == "invalid", "reparse meeting directory rejected before traversal");
                Throws(delegate { catalog.SaveWorker(link, null); }, "reparse writes rejected");
            } finally { Directory.Delete(link); }
        } else { skipped++; Console.WriteLine("SKIP: symbolic-link creation unavailable on this Windows account"); }
        string fileLink = Path.Combine(vault, "linked.md");
        if (CreateSymbolicLink(fileLink, foreignNote, 2)) {
            try {
                Write(Path.Combine(pathJump, "status.json"), new { state = "completed", note_path = fileLink, audio_path = audio });
                Equal(catalog.Get(pathJump).State, "invalid", "reparse finished file rejected");
            } finally { File.Delete(fileLink); }
        } else { skipped++; Console.WriteLine("SKIP: symbolic-link file creation unavailable on this Windows account"); }

        Equal(SessionCatalog.DisplayTitle(new Dictionary<string, object> { { "state", "queued" } }), "排队中", "queued title");
        Equal(SessionCatalog.DisplayTitle(new Dictionary<string, object> { { "stage", "merge" } }), "合并录音", "merge title");
        Equal(SessionCatalog.DisplayTitle(new Dictionary<string, object> { { "stage", "prepare" } }), "分析说话人", "speaker title");
        Equal(SessionCatalog.DisplayTitle(new Dictionary<string, object> { { "stage", "asr" }, { "completed", 23 }, { "total", 80 } }), "转写 23/80 段", "real segment progress");
        Equal(SessionCatalog.DisplayTitle(new Dictionary<string, object> { { "stage", "asr" }, { "completed", 1 } }), "转写中", "no invented progress without total");
        Equal(SessionCatalog.DisplayTitle(new Dictionary<string, object> { { "stage", "asr" }, { "completed", 81 }, { "total", 80 } }), "转写中", "invalid progress does not invent percent");
    }

    static string Session(string name, string state) {
        string path = Path.Combine(home, "work", name);
        Directory.CreateDirectory(path);
        Write(Path.Combine(path, "session.json"), new { state = state, session_dir = path, start_time = "2026-10-14T09:00:00+08:00" });
        File.WriteAllText(Path.Combine(path, "mic.wav"), "raw fixture existence only");
        return path;
    }
    static void Write(string path, object value) { File.WriteAllText(path, Json.Serialize(value), new UTF8Encoding(false)); }
    static SessionRecord Find(List<SessionRecord> records, string path) {
        foreach (var record in records) if (record.DirectoryPath == path) return record;
        throw new Exception("fixture not found: " + Path.GetFileName(path));
    }
    static bool Contains(List<SessionRecord> records, string path) {
        foreach (var record in records) if (record.DirectoryPath == path) return true;
        return false;
    }
    static void True(bool value, string label) { if (!value) throw new Exception(label); passed++; }
    static void Equal(object actual, object expected, string label) { True(object.Equals(actual, expected), label + ": " + actual); }
    static void Throws(Action action, string label) {
        try { action(); } catch (IOException) { passed++; return; }
        throw new Exception(label);
    }
}
