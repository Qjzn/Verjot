using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using NAudio.CoreAudioApi;
using NAudio.Wave;

// Exercises the actual production storage path with synthetic packets. No audio
// hardware, playback, user vault, model inference or multi-GB fixture is used.
internal static class TestSegmentedTimeline {
    static int assertions;
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    static TimelineWriter Segmented(string path, WaveFormat format, int seconds) {
        ConstructorInfo create = typeof(TimelineWriter).GetConstructor(new Type[] { typeof(string), typeof(WaveFormat), typeof(int) });
        Check(create != null, "production timeline must expose duration-bounded lossless segments");
        return (TimelineWriter)create.Invoke(new object[] { path, format, seconds });
    }
    static object[] Segments(TimelineWriter writer) {
        PropertyInfo property = typeof(TimelineWriter).GetProperty("Segments");
        Check(property != null, "segment frame manifest is required");
        return (object[])property.GetValue(writer, null);
    }
    static byte[] ReadAll(string root, TimelineWriter writer, WaveFormat expected, long totalFrames, long perSegment) {
        object[] segments = Segments(writer);
        Check(segments.Length == Math.Max(1, (totalFrames + perSegment - 1) / perSegment), "no duplicate or empty rollover segment");
        long first = 0;
        using (var joined = new MemoryStream()) {
            foreach (Dictionary<string, object> segment in segments) {
                long start = Convert.ToInt64(segment["start_frame"]), frames = Convert.ToInt64(segment["frames"]);
                Check(start == first, "segment starts must be exactly contiguous in session frame coordinates");
                Check(frames == Math.Min(perSegment, totalFrames - first), "segment has exact duration/stop-boundary frame count");
                string file = Path.Combine(root, (string)segment["path"]);
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                Check(true, "closed segments release file handles");
                using (var wave = new WaveFileReader(file)) {
                    Check(wave.WaveFormat.SampleRate == expected.SampleRate && wave.WaveFormat.Channels == expected.Channels &&
                        wave.WaveFormat.BitsPerSample == expected.BitsPerSample && wave.WaveFormat.Encoding == expected.Encoding,
                        "segments retain the device's lossless sample format");
                    Check(wave.Length == frames * expected.BlockAlign, "WAV header and manifest frame counts agree");
                    byte[] buffer = new byte[16384]; int read;
                    while ((read = wave.Read(buffer, 0, buffer.Length)) > 0) joined.Write(buffer, 0, read);
                }
                first += frames;
            }
            Check(first == totalFrames, "all segment frames end at the shared session stop boundary");
            return joined.ToArray();
        }
    }
    static short Pattern(long frame) { return (short)(frame % 30000 + 1); }
    static byte[] Pcm(long first, int frames, short constant) {
        byte[] bytes = new byte[frames * 2];
        for (int frame = 0; frame < frames; frame++) {
            short sample = constant == 0 ? Pattern(first + frame) : constant;
            bytes[frame * 2] = (byte)sample; bytes[frame * 2 + 1] = (byte)(sample >> 8);
        }
        return bytes;
    }
    static byte[] FloatStereo(int frames) {
        byte[] bytes = new byte[frames * 8];
        for (int frame = 0; frame < frames; frame++) {
            Buffer.BlockCopy(BitConverter.GetBytes(.25f), 0, bytes, frame * 8, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(-.5f), 0, bytes, frame * 8 + 4, 4);
        }
        return bytes;
    }
    static void ExactPacketSeams(string root) {
        string path = Path.Combine(root, "pattern.wav"); var format = new WaveFormat(8000, 16, 1); const int total = 195123;
        using (var writer = Segmented(path, format, 10)) {
            for (int first = 0; first < total; first += 1377) {
                byte[] packet = Pcm(first, Math.Min(1377, total - first), 0);
                writer.AddTimestamped(packet, packet.Length, first * 1000.0 / format.SampleRate, first, false);
            }
            writer.Finish(total * 1000.0 / format.SampleRate);
            byte[] actual = ReadAll(root, writer, format, total, 80000);
            bool equal = true;
            for (int frame = 0; frame < total; frame++) equal &= BitConverter.ToInt16(actual, frame * 2) == Pattern(frame);
            Check(equal, "every original PCM sample survives packet and storage seams without duplication or loss");
            Check(!File.Exists(path), "segmented recording must not create the logical source WAV");
        }
    }
    static void SilentGapsAndTail(string root) {
        string path = Path.Combine(root, "silent-gaps.wav"); var format = WaveFormat.CreateIeeeFloatWaveFormat(1000, 2);
        using (var writer = Segmented(path, format, 10)) {
            byte[] a = FloatStereo(2000), b = FloatStereo(1000);
            writer.AddTimestamped(a, a.Length, 9500, 0, false);
            writer.AddTimestamped(b, b.Length, 17500, 2000, false);
            writer.Finish(23000);
            byte[] actual = ReadAll(root, writer, format, 23000, 10000);
            bool equal = true;
            for (int frame = 0; frame < 23000; frame++) {
                bool audible = frame >= 9500 && frame < 11500 || frame >= 17500 && frame < 18500;
                equal &= BitConverter.ToSingle(actual, frame * 8) == (audible ? .25f : 0);
                equal &= BitConverter.ToSingle(actual, frame * 8 + 4) == (audible ? -.5f : 0);
            }
            Check(equal, "leading silence, suspended loopback counter gap and silent tail cross segments without moving audio");
        }
    }
    static void LatePauseAndOverrunTrim(string root) {
        string path = Path.Combine(root, "late-pause.wav"); var format = new WaveFormat(1000, 16, 1);
        using (var writer = Segmented(path, format, 10)) {
            byte[] packet = Pcm(0, 45000, 1000);
            writer.Add(packet, packet.Length, 45000); // Buffered data already crosses four closed WAVs.
            writer.SetPaused(true, 9500); writer.SetPaused(false, 20500);
            writer.SetEndBoundary(23000); writer.Finish(23000);
            byte[] actual = ReadAll(root, writer, format, 23000, 10000);
            bool equal = true;
            for (int frame = 0; frame < 23000; frame++) equal &= BitConverter.ToInt16(actual, frame * 2) ==
                (frame >= 9500 && frame < 20500 ? 0 : 1000);
            Check(equal, "late microphone pause repairs closed segments while preserving unpaused frames");
            Check(Directory.GetFiles(root, "late-pause.seg*.wav").Length == 3, "stop removes complete overrun segments and truncates the last one");
        }
    }
    static void PendingStopAcrossSeam(string root) {
        string path = Path.Combine(root, "pending-stop.wav"); var format = new WaveFormat(1000, 16, 1);
        using (var writer = Segmented(path, format, 10)) {
            byte[] packet = Pcm(0, 3000, 2000);
            writer.AddTimestamped(packet, packet.Length, 9500, 0, false);
            writer.SetEndBoundary(10500);
            writer.AddTimestamped(packet, packet.Length, 12500, 3000, false);
            writer.Finish(10500);
            byte[] actual = ReadAll(root, writer, format, 10500, 10000);
            bool equal = true;
            for (int frame = 0; frame < 10500; frame++) equal &= BitConverter.ToInt16(actual, frame * 2) == (frame < 9500 ? 0 : 2000);
            Check(equal, "a packet delivered after stop retains its pre-stop syllable across the storage seam only");
        }
    }
    static void SharedDualTimeline(string root) {
        string micPath = Path.Combine(root, "dual-mic.wav"), systemPath = Path.Combine(root, "dual-system.wav");
        var micFormat = new WaveFormat(16000, 16, 1); var systemFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        using (var mic = Segmented(micPath, micFormat, 10))
        using (var system = Segmented(systemPath, systemFormat, 10)) {
            mic.SetPaused(true, 9950); mic.SetPaused(false, 10050);
            mic.SetPaused(true, 19950); system.SetPaused(true, 19950);
            mic.SetPaused(false, 20050); system.SetPaused(false, 20050);
            for (int packet = 0; packet < 2130; packet++) {
                byte[] microphone = Pcm(packet * 160, 160, 1000), computer = FloatStereo(480);
                // Independent packet queues can be delivered arbitrarily late;
                // the acquisition QPC/frame position keeps both time axes fixed.
                mic.AddTimestamped(microphone, microphone.Length, packet * 10, packet * 160, false);
                system.AddTimestamped(computer, computer.Length, packet * 10, packet * 480, false);
            }
            mic.Finish(21234.375); system.Finish(21234.375);
            byte[] a = ReadAll(root, mic, micFormat, 339750, 160000);
            byte[] b = ReadAll(root, system, systemFormat, 1019250, 480000);
            bool micExact = true, systemExact = true;
            for (int frame = 0; frame < a.Length / 2; frame++) {
                double at = frame / 16.0;
                bool quiet = at >= 9950 && at < 10050 || at >= 19950 && at < 20050;
                micExact &= BitConverter.ToInt16(a, frame * 2) == (quiet ? 0 : 1000);
            }
            for (int frame = 0; frame < b.Length / 8; frame++) {
                double at = frame / 48.0; bool quiet = at >= 19950 && at < 20050;
                systemExact &= BitConverter.ToSingle(b, frame * 8) == (quiet ? 0 : .25f);
                systemExact &= BitConverter.ToSingle(b, frame * 8 + 4) == (quiet ? 0 : -.5f);
            }
            Check(micExact && systemExact, "different device formats retain one session clock: mic-only pause affects only mic, global pause affects both");
        }
    }
    static void ByteCapAndSingleOpenWriter(string root) {
        string path = Path.Combine(root, "byte-cap.wav"); var format = new WaveFormat(1000, 16, 1);
        using (var writer = Segmented(path, format, 10)) {
            FieldInfo cap = typeof(TimelineWriter).GetField("segmentByteLimit", Private);
            Check(cap != null && (long)cap.GetValue(writer) == 20000, "duration cap is expressed as exact complete audio frames");
            cap.SetValue(writer, 200L); // Trigger the production byte-cap rollover with 100-frame fixtures.
            byte[] packet = Pcm(0, 250, 1000); writer.Add(packet, packet.Length, 250);
            using (var exclusive = new FileStream(Path.Combine(root, "byte-cap.seg000001.wav"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            Check(true, "the prior segment is closed before the next segment receives data");
            writer.Finish(250); ReadAll(root, writer, format, 250, 100);
        }
    }
    static void CaptureTrackUsesSegments(string root) {
        // The event-based production constructor is exercised without opening a
        // device. Its otherwise-uninitialized WASAPI object only supplies format.
        var capture = (WasapiCapture)FormatterServices.GetUninitializedObject(typeof(WasapiCapture));
        capture.WaveFormat = new WaveFormat(1000, 16, 1);
        string path = Path.Combine(root, "capture-track.wav");
        ConstructorInfo constructor = typeof(CaptureTrack).GetConstructor(new Type[] { typeof(WasapiCapture), typeof(string), typeof(string), typeof(Stopwatch), typeof(Action<Exception>), typeof(int) });
        Check(constructor != null, "CaptureTrack must pass configured segment duration into the production writer");
        using (var track = (CaptureTrack)constructor.Invoke(new object[] { capture, "offline", path, Stopwatch.StartNew(), new Action<Exception>(delegate(Exception error) { throw error; }), 10 })) {
            var writer = (TimelineWriter)typeof(CaptureTrack).GetField("writer", Private).GetValue(track);
            byte[] packet = Pcm(0, 11000, 1000); writer.Add(packet, packet.Length, 11000);
            track.Finish(11000);
            object[] segments = (object[])typeof(CaptureTrack).GetProperty("Segments").GetValue(track, null);
            Check(segments.Length == 2 && !File.Exists(path), "CaptureTrack uses segmented storage and leaves its manifest readable after disposal");
        }
    }
    static void DriftPhaseSurvivesSeams(string root) {
        var format = new WaveFormat(8000, 16, 1); const double speed = 1.0001;
        string path = Path.Combine(root, "drift-seams.wav"), reference = Path.Combine(root, "drift-reference.wav");
        using (var segmented = Segmented(path, format, 10))
        using (var single = new TimelineWriter(reference, format)) {
            foreach (TimelineWriter writer in new TimelineWriter[] { segmented, single }) {
                writer.SetPaused(true, 9995.125); writer.SetPaused(false, 10015.625);
                for (int first = 0, packetIndex = 0; first < 200000; first += 137, packetIndex++) {
                    byte[] packet = Pcm(first, Math.Min(137, 200000 - first), 0);
                    double qpc = first * 1000.0 / format.SampleRate / speed + (packetIndex % 2 == 0 ? 0 : .04);
                    writer.AddTimestamped(packet, packet.Length, qpc, first, false);
                }
                writer.Finish(25000);
            }
            byte[] joined = ReadAll(root, segmented, format, 200000, 80000);
            byte[] original;
            using (var wave = new WaveFileReader(reference)) {
                original = new byte[wave.Length]; Check(wave.Read(original, 0, original.Length) == original.Length, "drift reference retains complete session");
            }
            bool equal = joined.Length == original.Length;
            for (int index = 0; index < joined.Length && equal; index++) equal &= joined[index] == original[index];
            Check(equal, "storage seams must not reset hardware-clock fit or fractional resampling phase under drift, QPC jitter and pause");
        }
    }
    static void RolloverFailureKeepsPrefix(string root) {
        var format = new WaveFormat(1000, 16, 1); string path = Path.Combine(root, "failed-rollover.wav");
        Directory.CreateDirectory(Path.Combine(root, "failed-rollover.seg000002.wav"));
        using (var writer = Segmented(path, format, 10)) {
            byte[] packet = Pcm(0, 15000, 1000); Exception failure = null;
            try { writer.Add(packet, packet.Length, 15000); } catch (Exception error) { failure = error; }
            Check(failure is TimelineWriter.StorageException, "rollover creation error must reach the fatal storage failure path");
            byte[] prefix = ReadAll(root, writer, format, 10000, 10000);
            Check(BitConverter.ToInt16(prefix, prefix.Length - 2) == 1000, "successfully closed prefix survives failure opening the next segment");
        }
        Directory.Delete(Path.Combine(root, "failed-rollover.seg000002.wav"));
    }
    static void HighBandwidthByteBound(string root) {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(192000, 64);
        using (var writer = Segmented(Path.Combine(root, "large-format.wav"), format, 300)) {
            long cap = (long)typeof(TimelineWriter).GetField("segmentByteLimit", Private).GetValue(writer);
            Check(cap == 0xf0000000L / format.BlockAlign * format.BlockAlign && cap % format.BlockAlign == 0,
                "high-bandwidth device formats rotate at the safe RIFF byte limit before duration, on complete frames");
            writer.Finish(0);
        }
    }
    static void DurationBounds(string root) {
        foreach (int invalid in new int[] { 0, 9, 601 }) {
            bool rejected = false;
            try { using (var writer = Segmented(Path.Combine(root, "invalid-duration.wav"), new WaveFormat(1000, 16, 1), invalid)) { } }
            catch (TargetInvocationException error) { rejected = error.InnerException is ArgumentOutOfRangeException; }
            Check(rejected && !File.Exists(Path.Combine(root, "invalid-duration.seg000001.wav")), "invalid segmentation duration fails before creating audio files");
        }
        var capture = (WasapiCapture)FormatterServices.GetUninitializedObject(typeof(WasapiCapture));
        capture.WaveFormat = new WaveFormat(1000, 16, 1);
        using (var track = new CaptureTrack(capture, "offline-default", Path.Combine(root, "default-duration.wav"), Stopwatch.StartNew(), delegate(Exception error) { throw error; })) {
            var writer = (TimelineWriter)typeof(CaptureTrack).GetField("writer", Private).GetValue(track);
            Check((long)typeof(TimelineWriter).GetField("segmentByteLimit", Private).GetValue(writer) == 600000L,
                "the compatibility production CaptureTrack overload defaults to exactly 300 seconds");
            track.Finish(0);
        }
    }
    static void FormatIdentity() {
        MethodInfo match = typeof(CaptureTrack).GetMethod("SameCaptureFormat", BindingFlags.Static | BindingFlags.NonPublic);
        Check(match != null, "capture startup must compare complete sample format identity");
        var floating = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var pcm = new WaveFormat(48000, 32, 2);
        Check((bool)match.Invoke(null, new object[] { floating, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2) }), "unchanged float format starts normally");
        Check(!(bool)match.Invoke(null, new object[] { floating, pcm }), "same rate/channel/width cannot disguise PCM32 as float32");
        var extendedA = new WaveFormatExtensible(48000, 32, 2);
        var extendedB = new WaveFormatExtensible(48000, 32, 2);
        Check((bool)match.Invoke(null, new object[] { extendedA, extendedB }), "unchanged extensible format starts normally");
        typeof(WaveFormatExtensible).GetField("subFormat", Private).SetValue(extendedB, new Guid("00000001-0000-0010-8000-00aa00389b71"));
        Check(!(bool)match.Invoke(null, new object[] { extendedA, extendedB }), "extensible sample encoding changes are rejected before capture");
        extendedB = new WaveFormatExtensible(48000, 32, 2);
        typeof(WaveFormatExtensible).GetField("dwChannelMask", Private).SetValue(extendedB, 12);
        Check(!(bool)match.Invoke(null, new object[] { extendedA, extendedB }), "channel assignment changes cannot reuse the old WAV header");
        extendedB = new WaveFormatExtensible(48000, 32, 2);
        typeof(WaveFormatExtensible).GetField("wValidBitsPerSample", Private).SetValue(extendedB, (short)24);
        Check(!(bool)match.Invoke(null, new object[] { extendedA, extendedB }), "valid precision changes cannot reuse the old WAV header");
    }
    public static int Main(string[] args) {
        try {
            string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
            ExactPacketSeams(root); SilentGapsAndTail(root); LatePauseAndOverrunTrim(root);
            PendingStopAcrossSeam(root); SharedDualTimeline(root); ByteCapAndSingleOpenWriter(root); CaptureTrackUsesSegments(root);
            DriftPhaseSurvivesSeams(root); RolloverFailureKeepsPrefix(root); HighBandwidthByteBound(root); DurationBounds(root); FormatIdentity();
            Console.WriteLine("Segmented timeline: " + assertions + " assertions passed; " + root); return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
