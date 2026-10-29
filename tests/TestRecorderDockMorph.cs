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
        // Drive one real timer callback at a known elapsed time for interrupted-
        // drag contracts. A busy Windows scheduler may legitimately deliver its
        // first native frame after the animation has already settled. Native
        // multi-frame rendering is covered separately in NativeChecks.
        typeof(RecorderPanel).GetField("dockLastTick",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(panel,unchecked(Environment.TickCount-32));
        typeof(System.Windows.Forms.Timer).GetMethod("OnTick",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(Field(panel,"dockAnimation"),new object[]{EventArgs.Empty});
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
    static int CaptureFromProcess(string[] args) {
        using (var image=new Bitmap(int.Parse(args[2]),int.Parse(args[3]))) {
            using(var graphics=Graphics.FromImage(image)) {
                IntPtr dc=graphics.GetHdc();
                try { if(!PrintWindow(new IntPtr(long.Parse(args[1])),dc,0)) return 2; }
                finally { graphics.ReleaseHdc(dc); }
            }
            int cyan=0;
            for(int y=0;y<image.Height;y++) for(int x=0;x<image.Width;x++) {
                Color c=image.GetPixel(x,y);
                if(c.R<120 && c.G>120 && c.B>130 && c.G>c.R+30) cyan++;
            }
            return cyan>=5 ? 0 : 3;
        }
    }
    static void ExternalCapture(RecorderPanel panel) {
        Pump(50); // Capture a presented native tree, not just a stopped timer.
        Console.WriteLine("External capture begin: mode="+RecorderMotionPreferences.Mode+"; bounds="+panel.Bounds);
        var start=new ProcessStartInfo(Application.ExecutablePath,"--capture-window "+panel.Handle.ToInt64()+" "+panel.Width+" "+panel.Height) {UseShellExecute=false,CreateNoWindow=true};
        using(var child=Process.Start(start)) {
            var clock=Stopwatch.StartNew();
            while(!child.HasExited && clock.ElapsedMilliseconds<5000) Pump(2);
            if(!child.HasExited) { child.Kill(); child.WaitForExit(); throw new InvalidOperationException("Owned capture fixture timed out"); }
            Check(child.ExitCode==0,"External-process PrintWindow retains visible Logo pixels rather than a blank white surface; code="+child.ExitCode);
            Console.WriteLine("External capture passed");
        }
    }
    static Point CapsuleAtLogo(RecorderPanel panel) {
        int pad = Math.Max(4, (int)Math.Round(40 * panel.DeviceDpi / 96.0) / 5);
        return new Point(panel.Left + panel.BrandMark.Left - pad, panel.Top + panel.BrandMark.Top - pad);
    }
    static PointF WorldLogo(RecorderPanel panel) {
        var timer=(System.Windows.Forms.Timer)Field(panel,"dockAnimation");
        bool running=timer.Enabled;
        Point location=panel.Location;
        Rectangle area=panel.BrandMark.Bounds;
        if(running) {
            float scale=(int)Field(panel,"fontDpi")/96f;
            float x=((bool)Field(panel,"dockLeft")?panel.Width-(int)Math.Round(40*scale):0)+(float)((RecorderDockSpring)Field(panel,"dockLogoX")).Value*scale;
            float y=((bool)Field(panel,"dockUp")?panel.Height-(int)Math.Round(40*scale):0)+(float)((RecorderDockSpring)Field(panel,"dockLogoY")).Value*scale;
            area=Rectangle.Round(new RectangleF(x,y,24*scale,24*scale));
        }
        PointF point = LogoCenter(panel, area);
        Check(panel.Location==location,"A captured native frame has one consistent screen rectangle");
        return new PointF(location.X + point.X, location.Y + point.Y);
    }
    static void LogoAnchorChecks(string output) {
        foreach (Screen screen in Screen.AllScreens) using (var panel = new RecorderPanel()) {
            panel.EnableSharedDock(); panel.ShowStatus("verbit", "ready"); panel.Show(); Pump(60);
            panel.Location = new Point(screen.WorkingArea.Left + 300, screen.WorkingArea.Top + 200); Pump(60);
            var timer = (System.Windows.Forms.Timer)Field(panel,"dockAnimation");
            PointF origin = WorldLogo(panel); IntPtr handle = panel.Handle;
            IntPtr logoHandle=panel.BrandMark.Handle; Image logoImage=panel.BrandMark.Image; int logoVisibility=0;
            panel.BrandMark.VisibleChanged+=delegate { logoVisibility++; };
            double drift = 0; int frames = 0;
            for (int cycle = 0; cycle < 4; cycle++) {
                foreach (bool expanded in new[] { true, false }) {
                    Rectangle before = panel.Bounds;
                    panel.SetDockExpanded(expanded);
                    Check(panel.Bounds == before,"Retargeting preserves the first native rectangle at the Logo");
                    Check(panel.BrandMark.Visible && panel.BrandMark.Handle==logoHandle,"Native Logo never disappears at transition start");
                    var clock = Stopwatch.StartNew();
                    bool capturedMiddle=false;
                    do {
                        Pump(12); PointF point = WorldLogo(panel); frames++;
                        Check(panel.BrandMark.Visible && panel.BrandMark.Handle==logoHandle && Object.ReferenceEquals(logoImage,panel.BrandMark.Image),"Each presented frame retains the same native Logo and image");
                        float progress=(float)Field(panel,"dockProgress");
                        if(cycle==0 && expanded && !capturedMiddle && progress>.65f && progress<.96f) {
                            using(var frame=Frame(panel)) frame.Save(Path.Combine(output,"logo-origin-middle-"+panel.DeviceDpi+".png"));
                            capturedMiddle=true;
                        }
                        drift = Math.Max(drift, Math.Max(Math.Abs(point.X-origin.X),Math.Abs(point.Y-origin.Y)));
                        if(Math.Abs(point.X-origin.X)>2 || Math.Abs(point.Y-origin.Y)>2) {
                            Console.WriteLine("Logo drift cycle="+cycle+" expanded="+expanded+" origin="+origin+" pixel="+point+" bounds="+panel.Bounds+" anchor="+Field(panel,"dockAnchor")+" shift="+((RecorderDockSpring)Field(panel,"dockEdgeShift")).Value+" local="+((RecorderDockSpring)Field(panel,"dockLogoX")).Value+" dpi="+panel.DeviceDpi);
                            using(var failed=Frame(panel)) failed.Save(Path.Combine(output,"logo-drift.png"));
                        }
                        Check(Math.Abs(point.X-origin.X)<=2 && Math.Abs(point.Y-origin.Y)<=2,"Painted Logo remains the origin throughout native open/close frames");
                        Check(screen.WorkingArea.Contains(panel.Bounds),"Logo-origin morph stays inside the actual monitor");
                    } while(timer.Enabled && clock.ElapsedMilliseconds<450);
                    Check(!timer.Enabled && panel.Handle==handle,"Logo-origin morph settles in the shared HWND and stops its timer");
                    if(cycle==0 && expanded) using(var frame=Frame(panel)) frame.Save(Path.Combine(output,"logo-origin-expanded-"+panel.DeviceDpi+".png"));
                }
            }
            for(int i=0;i<12;i++) {
                Rectangle before=panel.Bounds; panel.ToggleDockFromLogo();
                Check(panel.Bounds==before,"A rapid reversal retains the current native shape without a jump");
                Pump(14); PointF point=WorldLogo(panel);
                Check(Math.Abs(point.X-origin.X)<=2 && Math.Abs(point.Y-origin.Y)<=2,"Rapid reversals do not carry the Logo away from its origin");
            }
            panel.SetDockExpanded(false); WaitForRest(panel);
            PointF final=WorldLogo(panel);
            Check(Math.Abs(final.X-origin.X)<=2 && Math.Abs(final.Y-origin.Y)<=2,"Repeated open/close never creeps to the panel's outside corner");
            Check(logoVisibility==0,"Normal open/close and rapid reversals never hide/show the native Logo");
            Console.WriteLine("Logo-origin actual-monitor="+screen.DeviceName+"; dpi="+panel.DeviceDpi+"; frames="+frames+"; max-painted-drift-px="+drift);
            using(var frame=Frame(panel)) frame.Save(Path.Combine(output,"logo-origin-rest-"+panel.DeviceDpi+".png"));
            var layout=typeof(RecorderPanel).GetMethod("ApplyDpiLayout",BindingFlags.NonPublic|BindingFlags.Instance);
            foreach(int dpi in new[]{96,120,144,168,192,240}) {
                layout.Invoke(panel,new object[]{dpi}); panel.Location=new Point(screen.WorkingArea.Left+300,screen.WorkingArea.Top+200);
                PointF initial=WorldLogo(panel); double simulatedDrift=0;
                foreach(bool expanded in new[]{true,false}) {
                    panel.SetDockExpanded(expanded); var clock=Stopwatch.StartNew();
                    do {
                        Pump(18); PointF point=WorldLogo(panel);
                        simulatedDrift=Math.Max(simulatedDrift,Math.Max(Math.Abs(point.X-initial.X),Math.Abs(point.Y-initial.Y)));
                        Check(Math.Abs(point.X-initial.X)<=2 && Math.Abs(point.Y-initial.Y)<=2,"Simulated DPI Logo origin stays fixed throughout the morph: "+dpi);
                    } while(timer.Enabled && clock.ElapsedMilliseconds<450);
                    Check(!timer.Enabled,"Simulated DPI morph stops its timer: "+dpi);
                }
                Console.WriteLine("Logo-origin simulated-dpi="+dpi+"; max-painted-drift-px="+simulatedDrift);
            }
            layout.Invoke(panel,new object[]{panel.DeviceDpi});
            panel.AllowClose=true; panel.Close();
        }
    }
    static void StartupChecks() {
        foreach(Screen screen in Screen.AllScreens) using(var panel=new RecorderPanel()) {
            panel.EnableSharedDock(); panel.ShowStatus("ready","startup");
            panel.Location=new Point(screen.WorkingArea.Right-230,screen.WorkingArea.Bottom-80);
            panel.SetDockExpanded(true); panel.Show(); Pump(120); WaitForRest(panel);
            Check(screen.WorkingArea.Contains(panel.Bounds),"First expanded show fits after native monitor DPI promotion");
            PointF origin=WorldLogo(panel);
            for(int i=0;i<4;i++) {
                panel.ToggleDockFromLogo(); WaitForRest(panel);
                PointF current=WorldLogo(panel);
                Check(Math.Abs(current.X-origin.X)<=2 && Math.Abs(current.Y-origin.Y)<=2,"Startup DPI fitting rebases the same Logo anchor for later clicks");
                Check(screen.WorkingArea.Contains(panel.Bounds),"Startup toggles stay inside the working area");
            }
            panel.AllowClose=true; panel.Close();
        }
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
            int firstWidth = panel.Width; WaitForPartial(panel);
            Check(panel.Width > firstWidth && panel.Width < (int)Math.Round(186*panel.DeviceDpi/96.0), "Expansion contains intermediate native sizes");
            var radius = (RecorderDockSpring)Field(panel, "dockRadius");
            Check(radius.Value > 12 && radius.Value < 20, "Corner radius morphs continuously from capsule to panel");
            Check(panel.Heading.LinkArea.Length==0, "Shared status does not expose a text-link navigation target");
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
            ExternalCapture(panel);
            panel.ToggleDockFromLogo(); WaitForRest(panel); ExternalCapture(panel);
            panel.ToggleDockFromLogo(); WaitForRest(panel); ExternalCapture(panel);
            panel.SetDockExpanded(false); Pump(48); double before = ((RecorderDockSpring)Field(panel, "dockWidth")).Value;
            panel.SetDockExpanded(true); double after = ((RecorderDockSpring)Field(panel, "dockWidth")).Value;
            Check(before == after && timer.Enabled, "Rapid reentry retargets the current shape without restarting at the logo"); WaitForRest(panel);
            panel.ShowStatus("verbit", "ready"); panel.SetDockExpanded(true); WaitForRest(panel);
            Check(panel.Width == (int)Math.Round(186*panel.DeviceDpi/96.0) && panel.Height == (int)Math.Round(68*panel.DeviceDpi/96.0), "Idle and attention share one header-and-actions layout");
            panel.SetDockExpanded(false); WaitForRest(panel);
            Check(panel.Width == unit && !timer.Enabled, "Collapse is shorter than expansion and leaves no running frame timer");
            panel.Location = new Point(screen.WorkingArea.Right - unit - 4, screen.WorkingArea.Bottom - unit - 4);
            Point anchor = panel.Location; panel.ShowAttention("测试", "ready"); panel.SetDockExpanded(true);
            for (int i = 0; i < 10; i++) { Pump(24); Check(screen.WorkingArea.Contains(panel.Bounds), "Every edge-morph frame stays on the monitor"); }
            Point capsule = CapsuleAtLogo(panel);
            panel.SetDockExpanded(false); WaitForRest(panel); Check(Math.Abs(panel.Left-capsule.X)<=1 && Math.Abs(panel.Top-capsule.Y)<=1, "Screen-fitted panel collapses to its visible Logo within native DPI rounding");
            panel.SetDockExpanded(true); Pump(40); panel.Hide();
            Check(!timer.Enabled && Field(panel, "dockSnapshot") == null, "Hidden window stops morphing and releases transient content");
            RecorderMotionPreferences.Mode = "off"; panel.Show(); panel.SetDockExpanded(false);
            Check(panel.Width == unit && !timer.Enabled, "Motion-off mode changes state immediately without a timer");
            panel.SetDockExpanded(true); Check(panel.QuickNoteButton.Visible && !timer.Enabled, "Motion-off retains all actions and state feedback");
            ExternalCapture(panel);
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
                Event(panel,"OnMouseLeave"); Pump(330);
                double position = ((RecorderDockSpring)Field(panel,"dockWidth")).Value;
                Event(panel,"OnMouseEnter");
                Check(Math.Abs(((RecorderDockSpring)Field(panel,"dockWidth")).Value-position)<.001,"Reentering keeps the expanded shape unchanged"); Pump(40);
            }
            Event(panel,"OnMouseLeave");
            var leaveClock=Stopwatch.StartNew();
            while (((bool)Field(panel,"dockExpanded") || timer.Enabled) && leaveClock.ElapsedMilliseconds<750) Pump(2);
            Console.WriteLine("Leave settle total ms="+leaveClock.ElapsedMilliseconds);

            Check(!timer.Enabled && (bool)Field(panel, "dockExpanded"), "Leaving keeps the expanded panel stable until an explicit collapse");
            panel.ToggleDockFromLogo(); WaitForRest(panel);
            panel.SetDockExpanded(true); WaitForPartial(panel);
            SendMessage(handle,0x231,IntPtr.Zero,IntPtr.Zero); Event(panel,"OnMouseLeave"); Pump(260);
            SendMessage(handle,0x232,IntPtr.Zero,IntPtr.Zero); Pump(650);
            Check((bool)Field(panel,"dockExpanded") && !timer.Enabled,"Leaving during a held drag does not collapse the released panel");
            // A profile change must keep the same fixed Logo, not move it into a header.
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
                Point capsule=CapsuleAtLogo(panel);
                panel.SetDockExpanded(false); WaitForRest(panel); Check(Math.Abs(panel.Left-capsule.X)<=1 && Math.Abs(panel.Top-capsule.Y)<=1, "At every corner collapse keeps the expanded Logo within native DPI rounding");
            }
            foreach (Screen destination in Screen.AllScreens) if (destination.DeviceName != screen.DeviceName) {
                foreach (string state in new[] {"collapsed","expanded","partial"}) {
                    panel.SetDockExpanded(false); WaitForRest(panel);
                    if(state!="collapsed") { panel.SetDockExpanded(true); if(state=="partial") WaitForPartial(panel); else WaitForRest(panel); }
                    SendMessage(handle, 0x231, IntPtr.Zero, IntPtr.Zero);
                    panel.Location = new Point(destination.WorkingArea.Left + 140, destination.WorkingArea.Top + 140); Pump(30);
                    Check(Screen.FromControl(panel).DeviceName == destination.DeviceName, "Cross-DPI native dragging must not jump back to the old monitor: " + state);
                    Check(panel.ClientRectangle.Contains(panel.BrandMark.Bounds) || !panel.BrandMark.Visible,"Frozen DPI-scaled logo remains inside the visible surface: " + state);
                    if(state=="expanded") Check(Math.Abs(panel.BrandMark.Left+panel.BrandTitle.Right-panel.ClientSize.Width)<=2,"Cross-DPI dragging retains the centered brand group before release");
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
                Check(Math.Abs(panel.BrandMark.Left+panel.BrandTitle.Right-panel.ClientSize.Width)<=2,"Brand group remains centered at simulated DPI " + dpi);
                Check(typeof(RecorderPanel).GetField("CollapseButton")==null,"No separate collapse icon at simulated DPI " + dpi);
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
    static void Mouse(Control control, string method, int x, int y) {
        typeof(Control).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(control, new object[] {new MouseEventArgs(MouseButtons.Left,1,x,y,0)});
    }
    static void CheckToolsLayout(RecorderPanel panel) {
        Control[] actions={panel.QuickNoteButton,panel.StartButton,panel.SettingsButton};
        int firstGap=actions[1].Left-actions[0].Right;
        foreach(Control c in actions) {
            Check(panel.ClientRectangle.Contains(c.Bounds),"Every tool fits the expanded strip at actual DPI");
            Check(c.Top>panel.BrandTitle.Bottom,"Tools remain below the brand-only header");
            Check(!c.Bounds.IntersectsWith(panel.BrandMark.Bounds),"Tools never overlap the Logo toggle");
        }
        Check(firstGap>0 && Math.Abs(firstGap-(actions[2].Left-actions[1].Right))<=1,"Action gaps remain equal after DPI rounding");
        Check(typeof(RecorderPanel).GetField("CollapseButton")==null,"Logo is the only open/close control");
        Check(Math.Abs(panel.BrandMark.Left+panel.BrandTitle.Right-panel.ClientSize.Width)<=2,"Logo and name form a centered group");
    }
    static void CheckSharedLayout(RecorderPanel panel) {
        Control[] controls={panel.BrandMark,panel.BrandTitle,panel.Time,panel.AudioLevel,panel.Heading,panel.Detail,panel.AudioFile,panel.TranscriptFile,panel.MicrophoneButton,panel.StopButton,panel.QuickNoteButton,panel.StartButton,panel.SettingsButton};
        Check(panel.BrandTitle.Visible && panel.BrandTitle.Text=="verbit" && !panel.Heading.Visible && !panel.Detail.Visible,"All shared states retain the brand header without persistent small status text");
        Check(typeof(RecorderPanel).GetField("CollapseButton")==null,"There is no extra close or collapse icon");
        for(int i=0;i<controls.Length;i++) if(controls[i].Visible) {
            Check(panel.ClientRectangle.Contains(controls[i].Bounds),"Visible control fits the compact panel: "+controls[i].GetType().Name);
            for(int j=i+1;j<controls.Length;j++) if(controls[j].Visible)
                Check(!controls[i].Bounds.IntersectsWith(controls[j].Bounds),"Status, Logo and interactive controls have separate bounds: "+i+"/"+j);
        }
    }
    static Point LogoScreen(RecorderPanel panel) { return panel.BrandMark.PointToScreen(Point.Empty); }
    static void ExperienceChecks(string output) {
        foreach(Screen screen in Screen.AllScreens) using(var panel=new RecorderPanel()) {
            panel.EnableSharedDock(); panel.ShowStatus("ready","synthetic"); panel.Show(); Pump(60);
            panel.Location=new Point(screen.WorkingArea.Left+160,screen.WorkingArea.Top+160); Pump(60);
            int unit=(int)Math.Round(40*panel.DeviceDpi/96.0); IntPtr handle=panel.Handle;
            for(int i=0;i<8;i++) { Event(panel,"OnMouseEnter"); Pump(25); Event(panel,"OnMouseLeave"); Pump(25); }
            Check(!(bool)Field(panel,"dockExpanded"),"Skimming the Logo never opens the panel");
            foreach(Control hover in new Control[] {panel,panel.BrandMark}) {
                Event(hover,"OnMouseEnter"); Pump(500);
                Check(!(bool)Field(panel,"dockExpanded") && panel.Width==unit && !((System.Windows.Forms.Timer)Field(panel,"dockAnimation")).Enabled,"Sustained hover cannot expand or start a morph: "+hover.GetType().Name);
                Event(hover,"OnMouseLeave");
            }
            Mouse(panel.BrandMark,"OnMouseDown",5,5); Mouse(panel.BrandMark,"OnMouseUp",5,5); WaitForRest(panel);
            Check(panel.QuickNoteButton.Visible && panel.BrandMark.Visible,"A Logo click opens actions and remains the collapse target");
            CheckToolsLayout(panel);
            panel.ToggleDockFromLogo(); WaitForRest(panel);
            Event(panel.BrandMark,"OnMouseEnter"); Pump(500);
            Check(!(bool)Field(panel,"dockExpanded"),"Hover immediately after manual collapse never reopens tools");
            Mouse(panel,"OnMouseDown",2,2); Mouse(panel,"OnMouseUp",2,2); WaitForRest(panel);
            Check((bool)Field(panel,"dockExpanded"),"Clicking the collapsed Logo padding also opens tools");
            Mouse(panel.BrandMark,"OnMouseDown",5,5); Mouse(panel.BrandMark,"OnMouseUp",5,5); Event(panel,"OnMouseLeave"); Pump(650);
            Check(!(bool)Field(panel,"dockExpanded") && !panel.QuickNoteButton.Visible,"A second Logo click collapses and mouse leave preserves that choice");
            Mouse(panel.BrandMark,"OnMouseDown",5,5); Mouse(panel.BrandMark,"OnMouseUp",5,5); WaitForPartial(panel);
            bool left=(bool)Field(panel,"dockLeft"), up=(bool)Field(panel,"dockUp");
            Point paintedLogo=new Point((left?panel.Width-unit:0)+(int)Math.Round((((RecorderDockSpring)Field(panel,"dockLogoX")).Value+4)*panel.DeviceDpi/96.0),(up?panel.Height-unit:0)+(int)Math.Round((((RecorderDockSpring)Field(panel,"dockLogoY")).Value+4)*panel.DeviceDpi/96.0));
            Mouse(panel,"OnMouseDown",paintedLogo.X,paintedLogo.Y); Mouse(panel,"OnMouseUp",paintedLogo.X,paintedLogo.Y); WaitForRest(panel);
            Check(!(bool)Field(panel,"dockExpanded"),"Clicking the painted Logo reverses expansion without waiting for child controls");
            Mouse(panel.BrandMark,"OnMouseDown",5,5); Mouse(panel.BrandMark,"OnMouseUp",5,5); WaitForRest(panel);
            Check((bool)Field(panel,"dockExpanded"),"Logo remains clickable after an interrupted reversal");
            panel.ShowWorking("processing","synthetic"); WaitForRest(panel);
            Check((bool)Field(panel,"dockExpanded"),"Polling or a profile change preserves expansion");
            Check(!panel.Heading.Visible && !panel.Detail.Visible && panel.SettingsButton.Visible,"Processing keeps common actions without persistent status text");
            panel.ToggleDockFromLogo(); WaitForRest(panel); panel.ShowRecording("mic","system"); Pump(50);
            Check(!(bool)Field(panel,"dockExpanded") && !((System.Windows.Forms.Timer)Field(panel,"dockAnimation")).Enabled,"A new recording state does not reopen a manually collapsed dock");
            foreach(Point corner in new[] {new Point(screen.WorkingArea.Left,screen.WorkingArea.Top),new Point(screen.WorkingArea.Right-unit,screen.WorkingArea.Top),new Point(screen.WorkingArea.Left,screen.WorkingArea.Bottom-unit),new Point(screen.WorkingArea.Right-unit,screen.WorkingArea.Bottom-unit)}) {
                panel.SetDockExpanded(false); WaitForRest(panel); panel.Location=corner; Point logo=LogoScreen(panel);
                panel.SetDockExpanded(true);
                for(int i=0;i<12;i++) { Pump(25); Point current=panel.PointToScreen(new Point((bool)Field(panel,"dockLeft")?panel.Width-unit:0,(bool)Field(panel,"dockUp")?panel.Height-unit:0)); Check(current==corner,"Every morph frame preserves the actual Logo-cell anchor in both axes"); }
                WaitForRest(panel); Check(panel.BrandMark.Left==(int)Math.Round(59*panel.DeviceDpi/96.0) && panel.BrandMark.Top==(int)Math.Round(8*panel.DeviceDpi/96.0),"Expanded header keeps the brand group centered");
                Check(panel.BrandTitle.Left>panel.BrandMark.Right,"Header contains only Logo and name at every corner");
                foreach(Control c in new Control[] {panel.Time,panel.AudioLevel,panel.MicrophoneButton,panel.StopButton}) Check(panel.ClientRectangle.Contains(c.Bounds),"Recording controls remain inside every corner orientation: "+c.GetType().Name);
                panel.FlashBookmark(42); CheckSharedLayout(panel);
            }
            panel.SetDockExpanded(false); WaitForRest(panel); panel.Location=new Point(screen.WorkingArea.Left+160,screen.WorkingArea.Top+160);
            panel.ShowAttention("转写失败","录音已保留；合成错误原因"); panel.SetRetryAvailable(true); panel.SetDockExpanded(true); WaitForRest(panel);
            foreach(Point corner in new[] {new Point(screen.WorkingArea.Left,screen.WorkingArea.Top),new Point(screen.WorkingArea.Right-unit,screen.WorkingArea.Top),new Point(screen.WorkingArea.Left,screen.WorkingArea.Bottom-unit),new Point(screen.WorkingArea.Right-unit,screen.WorkingArea.Bottom-unit)}) {
                panel.SetDockExpanded(false); WaitForRest(panel); panel.Location=corner;
                panel.SetDockExpanded(true); WaitForRest(panel); CheckSharedLayout(panel);
            }
            int retries=0; panel.RetryRequested+=delegate { retries++; Retry(panel); };
            CheckSharedLayout(panel);
            Check(panel.CanRetryTranscription && !panel.Heading.Visible && panel.StartButton.Visible,"Recoverable failure remains retryable through the menu while preserving recording and hiding status text");
            Retry(panel); Check(retries==1,"A retry cannot be launched recursively or twice while its handler is running");
            int moves=0, visibility=0;
            foreach(Control button in new Control[]{panel.StartButton,panel.QuickNoteButton,panel.SettingsButton}) {
                button.LocationChanged+=delegate { moves++; }; button.VisibleChanged+=delegate { visibility++; };
            }
            for(int i=0;i<30;i++) { panel.ShowAttention("转写失败","录音已保留；合成错误原因"); panel.SetRetryAvailable(true); }
            Check(moves==0 && visibility==0,"Unchanged failure polling never moves or hides action buttons");
            Check(!panel.StartButton.Bounds.IntersectsWith(panel.BrandMark.Bounds),"Logo toggle and recording have separate hit targets");
            Pump(200);
            using(var frame=Frame(panel)) frame.Save(Path.Combine(output,"ux-retry-"+panel.DeviceDpi+".png"));
            panel.SetRetryAvailable(false); Check(!panel.CanRetryTranscription && panel.Heading.LinkArea.Length==0 && panel.StartButton.Visible,"A non-retryable state never offers a misleading retry action");
            using(var frame=Frame(panel)) frame.Save(Path.Combine(output,"ux-attention-"+panel.DeviceDpi+".png"));
            panel.ShowRecording("synthetic mic","synthetic system"); WaitForRest(panel); panel.SetElapsed(TimeSpan.FromMinutes(12)); panel.SetInputLevels(.2f,.3f); Pump(200);
            using(var frame=Frame(panel)) frame.Save(Path.Combine(output,"ux-recording-"+panel.DeviceDpi+".png"));
            foreach(Control surface in new Control[] {panel,panel.Heading,panel.Detail,panel.Time,panel.AudioLevel,panel.BrandTitle,panel.BrandMark,(Control)Field(panel,"dockContent")}) {
                Mouse(surface,"OnMouseDown",1,1); Check(Object.ReferenceEquals(Field(panel,"dockDragControl"),surface),"Every non-interactive surface participates in thresholded dragging: "+surface.GetType().Name);
                Mouse(surface,"OnMouseUp",1,1);
            }
            panel.SetDockExpanded(false); WaitForRest(panel); panel.Location=new Point(screen.WorkingArea.Left+160,screen.WorkingArea.Top+160);
            panel.SetDockExpanded(true); WaitForRest(panel);
            SendMessage(handle,0x231,IntPtr.Zero,IntPtr.Zero);
            panel.Location=new Point(screen.WorkingArea.Left+160,screen.WorkingArea.Bottom-unit);
            Rectangle released=panel.Bounds; SendMessage(handle,0x232,IntPtr.Zero,IntPtr.Zero);
            Check(panel.Bounds==released,"Releasing an expanded panel below the working area does not jump on its first frame");
            WaitForRest(panel); Check(screen.WorkingArea.Contains(panel.Bounds),"An expanded panel dragged to the bottom softly returns fully inside the working area");
            Point safeAnchor=CapsuleAtLogo(panel); panel.SetDockExpanded(false); WaitForRest(panel);
            Check(Math.Abs(panel.Left-safeAnchor.X)<=1 && Math.Abs(panel.Top-safeAnchor.Y)<=1,"Collapse after soft screen fitting keeps the visible Logo within native DPI rounding");
            panel.SetDockExpanded(false); WaitForRest(panel);
            foreach(int edge in new[]{-1,1}) {
                panel.Location=new Point(edge<0?screen.WorkingArea.Left+5:screen.WorkingArea.Right-unit-5,screen.WorkingArea.Top+180);
                SendMessage(handle,0x231,IntPtr.Zero,IntPtr.Zero); Rectangle before=panel.Bounds; SendMessage(handle,0x232,IntPtr.Zero,IntPtr.Zero);
                Check(panel.Bounds==before,"Edge attraction starts from the released position without a snap");
                Pump(700); WaitForRest(panel);
                Check((int)Field(panel,"dockEdge")==edge && panel.Width==(int)Math.Round(6*panel.DeviceDpi/96.0),"Release near a side settles to a small visible strip");
                Check(edge<0?panel.Left==screen.WorkingArea.Left:panel.Right==screen.WorkingArea.Right,"The strip is exactly inside the chosen working edge");
                using(var frame=Frame(panel)) frame.Save(Path.Combine(output,"ux-edge-"+edge+"-"+panel.DeviceDpi+".png"));
                Event(panel,"OnMouseEnter"); Pump(500);
                Check(!(bool)Field(panel,"dockExpanded") && panel.Width==(int)Math.Round(6*panel.DeviceDpi/96.0),"Sustained edge-strip hover never expands the panel");
                Mouse(panel,"OnMouseDown",2,8); Mouse(panel,"OnMouseUp",2,8); WaitForRest(panel);
                Check((bool)Field(panel,"dockExpanded") && screen.WorkingArea.Contains(panel.Bounds),"An edge-strip click expands inward within the working area");
                panel.ShowStatus("ready","synthetic"); WaitForRest(panel); CheckToolsLayout(panel);
                SendMessage(handle,0x231,IntPtr.Zero,IntPtr.Zero); panel.Location=new Point(screen.WorkingArea.Left+220,screen.WorkingArea.Top+180); SendMessage(handle,0x232,IntPtr.Zero,IntPtr.Zero);
                Check((int)Field(panel,"dockEdge")==0,"Dragging away releases edge mode without competing with the native drag");
                panel.SetDockExpanded(false); WaitForRest(panel); Check(panel.Width==unit,"Detached dock returns to its Logo rather than a strip");
            }
            Check(panel.TopMost && SendMessage(handle,0x21,IntPtr.Zero,IntPtr.Zero).ToInt32()==3,"The dock stays topmost without activating on a mouse click");
            Point saved=panel.Location; panel.Hide(); panel.Show(); Pump(50); Check(panel.Location==saved,"Hide/show preserves the user's position");
            Check(panel.Handle==handle,"All interaction modes retain one native floating window");
            panel.AllowClose=true; panel.Close();
        }
    }
    static void Retry(RecorderPanel panel) {
        panel.RetryTranscription();
    }
    [STAThread] static int Main(string[] args) {
        if(args.Length>0 && args[0]=="--capture-window") return CaptureFromProcess(args);
        string previous = RecorderMotionPreferences.Mode;
        try { Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException); Application.EnableVisualStyles(); RecorderMotionPreferences.Mode = "on"; SpringChecks(); if (Array.IndexOf(args, "--anchor-only") >= 0) LogoAnchorChecks(AppDomain.CurrentDomain.BaseDirectory); else if (Array.IndexOf(args, "--review-only") >= 0) ReviewChecks(AppDomain.CurrentDomain.BaseDirectory); else { StartupChecks(); LogoAnchorChecks(AppDomain.CurrentDomain.BaseDirectory); NativeChecks(AppDomain.CurrentDomain.BaseDirectory); ReviewChecks(AppDomain.CurrentDomain.BaseDirectory); ExperienceChecks(AppDomain.CurrentDomain.BaseDirectory); } Console.WriteLine("PASS fluid morph assertions=" + assertions); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { RecorderMotionPreferences.Mode = previous; }
    }
}
