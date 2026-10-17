using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NAudio.Wave;

internal static class TestTimestampedTimeline {
    static int assertions;
    const int Rate = 16000;
    static readonly MethodInfo timestamped = typeof(TimelineWriter).GetMethod("AddTimestamped");
    static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    static byte[] Packet(int marker, int frames) {
        byte[] data = new byte[frames * 2];
        Buffer.BlockCopy(BitConverter.GetBytes((short)marker), 0, data, frames, 2);
        return data;
    }
    static void Push(TimelineWriter writer, byte[] data, double startMs, long position, double callbackMs) {
        // The old arrival-time path is the RED control; production must use the timestamped API.
        if (timestamped == null) writer.Add(data, data.Length, callbackMs);
        else timestamped.Invoke(writer, new object[] { data, data.Length, startMs, position, false });
    }
    static Dictionary<int, double> Markers(string path) {
        var values = new Dictionary<int, double>();
        using (var wave = new WaveFileReader(path)) {
            byte[] samples = new byte[65536]; long first = 0; int count;
            while ((count = wave.Read(samples, 0, samples.Length)) > 0) {
                for (int i = 0; i + 1 < count; i += 2) {
                    int value = BitConverter.ToInt16(samples, i);
                    if (value != 0) values[value] = (first + i / 2) * 1000.0 / Rate;
                }
                first += count / 2;
            }
        }
        return values;
    }
    static void Schedule(string root, string name, double[] arrivals, double[] starts, double stop) {
        string file = Path.Combine(root, name + ".wav");
        using (var writer = new TimelineWriter(file, new WaveFormat(Rate, 16, 1))) {
            for (int i = 0; i < starts.Length; i++) Push(writer, Packet(i + 1, 1600), starts[i], (long)Math.Round(starts[i] * Rate / 1000), arrivals[i]);
            writer.SetEndBoundary(stop); writer.Finish(stop);
        }
        var actual = Markers(file);
        Check(actual.Count == starts.Length, name + ": all pre-stop markers must survive; got " + actual.Count);
        for (int i = 0; i < starts.Length; i++) Check(Math.Abs(actual[i + 1] - starts[i] - 50) <= .0625,
            name + ": callback delivery must not change the sample timeline");
        using (var wave = new WaveFileReader(file)) Check(Math.Abs(wave.TotalTime.TotalMilliseconds - stop) <= .0625,
            name + ": both sources must retain the same stop boundary");
    }
    static void PausedDelayedPackets(string root) {
        string file = Path.Combine(root, "timestamped-pause.wav");
        byte[] packet = new byte[3200];
        for (int i = 0; i < 1600; i++) Buffer.BlockCopy(BitConverter.GetBytes((short)1000), 0, packet, i * 2, 2);
        using (var writer = new TimelineWriter(file, new WaveFormat(Rate, 16, 1))) {
            writer.SetPaused(true, 150); writer.SetPaused(false, 250);
            for (int i = 0; i < 4; i++) Push(writer, packet, i * 100, i * 1600, 500);
            writer.SetEndBoundary(350); writer.Finish(350);
        }
        using (var wave = new WaveFileReader(file)) {
            byte[] samples = new byte[11200]; Check(wave.Read(samples, 0, samples.Length) == samples.Length, "pause: length at stop");
            bool matched = true;
            for (int i = 0; i < 5600; i++) matched &= BitConverter.ToInt16(samples, i * 2) == (short)(i >= 2400 && i < 4000 ? 0 : 1000);
            Check(matched, "pause masks acquisition time, including callback arriving after resume and after stop");
        }
    }
    static void SampleClock(string root, string name, double speed, int seconds) {
        string file = Path.Combine(root, name + ".wav");
        double[] markers = { seconds / 3.0, seconds * 2.0 / 3, seconds - 1.0 };
        long[] markerFrames = new long[markers.Length];
        for (int i = 0; i < markers.Length; i++) markerFrames[i] = (long)Math.Round(markers[i] * Rate * speed);
        long total = (long)Math.Round(seconds * Rate * speed);
        using (var writer = new TimelineWriter(file, new WaveFormat(Rate, 16, 1))) {
            for (long first = 0; first < total; first += 1600) {
                int count = (int)Math.Min(1600, total - first); byte[] data = new byte[count * 2];
                for (int i = 0; i < markerFrames.Length; i++) if (markerFrames[i] >= first && markerFrames[i] < first + count)
                    Buffer.BlockCopy(BitConverter.GetBytes((short)12000), 0, data, (int)(markerFrames[i] - first) * 2, 2);
                Push(writer, data, first * 1000.0 / Rate / speed, first, (first + count) * 1000.0 / Rate / speed + 60);
            }
            writer.Finish(seconds * 1000.0);
        }
        using (var wave = new WaveFileReader(file)) {
            foreach (double marker in markers) {
                long first = (long)Math.Round((marker - .005) * Rate); wave.Position = first * 2;
                byte[] data = new byte[320]; int read = wave.Read(data, 0, data.Length), best = -1, peak = 0;
                for (int i = 0; i + 1 < read; i += 2) if (Math.Abs((int)BitConverter.ToInt16(data, i)) > peak) { peak = Math.Abs((int)BitConverter.ToInt16(data, i)); best = i / 2; }
                Check(peak > 3000, name + ": pre-stop marker retained after clock correction");
                Check(Math.Abs((first + best) * 1000.0 / Rate - marker * 1000) <= .0625,
                    name + ": hardware clock drift must stay within one sample, including delayed delivery");
            }
            Check(Math.Abs(wave.TotalTime.TotalSeconds - seconds) <= 1.0 / Rate, name + ": stop boundary");
        }
    }
    static void EndpointSelection() {
        string[] active = { "Realtek", "NVIDIA", "Headphones" };
        Check(CaptureEndpointSelection.Outputs("all_active", "", "Realtek", active).Length == 3, "all active outputs must be covered");
        Check(CaptureEndpointSelection.Outputs("all_active", "", "NVIDIA", active).Length == 3, "default change must retain all existing outputs");
        Check(CaptureEndpointSelection.Outputs("all_active", "NVIDIA", "Realtek", active)[0] == "NVIDIA", "explicit output overrides all-active mode");
        string[] selected = CaptureEndpointSelection.Outputs("default", "", "Headphones", active);
        Check(selected.Length == 1 && selected[0] == "Headphones", "default-only mode follows current default");
        Check(CaptureEndpointSelection.Outputs("default", "", "", active).Length == 0, "missing default is an empty target set, not an invented device");
        Check(CaptureEndpointSelection.Outputs("all_active", "", "", new string[] { "NVIDIA", "USB" }).Length == 2, "new output is covered without requiring a default change");
    }
    static void SuspendedLoopbackCounter(string root) {
        string file = Path.Combine(root, "silent-loopback-counter-paused.wav");
        using (var writer = new TimelineWriter(file, new WaveFormat(Rate, 16, 1))) {
            Push(writer, Packet(1, 1600), 0, 0, 160);
            Push(writer, Packet(2, 1600), 2000, 1600, 2160);
            writer.Finish(2200);
        }
        var values = Markers(file);
        Check(values.Count == 2 && Math.Abs(values[1] - 50) <= .0625 && Math.Abs(values[2] - 2050) <= .0625,
            "a loopback frame counter suspended during silence must not stretch speech over the gap");
    }
    static void StereoFloatClockAndPause(string root) {
        string file = Path.Combine(root, "float-stereo-clock-pause.wav");
        byte[] data = new byte[4800 * 8];
        for (int frame = 0; frame < 4800; frame++) {
            Buffer.BlockCopy(BitConverter.GetBytes(.25f), 0, data, frame * 8, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(-.5f), 0, data, frame * 8 + 4, 4);
        }
        using (var writer = new TimelineWriter(file, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2))) {
            writer.SetPaused(true, 150); writer.SetPaused(false, 250);
            for (int i = 0; i < 3; i++) Push(writer, data, i * 100.01, i * 4800, i * 100.01 + 160);
            writer.SetEndBoundary(300); writer.Finish(300);
        }
        using (var wave = new WaveFileReader(file)) {
            byte[] frames = new byte[14400 * 8]; Check(wave.Read(frames, 0, frames.Length) == frames.Length, "float clock correction: exact output duration");
            bool valid = true;
            for (int frame = 0; frame < 14400; frame++) {
                bool silent = frame >= 7200 && frame < 12000;
                valid &= BitConverter.ToSingle(frames, frame * 8) == (silent ? 0 : .25f);
                valid &= BitConverter.ToSingle(frames, frame * 8 + 4) == (silent ? 0 : -.5f);
            }
            Check(valid, "float clock correction must preserve independent stereo channels and pause mask");
        }
    }
    static void CaptureFailureBackoff() {
        // Exercise the production error handler without initializing COM devices,
        // touching the user's audio settings, or starting a recording.
        var type = typeof(RecordingSession);
        var sourceType = type.GetNestedType("Source", BindingFlags.NonPublic);
        var session = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
        object source = Activator.CreateInstance(sourceType, true);
        sourceType.GetField("Name").SetValue(source, "broken endpoint");
        var map = (System.Collections.IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), sourceType));
        map.Add("loopback:broken", source); map.Add("loopback:healthy", Activator.CreateInstance(sourceType, true));
        var failed = new CaptureRetryState();
        var signal = new System.Threading.AutoResetEvent(false);
        var fields = new Dictionary<string, object> {
            { "gate", new object() }, { "clock", new System.Diagnostics.Stopwatch() }, { "active", map },
            { "failedDevices", failed }, { "notices", new Queue<string>() },
            { "captureProblems", new List<string>() }, { "devicesChanged", signal }
        };
        foreach (var field in fields) type.GetField(field.Key, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(session, field.Value);
        try {
            type.GetMethod("CaptureFailed", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(session,
                new object[] { source, "loopback:broken", new IOException("persistent driver error") });
            Check(!failed.CanRetry("loopback:broken", 0), "a runtime capture failure must have a cooldown");
            Check(failed.CanRetry("loopback:broken", 3000), "a failed but still online endpoint must retry even without a Windows notification");
            Check(!map.Contains("loopback:broken") && map.Contains("loopback:healthy"), "one failed endpoint must not stop the healthy endpoint");
            Check((bool)sourceType.GetField("Ended").GetValue(source) && (string)sourceType.GetField("Error").GetValue(source) == "persistent driver error",
                "capture failure must retain the ended source and its error");
            Check(signal.WaitOne(0), "capture failure must wake the manager to finalize its source");
        } finally { signal.Dispose(); }
    }
    static void PacketTimestampJitter(string root, double jitterMs) {
        const int rate = 48000, frames = 480, packets = 200;
        string file = Path.Combine(root, "continuous-qpc-jitter-" + (jitterMs * 1000) + "us.wav");
        using (var writer = new TimelineWriter(file, WaveFormat.CreateIeeeFloatWaveFormat(rate, 1))) {
            bool reportedGap = false;
            for (int packet = 0; packet < packets; packet++) {
                byte[] data = new byte[frames * 4];
                for (int frame = 0; frame < frames; frame++) {
                    double time = (packet * frames + frame) / (double)rate;
                    float value = (float)(.08 * Math.Cos(.3 + 2 * Math.PI * (2650 * time + 175 * time * time)));
                    Buffer.BlockCopy(BitConverter.GetBytes(value), 0, data, frame * 4, 4);
                }
                double stamp = packet * 10 + (packet == 0 ? 0 : (packet % 2 == 0 ? -jitterMs : jitterMs));
                reportedGap |= writer.AddTimestamped(data, data.Length, stamp, packet * frames, false);
            }
            writer.Finish(2000);
            Check(!reportedGap, "bounded QPC jitter is not reported as a device gap");
        }
        using (var wave = new WaveFileReader(file)) {
            byte[] data = new byte[packets * frames * 4]; int read = wave.Read(data, 0, data.Length);
            Check(read == data.Length, "QPC jitter: common stop boundary");
            int zeros = 0; double cross = 0, expectedEnergy = 0, actualEnergy = 0;
            for (int frame = 4800; frame < packets * frames - 4800; frame++) {
                double time = frame / (double)rate;
                double expected = (float)(.08 * Math.Cos(.3 + 2 * Math.PI * (2650 * time + 175 * time * time)));
                double actual = BitConverter.ToSingle(data, frame * 4);
                if (actual == 0) zeros++;
                cross += expected * actual; expectedEnergy += expected * expected; actualEnergy += actual * actual;
            }
            Check(zeros == 0, "continuous frames with +/-" + jitterMs + "ms timestamp jitter must not acquire zero holes");
            Check(cross / Math.Sqrt(expectedEnergy * actualEnergy) >= .995,
                "short QPC jitter must not phase-modulate continuous high-frequency audio");
        }
    }
    static void JitterDriftAndCounterPause(string root) {
        const double speed = 1.0001, gapMs = 40;
        const int seconds = 60, frames = 160;
        string file = Path.Combine(root, "qpc-jitter-drift-and-counter-pause.wav");
        long total = (long)Math.Round(seconds * Rate * speed);
        long gapPosition = (long)Math.Round(30 * Rate * speed);
        long[] markers = { (long)Math.Round(10 * Rate * speed), (long)Math.Round(40 * Rate * speed), (long)Math.Round(59 * Rate * speed) };
        using (var writer = new TimelineWriter(file, new WaveFormat(Rate, 16, 1))) {
            int gaps = 0;
            for (long first = 0; first < total; first += frames) {
                int count = (int)Math.Min(frames, total - first); byte[] data = new byte[count * 2];
                for (int frame = 0; frame < count; frame++) Buffer.BlockCopy(BitConverter.GetBytes((short)1000), 0, data, frame * 2, 2);
                foreach (long marker in markers) if (marker >= first && marker < first + count)
                    Buffer.BlockCopy(BitConverter.GetBytes((short)12000), 0, data, (int)(marker - first) * 2, 2);
                double stamp = first * 1000.0 / Rate / speed + (first >= gapPosition ? gapMs : 0);
                if (first > 0) stamp += (first / frames % 2 == 0 ? -.4 : .4);
                if (writer.AddTimestamped(data, data.Length, stamp, first, false)) gaps++;
            }
            writer.Finish(seconds * 1000 + gapMs);
            Check(gaps == 1, "the unflagged true suspended-counter gap must be reported exactly once");
        }
        using (var wave = new WaveFileReader(file)) {
            foreach (double nominal in new double[] { 10, 40, 59 }) {
                double expected = nominal + (nominal >= 30 ? gapMs / 1000 : 0);
                long first = (long)Math.Round((expected - .005) * Rate); wave.Position = first * 2;
                byte[] data = new byte[320]; int read = wave.Read(data, 0, data.Length), best = -1, peak = 0;
                for (int i = 0; i + 1 < read; i += 2) if (BitConverter.ToInt16(data, i) > peak) { peak = BitConverter.ToInt16(data, i); best = i / 2; }
                Check(peak > 3000, "combined jitter/drift: speech marker survives");
                Check(Math.Abs((first + best) / (double)Rate - expected) <= 1.0 / Rate,
                    "combined jitter/drift/counter pause must retain one-sample marker accuracy");
            }
            wave.Position = (long)Math.Round(30.015 * Rate) * 2;
            byte[] gap = new byte[(int)Math.Round(.015 * Rate) * 2]; wave.Read(gap, 0, gap.Length);
            bool silent = true; foreach (byte value in gap) silent &= value == 0;
            Check(silent, "a genuine 40ms suspended-counter interval must remain silent with jitter and clock drift");
        }
    }
    static void SuspectClockFitIsReported(string root) {
        string file = Path.Combine(root, "unsupported-clock-fit.wav"); bool rejected = false;
        using (var writer = new TimelineWriter(file, new WaveFormat(Rate, 16, 1))) {
            for (int packet = 0; packet < 100; packet++) {
                byte[] data = new byte[160 * 2];
                writer.AddTimestamped(data, data.Length, packet * 10 * 1.006, packet * 160, false);
                rejected |= writer.ClockFitRejected;
            }
            writer.Finish(1006);
        }
        Check(rejected, "unsupported clock slope must be exposed for a bounded timing-uncertain diagnostic");
    }
    public static int Main(string[] args) {
        try {
            string folder = args.Length == 0 ? Path.Combine(Path.GetTempPath(), "TingjiTimestamped-" + Guid.NewGuid().ToString("N")) : Path.GetFullPath(args[0]);
            Directory.CreateDirectory(folder);
            double[] starts = { 0, 100, 200, 300, 400, 500, 600, 700, 800, 900 };
            Schedule(folder, "first-delivery-late-60ms", new double[] { 160,260,360,460,560,660,760,860,960,1060 }, starts, 1000);
            Schedule(folder, "split-delivery-stall-200ms", new double[] { 100,200,500,500,500,600,700,800,900,1000 }, starts, 1000);
            Schedule(folder, "real-silence-40ms", new double[] { 100,240,380 }, new double[] { 0,140,280 }, 420);
            PausedDelayedPackets(folder);
            SampleClock(folder, "fast-clock-100ppm-30min", 1.0001, 1800);
            SampleClock(folder, "slow-clock-100ppm-30min", .9999, 1800);
            SampleClock(folder, "fast-clock-100ppm-1hour", 1.0001, 3600);
            EndpointSelection();
            SuspendedLoopbackCounter(folder);
            StereoFloatClockAndPause(folder);
            CaptureFailureBackoff();
            PacketTimestampJitter(folder, .04);
            PacketTimestampJitter(folder, .15);
            PacketTimestampJitter(folder, .4);
            JitterDriftAndCounterPause(folder);
            SuspectClockFitIsReported(folder);
            Check(timestamped != null, "production timestamped entry point is required");
            Console.WriteLine("Timestamped timeline: " + assertions + " assertions passed; " + folder); return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
