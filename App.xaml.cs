using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Forms = System.Windows.Forms;

namespace TextDock;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\TextDock.Instance";
    private const string ActivateEventName = @"Local\TextDock.Activate";
    private const string QuitEventName = @"Local\TextDock.Quit";

    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private EventWaitHandle? _activateEvent;
    private EventWaitHandle? _quitEvent;
    private RegisteredWaitHandle? _activateWait;
    private RegisteredWaitHandle? _quitWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // "TextDock.exe --quit" closes a running instance (used by publish.ps1).
        bool quit = Array.Exists(e.Args, a => a.Equals("--quit", StringComparison.OrdinalIgnoreCase));

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            // Already running. Ask the first instance to show itself (or quit), handing over
            // foreground rights so its window can actually come to the front.
            if (!quit) Native.AllowSetForegroundWindow(Native.ASFW_ANY);
            try
            {
                using var signal = EventWaitHandle.OpenExisting(quit ? QuitEventName : ActivateEventName);
                signal.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // First instance is still starting up or shutting down.
            }
            Shutdown();
            return;
        }
        if (quit)
        {
            Shutdown(); // nothing was running
            return;
        }

        Log.Init();
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("unhandled UI exception", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("fatal exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("unobserved task exception", args.Exception);
            args.SetObserved();
        };

        Forms.Application.EnableVisualStyles();

        var options = StartupOptions.Parse(e.Args);
        var settings = AppSettings.Load();
        var window = new MainWindow(settings, options);
        MainWindow = window;

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _activateWait = ThreadPool.RegisterWaitForSingleObject(_activateEvent,
            (_, _) => Dispatcher.InvokeAsync(window.ShowAndFocus), null, Timeout.Infinite, executeOnlyOnce: false);
        _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        _quitWait = ThreadPool.RegisterWaitForSingleObject(_quitEvent,
            (_, _) => Dispatcher.InvokeAsync(window.ExitApp), null, Timeout.Infinite, executeOnlyOnce: true);

        SessionEnding += (_, _) => window.PrepareForShutdown();
        window.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activateWait?.Unregister(null);
        _quitWait?.Unregister(null);
        _activateEvent?.Dispose();
        _quitEvent?.Dispose();
        if (_ownsMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
