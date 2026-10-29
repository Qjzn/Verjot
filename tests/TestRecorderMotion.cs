using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

internal static class TestRecorderMotion {
    static int assertions;
    static void Check(bool condition, string message) {
        assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }
    static object Field(object control, string name) {
        FieldInfo field = control.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        if (field == null) throw new InvalidOperationException("Missing motion state: " + control.GetType().Name + "." + name);
        return field.GetValue(control);
    }
    static System.Windows.Forms.Timer Animation(object control) {
        return (System.Windows.Forms.Timer)Field(control, "animation");
    }
    static void Event(Control control, string name, EventArgs args) {
        MethodInfo method = control.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Check(method != null, "Missing native event: " + name);
        method.Invoke(control, new object[] { args });
    }
    static void Pump(int milliseconds) {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(5); }
        Application.DoEvents();
    }
    static void PumpUntil(Func<bool> condition, string message) {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < 400) { Application.DoEvents(); Thread.Sleep(2); }
        Check(condition(), message);
    }
    static int ColorDistance(Color a, Color b) { return Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B); }
    static Color Fill(RecorderActionButton button) { return (Color)Field(button, "visualFill"); }
    static double Level(RecordingLevelIndicator indicator) { return (double)Field(indicator, "level"); }
    static bool MotionEnabled() {
        Type preference = typeof(RecorderActionButton).Assembly.GetType("RecorderMotionPreferences");
        Check(preference != null, "Native animation preferences must be shared by recording and processing feedback");
        return (bool)preference.GetProperty("Enabled", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).GetValue(null, null);
    }
    [STAThread]
    static int Main() {
        try {
            Application.EnableVisualStyles();
            RecorderMotionPreferences.Mode = "auto";
            bool automaticMotion = MotionEnabled();
            RecorderMotionPreferences.Mode = "on";
            Check(MotionEnabled(), "Explicit on must enable motion independently of the Windows preference");
            using (var form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-2000, -2000), ClientSize = new Size(120, 60) })
            using (var button = new RecorderActionButton { Size = new Size(36, 36), BackColor = Color.White })
            using (var indicator = new RecordingLevelIndicator { Location = new Point(50, 0), Size = new Size(18, 24), BackColor = Color.White }) {
                form.Controls.Add(button);
                form.Controls.Add(indicator);
                form.Show();
                Pump(25);
                var buttonTimer = Animation(button);
                var waveTimer = Animation(indicator);
                bool motion = MotionEnabled();
                Check(buttonTimer.Interval >= 30 && buttonTimer.Interval <= 34 && waveTimer.Interval >= 30 && waveTimer.Interval <= 34, "Motion must stay near 30 fps in the small controls");
                Check(!buttonTimer.Enabled && !waveTimer.Enabled, "Idle controls must not run frame timers");
                Color resting = Fill(button);
                Event(button, "OnMouseEnter", EventArgs.Empty);
                if (motion) {
                    Check(buttonTimer.Enabled && Fill(button) == resting, "Hover must start from the current appearance");
                    PumpUntil(delegate { return Fill(button) != resting; }, "Hover must produce its first visible frame");
                    Color firstFrame = Fill(button);
                    Check(firstFrame != resting, "Hover must render an intermediate frame");
                    PumpUntil(delegate { return Fill(button) != firstFrame; }, "Hover must produce a later visible frame");
                    Color secondFrame = Fill(button);
                    Check(secondFrame != firstFrame && buttonTimer.Enabled, "Hover must use multiple frames over its short duration");
                    Event(button, "OnMouseLeave", EventArgs.Empty);
                    Check(ColorDistance(Fill(button), secondFrame) < 16, "Interrupting hover must continue from its current appearance");
                    Pump(220);
                    Check(Fill(button) == resting && !buttonTimer.Enabled, "Interrupted hover must finish at the new target and stop its timer");
                } else {
                    Check(!buttonTimer.Enabled && Fill(button) != resting, "Reduced motion must update hover feedback immediately");
                    Event(button, "OnMouseLeave", EventArgs.Empty);
                }
                using (var action = new RecorderActionButton { QuickAction = "note", Size = new Size(32,32), BackColor = Color.White, Location = new Point(80,0) }) {
                    form.Controls.Add(action); action.Show();
                    Event(action,"OnMouseEnter",EventArgs.Empty); Pump(220);
                    Color entered; using(var bitmap=new Bitmap(32,32)) { action.DrawToBitmap(bitmap,action.ClientRectangle); entered=bitmap.GetPixel(3,16); }
                    Event(action,"OnMouseLeave",EventArgs.Empty);
                    Color leaving; using(var bitmap=new Bitmap(32,32)) { action.DrawToBitmap(bitmap,action.ClientRectangle); leaving=bitmap.GetPixel(3,16); }
                    Check(ColorDistance(entered,leaving)<16 && leaving.ToArgb()!=action.BackColor.ToArgb(),"Quick-action background release retains the painted intermediate frame instead of disappearing on leave");
                    Pump(220);
                    using(var bitmap=new Bitmap(32,32)) { action.DrawToBitmap(bitmap,action.ClientRectangle); Check(bitmap.GetPixel(3,16).ToArgb()==action.BackColor.ToArgb() && !Animation(action).Enabled,"Quick action settles to the white surface and stops its timer"); }
                }
                int clicks = 0;
                button.Click += delegate { clicks++; };
                Event(button, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 18, 18, 0));
                button.PerformClick();
                Check(clicks == 1, "A press transition must never delay or consume the click");
                Event(button, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 18, 18, 0));
                Pump(220);
                button.SetInputLevel(.6f);
                Check(button.InputActive, "Microphone semantics must react to actual input immediately");
                if (motion) {
                    Check((float)Field(button, "visualInputLevel") == 0 && buttonTimer.Enabled, "Input fill must ease in from silence");
                    PumpUntil(delegate { return (float)Field(button, "visualInputLevel") > 0; }, "Input fill must produce its first visible frame");
                    Check((float)Field(button, "visualInputLevel") > 0 && (float)Field(button, "visualInputLevel") < 1, "Input fill must render an intermediate level");
                }
                Pump(220);
                button.MicrophoneDisabled = true;
                Check(!button.InputActive, "Disabling the mic must immediately clear its input state");
                Pump(220);
                Check((float)Field(button, "visualInputLevel") == 0 && !buttonTimer.Enabled, "Disabled mic must settle without a frame loop");
                button.MicrophoneDisabled = false;
                button.SetInputLevel(.5f);
                button.RecordingPaused = true;
                Check(!button.InputActive, "Whole pause must immediately clear microphone input");
                Pump(220);
                Check((float)Field(button, "visualInputLevel") == 0, "Whole pause must settle to an empty microphone");
                button.RecordingPaused = false;
                Event(button, "OnMouseEnter", EventArgs.Empty);
                button.Hide();
                Check(!buttonTimer.Enabled, "A hidden button must stop its timer immediately");
                button.Show();
                Check(!buttonTimer.Enabled, "Showing a settled button must not restart animation");
                indicator.SetRecording(true);
                indicator.SetLevels(0, 0);
                Check(Level(indicator) == 0 && !waveTimer.Enabled, "Real silence must remain still without an animation loop");
                indicator.SetLevels(.6f, 0);
                Check(indicator.AccessibleName.Contains("有声音"), "Input accessibility feedback must not wait for interpolation");
                if (motion) {
                    Check(Level(indicator) == 0 && waveTimer.Enabled, "A real input peak must animate from the previous measured level");
                    PumpUntil(delegate { return Level(indicator) > 0; }, "Wave attack must produce its first visible frame");
                    double firstLevel = Level(indicator);
                    Check(firstLevel > 0 && firstLevel < 1, "Wave attack must render an intermediate frame");
                    PumpUntil(delegate { return Level(indicator) > firstLevel; }, "Wave attack must produce a later visible frame");
                    Check(Level(indicator) > firstLevel, "Wave attack must continue toward the measured peak");
                }
                Pump(600);
                Check(Level(indicator) > .8 && !waveTimer.Enabled, "A steady signal must settle and stop the frame timer");
                indicator.SetLevels(0, 0);
                if (motion) {
                    double previousLevel = Level(indicator);
                    PumpUntil(delegate { return Level(indicator) < previousLevel; }, "Measured silence must produce a release frame");
                    Check(Level(indicator) > 0 && Level(indicator) < previousLevel, "Measured silence must release smoothly");
                }
                Pump(1200);
                Check(Level(indicator) == 0 && !waveTimer.Enabled && indicator.AccessibleName.Contains("暂无明显声音"), "Silence must reach exact zero without invented waveform activity");
                indicator.SetLevels(0, .6f);
                Pump(600);
                Check(Level(indicator) > .8, "Computer-only audio must still drive the measured waveform");
                indicator.SetRecording(false);
                Check(Level(indicator) == 0 && !waveTimer.Enabled && indicator.AccessibleName.Contains("已暂停"), "Whole pause must synchronously zero the waveform and stop animation");
                indicator.SetLevels(.6f, .6f);
                Check(Level(indicator) == 0 && !waveTimer.Enabled, "Paused input must not restart the waveform");
                indicator.SetRecording(true);
                indicator.SetLevels(.5f, 0);
                indicator.Hide();
                Check(!waveTimer.Enabled, "A hidden waveform must stop its timer immediately");
                indicator.Show();
                Check(!waveTimer.Enabled, "Showing a settled waveform must wait for a new real input sample");
                Event(button, "OnMouseLeave", EventArgs.Empty);
                indicator.SetLevels(0, 0);
                Check(buttonTimer.Enabled && waveTimer.Enabled, "Explicit on must animate both controls before a preference change");
                RecorderMotionPreferences.Mode = "off";
                Check(!MotionEnabled(), "Explicit off must disable motion independently of the Windows preference");
                Event(button, "OnMouseEnter", EventArgs.Empty);
                Check(!buttonTimer.Enabled && Fill(button) == (Color)Field(button, "targetFill"), "Off must interrupt active hover and apply its new appearance immediately");
                indicator.SetLevels(.2f, 0);
                Check(Level(indicator) > 0 && !waveTimer.Enabled, "Off must interrupt active waveform motion and show measured input immediately");
                button.SetInputLevel(.4f);
                Check(button.InputActive && (float)Field(button, "visualInputLevel") > 0 && !buttonTimer.Enabled, "Off must preserve immediate measured microphone feedback");
                button.MicrophoneDisabled = true;
                Check(!button.InputActive && (float)Field(button, "visualInputLevel") == 0 && !buttonTimer.Enabled, "Off must apply microphone disabled feedback without a timer");
                indicator.SetLevels(0, 0);
                Check(Level(indicator) == 0 && !waveTimer.Enabled, "Off must apply measured silence immediately");
                indicator.SetLevels(0, .6f);
                Check(Level(indicator) > .8 && !waveTimer.Enabled, "Off must retain measured computer-only feedback without motion");
                button.PerformClick();
                Check(clicks == 2, "Off must preserve immediate click behavior");
                RecorderMotionPreferences.Mode = "auto";
                Check(MotionEnabled() == automaticMotion, "Auto must resume the unchanged native Windows preference after an override");
                form.Hide();
                Check(!buttonTimer.Enabled && !waveTimer.Enabled, "Hiding the parent must stop both controls");
                button.Dispose();
                indicator.Dispose();
                Check(!buttonTimer.Enabled && !waveTimer.Enabled, "Disposing controls must stop and dispose both frame timers");
                form.Close();
                Console.WriteLine("PASS: " + assertions + " native motion assertions; on/off exercised; Windows auto=" + automaticMotion + "; synthetic levels, no audio capture");
            }
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { RecorderMotionPreferences.Mode = "auto"; }
    }
}
