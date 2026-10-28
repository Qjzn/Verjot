using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

internal static class TestRecorderDockMorph {
    static int assertions;
    static void Check(bool value, string message) { assertions++; if (!value) throw new InvalidOperationException(message); }
    static object Field(object value, string name) { return value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(value); }
    static void Pump(int ms) { var clock = Stopwatch.StartNew(); while (clock.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(2); } }
    static void WaitForPartial(RecorderPanel panel) {
        var clock=Stopwatch.StartNew();
        while ((float)Field(panel,"dockProgress") <= .05f && clock.ElapsedMilliseconds<200) Pump(2);
        float progress=(float)Field(panel,"dockProgress");
        Check(progress>.05f && progress<.95f,"Partial-drag fixture must actually observe an intermediate frame");
    }
    static void WaitForRest(RecorderPanel panel) {
        var timer=(System.Windows.Forms.Timer)Field(panel,"dockAnimation"); var clock=Stopwatch.StartNew();
        while(timer.Enabled && clock.ElapsedMilliseconds<450) Pump(2);
        Check(!timer.Enabled,"Morph reaches its exact rest state within bounded native scheduling allowance");
    }
    static void SpringChecks() {
        var spring = new RecorderDockSpring(40); double previous = spring.Value;
        for (int i = 0; i < 60; i++) {
            spring.Step(186, 38, 1.0 / 240);
            Check(spring.Value >= previous && spring.Value <= 186, "Expansion decelerates without overshooting the panel"); previous = spring.Value;
        }
        Check(spring.Value == 186 && spring.Velocity == 0, "Expansion settles exactly within 250 ms at 240 Hz");
        var collapse = new RecorderDockSpring(186); collapse.Step(40,46,.22);
        Check(collapse.Value==40 && collapse.Velocity==0,"Ideal collapse curve settles in 220 ms, faster than expansion");
        var left = new RecorderDockSpring(8); left.Step(-83,38,.25);
        Check(left.Value==-83,"Negative logo offsets settle using pixel tolerances rather than normalized-opacity tolerances");
        var slow = new RecorderDockSpring(40); var fast = new RecorderDockSpring(40);
        slow.Step(186, 38, .064); for (int i = 0; i < 4; i++) fast.Step(186, 38, .016);
        Check(Math.Abs(slow.Value - fast.Value) < .00001 && Math.Abs(slow.Velocity - fast.Velocity) < .00001, "The curve is elapsed-time based, independent of timer frequency");
        double position = slow.Value, velocity = slow.Velocity; slow.Step(40, 46, 0);
        Check(slow.Value == position && slow.Velocity == velocity, "Retargeting retains position and velocity at the interruption");
        slow.Step(40, 46, .25); Check(slow.Value == 40, "Reversal settles without a second expansion or long tail");
        slow.Step(186, 38, 3); Check(slow.Value == 186 && slow.Velocity == 0, "A delayed UI frame settles rather than accumulating catch-up frames");
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    static Bitmap Frame(RecorderPanel panel) {
        var image = new Bitmap(panel.Width, panel.Height);
        using (var graphics = Graphics.FromImage(image)) { var dc = graphics.GetHdc(); try { Check(PrintWindow(panel.Handle, dc, 0), "Capture the real native morph surface"); } finally { graphics.ReleaseHdc(dc); } }
        return image;
    }
    static void NativeChecks(string output) {
        foreach (Screen screen in Screen.AllScreens) using (var panel = new RecorderPanel()) {
            panel.EnableSharedDock(); panel.ShowStatus("verbit", "ready"); panel.Show(); Pump(60);
            int unit = (int)Math.Round(40 * panel.DeviceDpi / 96.0);
            panel.Location = new Point(screen.WorkingArea.Left + 120, screen.WorkingArea.Top + 120); Pump(60);
            unit = (int)Math.Round(40 * panel.DeviceDpi / 96.0);
            IntPtr handle = panel.Handle;
            var timer = (System.Windows.Forms.Timer)Field(panel, "dockAnimation");
            panel.ShowAttention("转写失败 · 可重试", "原录音已保留"); panel.SetDockExpanded(true);
            Check(timer.Enabled && !panel.QuickNoteButton.Visible, "Only the raster morph runs during expansion, native controls do not flash");
            int firstWidth = panel.Width; Pump(48);
            Check(panel.Width > firstWidth && panel.Width < unit * 4.65, "Expansion contains intermediate native sizes");
            var radius = (RecorderDockSpring)Field(panel, "dockRadius");
            Check(radius.Value > 12 && radius.Value < 20, "Corner radius morphs continuously from capsule to panel");
            Check(panel.BrandTitle.Left >= (int)Math.Round(83 * panel.DeviceDpi / 96.0), "Snapshot brand title uses target-DPI layout before the logo reaches its header");
            var snapshot = (Bitmap)Field(panel, "dockSnapshot"); int ink = 0;
            Check(snapshot != null, "Content is rasterized once for cross-fade");
            for (int y = 0; y < snapshot.Height; y++) for (int x = 40; x < snapshot.Width; x++) {
                Color color = snapshot.GetPixel(x, y); if (color.B > color.R + 10) ink++;
            }
            Check(ink > 20, "Transition snapshot contains actual native text and icons, not an empty white panel");
            using (var sheet = new Bitmap(unit * 5 * 5, unit * 2 + 24)) using (var graphics = Graphics.FromImage(sheet)) {
                graphics.Clear(Color.FromArgb(241, 245, 250));
                for (int i = 0; i < 5; i++) { using (var frame = Frame(panel)) graphics.DrawImageUnscaled(frame, i * unit * 5, 12); Pump(40); }
                sheet.Save(Path.Combine(output, "morph-frames-" + panel.DeviceDpi + ".png"));
            }
            WaitForRest(panel);
            Check(!timer.Enabled && panel.QuickNoteButton.Visible && panel.Handle == handle, "Settled panel restores interactive native controls in the same HWND");
            Check(Field(panel, "dockSnapshot") == null, "Transient bitmap is released at rest");
            panel.SetDockExpanded(false); Pump(48); double before = ((RecorderDockSpring)Field(panel, "dockWidth")).Value;
            panel.SetDockExpanded(true); double after = ((RecorderDockSpring)Field(panel, "dockWidth")).Value;
            Check(before == after && timer.Enabled, "Rapid reentry retargets the current shape without restarting at the logo"); WaitForRest(panel);
            panel.ShowStatus("verbit", "ready"); panel.SetDockExpanded(true); WaitForRest(panel);
            Check(panel.Width == unit * 4 && panel.Height == unit, "Panel-to-toolbar change also reaches capsule geometry");
            panel.SetDockExpanded(false); WaitForRest(panel);
            Check(panel.Width == unit && !timer.Enabled, "Collapse is shorter than expansion and leaves no running frame timer");
            panel.Location = new Point(screen.WorkingArea.Right - unit - 4, screen.WorkingArea.Bottom - unit - 4);
            Point anchor = panel.Location; panel.ShowAttention("测试", "ready"); panel.SetDockExpanded(true);
            for (int i = 0; i < 10; i++) { Pump(24); Check(screen.WorkingArea.Contains(panel.Bounds), "Every edge-morph frame stays on the monitor"); }
            panel.SetDockExpanded(false); WaitForRest(panel); Check(panel.Location == anchor, "Edge morph preserves its original logo anchor");
            panel.SetDockExpanded(true); Pump(40); panel.Hide();
            Check(!timer.Enabled && Field(panel, "dockSnapshot") == null, "Hidden window stops morphing and releases transient content");
            RecorderMotionPreferences.Mode = "off"; panel.Show(); panel.SetDockExpanded(false);
            Check(panel.Width == unit && !timer.Enabled, "Motion-off mode changes state immediately without a timer");
            panel.SetDockExpanded(true); Check(panel.QuickNoteButton.Visible && !timer.Enabled, "Motion-off retains all actions and state feedback");
            RecorderMotionPreferences.Mode = "on"; panel.AllowClose = true; panel.Close();
            Console.WriteLine("PASS fluid actual-monitor=" + screen.DeviceName + "; dpi=" + panel.DeviceDpi);
        }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wp, IntPtr lp);
    static void Event(Control control, string name) { typeof(Control).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(control, new object[] { EventArgs.Empty }); }
    static PointF LogoCenter(RecorderPanel panel, Rectangle area) {
        using (var frame = Frame(panel)) {
            double xSum=0, ySum=0; int count=0;
            for (int y=area.Top;y<Math.Min(frame.Height,area.Bottom);y++) for (int x=area.Left;x<Math.Min(frame.Width,area.Right);x++) {
                Color c=frame.GetPixel(x,y);
                if(c.G>150 && c.G>c.R+60 && c.B-c.G<65 && c.B>c.R+70) { count++; xSum+=x; ySum+=y; }
            }
            Check(count>4,"The real frame contains a visible cyan brand mark");
            return new PointF((float)(xSum/count),(float)(ySum/count));
        }
    }
    static void ReviewChecks(string output) {
        foreach (Screen screen in Screen.AllScreens) using (var panel = new RecorderPanel()) {
            panel.EnableSharedDock(); panel.ShowStatus("verbit", "ready"); panel.Show(); Pump(70);
            Point point = new Point(screen.WorkingArea.Left + 140, screen.WorkingArea.Top + 140);
            if (new Rectangle(point, new Size(500, 220)).Contains(Cursor.Position)) point.Y = screen.WorkingArea.Bottom - 260;
            panel.Location = point; Pump(60); IntPtr handle = panel.Handle;
            var timer = (System.Windows.Forms.Timer)Field(panel, "dockAnimation");
            panel.SetDockExpanded(true); WaitForPartial(panel);
            Rectangle before = panel.Bounds; float progress = (float)Field(panel, "dockProgress");
            SendMessage(handle, 0x231, IntPtr.Zero, IntPtr.Zero);
            Check(panel.Bounds == before && Math.Abs((float)Field(panel, "dockProgress") - progress) < .001, "Starting a drag in mid-morph must freeze the current shape without snapping to the end");
            Check(!timer.Enabled, "Native drag loop must not compete with a running frame timer");
            using (var frame = Frame(panel)) frame.Save(Path.Combine(output, "drag-frozen-" + panel.DeviceDpi + ".png"));
            panel.Location = new Point(before.Left + 37, before.Top + 29); Rectangle moved = panel.Bounds;
            Pump(45); Check(panel.Bounds == moved, "Holding a drag preserves its position and frozen shape");
            SendMessage(handle, 0x232, IntPtr.Zero, IntPtr.Zero);
            Check(panel.Bounds == moved, "Ending a drag resumes from the visible bounds rather than jumping to its target");
            Event(panel,"OnMouseEnter"); WaitForRest(panel); Check(!timer.Enabled && panel.Handle == handle && panel.QuickNoteButton.Visible, "Resumed drag settles with the original interactive window");
            // Exercise actual managed hover handlers; never move the user's pointer.
            for (int i = 0; i < 18; i++) {
                Event(panel, "OnMouseEnter"); Pump(12 + i % 4 * 7);
                Event(panel, "OnMouseLeave"); Pump(14 + i % 3 * 5);
                Check(panel.Handle == handle && screen.WorkingArea.Contains(panel.Bounds), "Rapid hover keeps one bounded native surface");
            }
            for (int i=0; i<6; i++) {
                Event(panel,"OnMouseLeave"); Pump(235);
                double position = ((RecorderDockSpring)Field(panel,"dockWidth")).Value;
                Event(panel,"OnMouseEnter");
                Check(Math.Abs(((RecorderDockSpring)Field(panel,"dockWidth")).Value-position)<.001,"Reentering during actual leave-triggered collapse retains the current shape"); Pump(40);
            }
            Event(panel,"OnMouseLeave");
            var leaveClock=Stopwatch.StartNew();
            while (((bool)Field(panel,"dockExpanded") || timer.Enabled) && leaveClock.ElapsedMilliseconds<750) Pump(2);
            Console.WriteLine("Leave settle total ms="+leaveClock.ElapsedMilliseconds);
            Console.WriteLine("Final leave expanded="+Field(panel,"dockExpanded")+"; timer="+timer.Enabled+"; leave="+((System.Windows.Forms.Timer)Field(panel,"dockLeave")).Enabled+"; pinned="+Field(panel,"dockPinned")+"; width="+panel.Width+"; cursor="+Cursor.Position+"; bounds="+panel.Bounds);
            Check(!timer.Enabled && !(bool)Field(panel, "dockExpanded"), "Final real leave handler collapses and stops its timer");
            panel.SetDockExpanded(true); WaitForPartial(panel);
            SendMessage(handle,0x231,IntPtr.Zero,IntPtr.Zero); Event(panel,"OnMouseLeave"); Pump(260);
            SendMessage(handle,0x232,IntPtr.Zero,IntPtr.Zero); Pump(550);
            Check(!(bool)Field(panel,"dockExpanded") && !timer.Enabled,"Leaving during a held drag is re-evaluated after release, rather than leaving the toolbar stuck open");
            // A full strip becoming a recording panel must move its shared logo,
            // rather than swapping it to the new header on the first frame.
            panel.SetDockExpanded(true); WaitForRest(panel); Rectangle logoBounds = panel.BrandMark.Bounds; PointF logo = LogoCenter(panel, logoBounds);
            using (var frame = Frame(panel)) frame.Save(Path.Combine(output, "strip-before-" + panel.DeviceDpi + ".png"));
            panel.ShowRecording("Synthetic mic", "Synthetic system");
            PointF firstLogo = LogoCenter(panel, logoBounds);
            Console.WriteLine("Logo transition before="+logo+"; after="+firstLogo+"; width="+panel.Width+"; scalar="+((RecorderDockSpring)Field(panel,"dockLogoX")).Value);
            using (var frame = Frame(panel)) frame.Save(Path.Combine(output, "strip-to-panel-first-" + panel.DeviceDpi + ".png"));
            Check(Math.Abs(firstLogo.X-logo.X)<2 && Math.Abs(firstLogo.Y-logo.Y)<2,"Strip-to-panel keeps the logo in the same position on the first frame");
            WaitForRest(panel); Check(panel.MicrophoneButton.Visible && panel.StopButton.Visible, "Recording controls return after a profile morph");
            panel.SetDockExpanded(false); WaitForRest(panel);
            int unit = (int)Math.Round(40 * panel.DeviceDpi / 96.0);
            foreach (Point corner in new[] { new Point(screen.WorkingArea.Left, screen.WorkingArea.Top), new Point(screen.WorkingArea.Right-unit, screen.WorkingArea.Top), new Point(screen.WorkingArea.Left,screen.WorkingArea.Bottom-unit), new Point(screen.WorkingArea.Right-unit,screen.WorkingArea.Bottom-unit) }) {
                panel.Location = corner; panel.SetDockExpanded(true);
                for (int i=0; i<14; i++) { Pump(20); Check(screen.WorkingArea.Contains(panel.Bounds), "All four screen edges contain every sampled expansion frame"); }
                panel.SetDockExpanded(false); WaitForRest(panel); Check(panel.Location == corner, "Every corner returns to its exact anchor");
            }
            foreach (Screen destination in Screen.AllScreens) if (destination.DeviceName != screen.DeviceName) {
                foreach (string state in new[] {"collapsed","expanded","partial"}) {
                    panel.SetDockExpanded(false); WaitForRest(panel);
                    if(state!="collapsed") { panel.SetDockExpanded(true); if(state=="partial") WaitForPartial(panel); else WaitForRest(panel); }
                    SendMessage(handle, 0x231, IntPtr.Zero, IntPtr.Zero);
                    panel.Location = new Point(destination.WorkingArea.Left + 140, destination.WorkingArea.Top + 140); Pump(30);
                    Check(Screen.FromControl(panel).DeviceName == destination.DeviceName, "Cross-DPI native dragging must not jump back to the old monitor: " + state);
                    Check(panel.ClientRectangle.Contains(panel.BrandMark.Bounds) || !panel.BrandMark.Visible,"Frozen DPI-scaled logo remains inside the visible surface: " + state);
                    var content=(Panel)Field(panel,"dockContent");
                    foreach(Control control in content.Controls) if(control.Visible) Check(panel.ClientRectangle.Contains(control.Bounds),"Frozen destination-DPI controls fit before releasing the drag: "+control.GetType().Name);
                    Rectangle frozen=panel.Bounds;
                    SendMessage(handle, 0x232, IntPtr.Zero, IntPtr.Zero);
                    Console.WriteLine("Cross drag="+state+"; frozen="+frozen+"; released="+panel.Bounds+"; dpi="+panel.DeviceDpi+"; minimum="+panel.MinimumSize);
                    Check(panel.Bounds==frozen,"Cross-DPI release preserves its current rectangle: "+state);
                    WaitForRest(panel);
                    Check(destination.WorkingArea.Contains(panel.Bounds) && panel.Handle == handle, "Cross-DPI drag finishes inside the destination monitor: " + state);
                    panel.Location = point; Pump(50);
                }
            }
            var layout = typeof(RecorderPanel).GetMethod("ApplyDpiLayout", BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (int dpi in new[] {96,120,144,168,192,240}) {
                layout.Invoke(panel, new object[] {dpi}); panel.SetDockExpanded(true); WaitForRest(panel);
                Check(panel.Width == (int)Math.Round(186 * dpi / 96.0) && panel.Height == (int)Math.Round(68 * dpi / 96.0), "Simulated logical metrics retain panel bounds at " + dpi);
                panel.SetDockExpanded(false); WaitForRest(panel);
                Check(panel.Width == (int)Math.Round(40 * dpi / 96.0) && panel.Height == panel.Width, "Simulated logical metrics retain logo bounds at " + dpi);
            }
            layout.Invoke(panel, new object[] {panel.DeviceDpi});
            panel.SetDockExpanded(true); WaitForRest(panel);
            var image = Field(panel, "dockLogo"); var region = panel.Region;
            for (int i=0; i<20; i++) panel.ShowRecording("Synthetic mic", "Synthetic system");
            Check(Field(panel, "dockSnapshot") == null && Object.ReferenceEquals(image, Field(panel, "dockLogo")) && Object.ReferenceEquals(region,panel.Region), "Repeated status polling does not allocate a bitmap or region or restart the morph");
            panel.AllowClose = true; panel.Close();
            Console.WriteLine("PASS motion review actual-monitor=" + screen.DeviceName);
        }
    }
    [STAThread] static int Main(string[] args) {
        string previous = RecorderMotionPreferences.Mode;
        try { Application.EnableVisualStyles(); RecorderMotionPreferences.Mode = "on"; SpringChecks(); if (Array.IndexOf(args, "--review-only") >= 0) ReviewChecks(AppDomain.CurrentDomain.BaseDirectory); else { NativeChecks(AppDomain.CurrentDomain.BaseDirectory); ReviewChecks(AppDomain.CurrentDomain.BaseDirectory); } Console.WriteLine("PASS fluid morph assertions=" + assertions); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { RecorderMotionPreferences.Mode = previous; }
    }
}
