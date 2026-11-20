using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mailkeep.WinForms;

/// <summary>Opt-in startup trace. Set MAILKEEP_STARTUP_TRACE to an output file before launch.
/// Native message timings, UI heartbeat and compositor waits cover the first 30 seconds.
/// All disk writes run on a separate thread; no subjects, addresses or message bodies are recorded.</summary>
internal static class StartupDiagnostics
{
    internal static readonly bool Enabled = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MAILKEEP_STARTUP_TRACE"));
    private static readonly ConcurrentQueue<string> Lines = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double _processOffset;
    private static int _started;
    private static long _revealed;
    private static int _compositionAfterReveal;
    private static volatile bool _stopped;
    private static Thread? _writer;

    internal static bool Skip(string part) => Enabled && Environment.GetEnvironmentVariable("MAILKEEP_NO_" + part) == "1";

    internal static void Start()
    {
        if (!Enabled || Interlocked.Exchange(ref _started, 1) != 0) return;
        using var process = Process.GetCurrentProcess();
        _processOffset = (DateTime.Now - process.StartTime).TotalMilliseconds - Clock.Elapsed.TotalMilliseconds;
        Mark($"start pid={process.Id} utc={DateTime.UtcNow:O} runtime={Environment.Version}");
        _writer = new Thread(WriteLines) { IsBackground = true, Name = "Mailkeep startup log" };
        _writer.Start();
        if (!Skip("DWM_PROBE"))
            new Thread(ProbeComposition) { IsBackground = true, Name = "Mailkeep startup compositor" }.Start();
    }

    internal static void Mark(string phase)
    {
        if (Enabled && !_stopped)
            Lines.Enqueue($"{Clock.Elapsed.TotalMilliseconds + _processOffset,10:F1} ms t{Environment.CurrentManagedThreadId} os_tid={GetCurrentThreadId()} {phase}");
    }

    internal static void Finish()
    {
        _stopped = true;
        _writer?.Join(500); // On exit only: preserve the final heartbeat summary before the process ends.
    }

    internal static T Time<T>(string phase, Func<T> work)
    {
        if (!Enabled) return work();
        var start = Stopwatch.GetTimestamp();
        Mark("begin " + phase);
        try { return work(); }
        finally { Mark($"end {phase} duration_ms={Stopwatch.GetElapsedTime(start).TotalMilliseconds:F1}"); }
    }

    internal static void Time(string phase, Action work) => Time(phase, () => { work(); return 0; });

    internal static async Task TimeAsync(string phase, Func<Task> work)
    {
        if (!Enabled) { await work(); return; }
        var start = Stopwatch.GetTimestamp();
        Mark("begin " + phase);
        try { await work(); }
        finally { Mark($"end {phase} duration_ms={Stopwatch.GetElapsedTime(start).TotalMilliseconds:F1}"); }
    }

    internal static void Watch(Form form)
    {
        if (!Enabled) return;
        var hooks = new List<MessageProbe>();
        var watched = new HashSet<Control>();
        var last = Stopwatch.GetTimestamp();
        double longest = 0;
        var ticks = 0;
        var finished = false;
        var timer = new System.Windows.Forms.Timer { Interval = 16 };
        WatchControl(form);
        form.Shown += (_, _) => Resources(form, "shown");
        form.FormClosed += (_, _) => Stop();
        timer.Tick += (_, _) =>
        {
            var now = Stopwatch.GetTimestamp();
            var gap = Stopwatch.GetElapsedTime(last, now).TotalMilliseconds;
            longest = Math.Max(longest, gap);
            ticks++;
            if (gap > 100) Mark($"ui_gap duration_ms={gap:F1}");
            last = now;
            if (Clock.Elapsed.TotalSeconds >= 30) Stop();
        };
        timer.Start();

        void WatchControl(Control control)
        {
            if (!watched.Add(control)) return;
            var probe = new MessageProbe(control);
            hooks.Add(probe);
            control.HandleCreated += probe.Created;
            control.HandleDestroyed += probe.Destroyed;
            control.ControlAdded += Added;
            if (control.IsHandleCreated) probe.Created(control, EventArgs.Empty);
            foreach (Control child in control.Controls) WatchControl(child);
        }
        void Added(object? sender, ControlEventArgs e) { if (e.Control is { } child) WatchControl(child); }
        void Stop()
        {
            if (finished) return;
            finished = true;
            timer.Dispose();
            foreach (var control in watched) control.ControlAdded -= Added;
            foreach (var probe in hooks) probe.Detach();
            Mark($"ui_summary ticks={ticks} max_gap_ms={longest:F1} controls={watched.Count}");
            _stopped = true;
        }
    }

