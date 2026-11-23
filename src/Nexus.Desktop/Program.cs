namespace Nexus.Desktop;

internal static class Program
{
    private const string MutexName = @"Local\ProjectNexusDesktopSingleton";
    private const string ActivateEventName = @"Local\ProjectNexusDesktopActivate";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--verify-install", StringComparison.OrdinalIgnoreCase)))
        {
            _ = DesktopConfigStore.LoadBundledDefaultUrl();
            return 0;
        }

        var startMinimized = args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        using var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);

        if (!createdNew)
        {
            activateEvent.Set();
            return 0;
        }

        ApplicationConfiguration.Initialize();

        var config = DesktopConfigStore.Load();
        using var mainForm = new MainForm(config, startMinimized);

        using var cts = new CancellationTokenSource();
        var watcher = Task.Run(() =>
        {
            var handles = new WaitHandle[] { activateEvent, cts.Token.WaitHandle };
            while (!cts.IsCancellationRequested)
            {
                if (WaitHandle.WaitAny(handles) == 0)
                {
                    mainForm.ShowAndActivate();
                }
            }
        });

        Application.Run(mainForm);
        cts.Cancel();
        try
        {
            watcher.Wait(TimeSpan.FromMilliseconds(250));
        }
        catch
        {
            // Ignore cancellation race on exit.
        }

        return 0;
    }
}
