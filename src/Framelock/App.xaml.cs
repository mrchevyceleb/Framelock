using System.IO;
using System.Windows;
using System.Windows.Threading;
using Framelock.Capture;
using Framelock.Core;
using Framelock.Encoding;
using Framelock.Engine;
using Framelock.Graphics;

namespace Framelock;

public partial class App : Application
{
    private const string MutexName = "Framelock.SingleInstance.v1";
    private const string ActivateEventName = "Framelock.Activate.v1";
    private Mutex? _mutex;
    private EventWaitHandle? _activate;

    public static SettingsStore Store { get; private set; } = null!;
    public static RecorderController Recorder { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Paths.Ensure();
        Log.Init();
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("UI exception", ex.Exception);
            ex.Handled = true;
            ShowError(ex.Exception.Message);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Fatal exception", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("Unobserved task exception", ex.Exception); ex.SetObserved(); };

        if (e.Args.Contains("--selftest"))
        {
            int code = await SelfTest.RunAsync(e.Args);
            Log.Shutdown();
            Shutdown(code);
            return;
        }

        _mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            try { EventWaitHandle.OpenExisting(ActivateEventName).Set(); } catch { }
            Shutdown();
            return;
        }
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        new Thread(() =>
        {
            while (_activate.WaitOne())
                Dispatcher.BeginInvoke(() => (MainWindow as Ui.MainWindow)?.BringToFront());
        }) { IsBackground = true, Name = "Activate listener" }.Start();

        if (!WgcSource.IsSupported)
        {
            MessageBox.Show("Screen capture isn't supported on this version of Windows (needs Windows 10 2004 or newer).", "Framelock", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        try { FFmpegSetup.Init(); }
        catch (Exception ex)
        {
            Log.Error("FFmpeg failed to load", ex);
            MessageBox.Show($"The FFmpeg libraries couldn't be loaded from\n{Paths.FFmpegFolder}\n\n{ex.Message}", "Framelock", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        Store = new SettingsStore();
        foreach (var o in Store.Settings.Overlays) OverlayRenderer.Refresh(o);
        _ = EncoderCatalog.ProbeAsync();
        WgcSource.RequestBorderlessAccess();
        Recorder = new RecorderController(Store, Dispatcher);

        var win = new Ui.MainWindow();
        MainWindow = win;
        if (e.Args.Contains("--minimized") || e.Args.Contains("--tray")) win.StartHidden();
        else win.Show();
        if (e.Args.FirstOrDefault(a => a.StartsWith("--uishot=")) is { } shot)
            _ = win.SaveUiShotsAsync(shot["--uishot=".Length..]);
    }

    public static void ShowError(string message)
    {
        try { Ui.Toast.Show(new Notification("Something went wrong", message, IsError: true)); } catch { }
    }

    public void Quit()
    {
        try { Recorder?.Shutdown(); } catch (Exception ex) { Log.Error("Shutdown failed", ex); }
        try { Store?.SaveNow(); } catch { }
        Log.Shutdown();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activate?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