    internal static void Revealed(Form form)
    {
        if (!Enabled) return;
        Volatile.Write(ref _revealed, Stopwatch.GetTimestamp());
        Mark("opacity=1 returned");
        Resources(form, "revealed");
    }

    internal static void Resources(Form form, string phase)
    {
        if (!Enabled || !form.IsHandleCreated) return;
        var children = 0;
        EnumChildWindows(form.Handle, (_, _) => { children++; return true; }, IntPtr.Zero);
        using var process = Process.GetCurrentProcess();
        Mark($"resources {phase} children={children} gdi={GetGuiResources(process.Handle, 0)} user={GetGuiResources(process.Handle, 1)} " +
             $"bounds={form.Bounds} state={form.WindowState} dpi={form.DeviceDpi} opacity={form.Opacity}");
    }

    private static void ProbeComposition()
    {
        while (!_stopped && Clock.Elapsed.TotalSeconds < 30)
        {
            var start = Stopwatch.GetTimestamp();
            var revealed = Volatile.Read(ref _revealed);
            var result = DwmFlush();
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (elapsed > 100 || result != 0) Mark($"dwm_flush duration_ms={elapsed:F1} hr=0x{result:X8}");
            // A flush begun after reveal is a compositor fence, not proof of this window's pixels on screen.
            if (result == 0 && revealed != 0 && Interlocked.Exchange(ref _compositionAfterReveal, 1) == 0)
                Mark($"dwm_fence_after_reveal elapsed_ms={Stopwatch.GetElapsedTime(revealed).TotalMilliseconds:F1}");
            Thread.Sleep(50);
        }
    }

    private static void WriteLines()
    {
        try
        {
            var file = Path.GetFullPath(Environment.GetEnvironmentVariable("MAILKEEP_STARTUP_TRACE")!);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using var writer = new StreamWriter(file, append: true) { AutoFlush = true };
            while (!_stopped || !Lines.IsEmpty)
            {
                while (Lines.TryDequeue(out var line)) writer.WriteLine(line);
                Thread.Sleep(50);
            }
        }
        catch (Exception) { } // Diagnostics must never prevent opening mail.
    }

    private sealed class MessageProbe(Control control) : NativeWindow
    {
        private readonly HashSet<int> _first = [];
        internal void Created(object? sender, EventArgs e)
        {
            AssignHandle(control.Handle);
            Mark($"handle_created control={control.GetType().Name} hwnd=0x{Handle:X}");
        }
        internal void Destroyed(object? sender, EventArgs e) { ReleaseHandle(); }
        internal void Detach()
        {
            control.HandleCreated -= Created;
            control.HandleDestroyed -= Destroyed;
            ReleaseHandle();
        }
        protected override void WndProc(ref Message message)
        {
            var id = message.Msg;
            var start = Stopwatch.GetTimestamp();
            var lifecycle = control is Form && id is 0x18 or 0xF or 0x85 or 0x47 or 0x2E0 or 0x6;
            if (lifecycle && (_first.Add(id) || id is 0x47 or 0x2E0))
                Mark($"message control={control.GetType().Name} hwnd=0x{Handle:X} id=0x{id:X4} bounds={control.Bounds} dpi={control.DeviceDpi}");
            try { base.WndProc(ref message); }
            finally
            {
                var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (elapsed > 50) Mark($"slow_message control={control.GetType().Name} hwnd=0x{Handle:X} id=0x{id:X4} duration_ms={elapsed:F1}");
            }
        }
    }

    private delegate bool EnumWindow(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hwnd, EnumWindow callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flags);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
