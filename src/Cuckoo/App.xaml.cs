using System.Windows;
using System.Windows.Threading;
using Cuckoo.Core;
using Cuckoo.Services;
using Cuckoo.ViewModels;

namespace Cuckoo;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private TwitchClient? _client;
    private Task? _runTask;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single instance guard (replaces lock.file). Scoped to the install folder,
        // so separate copies (each with its own settings/auth) can run side by side,
        // e.g. one folder per account.
        _instanceMutex = new Mutex(true, $@"Local\Cuckoo_{Core.Constants.InstanceId}", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "Cuckoo is already running from this folder.\n\n"
                + "To mine with a second account, copy the app into a different folder "
                + "and run it from there.",
                "Cuckoo", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(3);
            return;
        }

        // process-wide crash catching: log everything, keep running where possible
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Logger.Instance.Exception(
                "FATAL unhandled exception" + (args.IsTerminating ? " (terminating)" : ""),
                args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject.ToString()));
            if (args.IsTerminating)
                Logger.Instance.Dispose(); // flush logs before the process dies
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Instance.Exception("Unobserved task exception", args.Exception);
            args.SetObserved(); // never let a forgotten task crash the process
        };

        Logger.Instance.Info($"=== Cuckoo starting (v{typeof(App).Assembly.GetName().Version}) ===");
        Settings settings = Settings.Load();
        Logger.Instance.Verbosity = settings.LogVerbosity;
        // fix up any pre-fix shared autostart entry that belonged to this instance
        AutostartService.MigrateLegacy();
        var viewModel = new MainViewModel(settings);
        _client = new TwitchClient(settings, viewModel);
        viewModel.AttachClient(_client);

        var window = new MainWindow(viewModel);
        MainWindow = window;
        bool startInTray = e.Args.Contains("--tray") || settings.AutostartTray;
        window.Show();
        if (startInTray)
            window.MinimizeToTray();
        else if (settings.MiniMode)
            window.SwitchToMini();

        // run the mining client on a background task
        TwitchClient client = _client;
        _runTask = Task.Run(() => RunClientWithSelfHealAsync(client, viewModel, settings));
    }

    /// <summary>
    /// Runs the mining client and, when self-heal is enabled, restarts it with
    /// exponential backoff after fatal errors instead of staying dead.
    /// </summary>
    private static async Task RunClientWithSelfHealAsync(
        TwitchClient client, MainViewModel viewModel, Settings settings)
    {
        // base 2 with a 10s shift: ~11s, 12s, 14s, 18s, ... capped at 5 minutes
        var backoff = new ExponentialBackoff(shift: 10, maximum: 300);
        try
        {
            while (true)
            {
                DateTime runStarted = DateTime.UtcNow;
                TimeSpan restartDelay;
                try
                {
                    await client.RunAsync().ConfigureAwait(false);
                    break; // clean exit (user close)
                }
                catch (CaptchaRequiredException)
                {
                    client.LogError("Captcha required during login");
                    viewModel.Print("Twitch requires a captcha to be solved. Please try again later.");
                    client.NotifyError("Twitch requires a captcha to be solved.");
                    if (!settings.AutoRestartOnError || viewModel.CloseRequested)
                        break;
                    // captchas don't resolve quickly: wait substantially longer
                    restartDelay = TimeSpan.FromMinutes(30);
                }
                catch (Exception ex)
                {
                    client.LogException("Fatal error in the mining core", ex);
                    viewModel.Print($"Fatal error encountered:\n{ex.Message}");
                    client.NotifyError($"The miner hit an error:\n{ex.Message}");
                    if (!settings.AutoRestartOnError || viewModel.CloseRequested)
                    {
                        viewModel.SetStatus("Terminated");
                        viewModel.ChangeTrayIcon("error");
                        viewModel.TrayNotify(
                            "The miner has stopped due to an error. Check the logs.",
                            "Cuckoo - terminated");
                        break;
                    }
                    // a long healthy run means the previous problem was resolved
                    if (DateTime.UtcNow - runStarted > TimeSpan.FromMinutes(15))
                        backoff.Reset();
                    restartDelay = TimeSpan.FromSeconds(backoff.Next());
                }

                // self-heal: clean up, wait out the backoff, then start over
                viewModel.ChangeTrayIcon("error");
                string delayText = restartDelay.TotalSeconds < 120
                    ? $"{Math.Round(restartDelay.TotalSeconds)}s"
                    : $"{Math.Round(restartDelay.TotalMinutes)}min";
                client.LogWarning($"Self-heal: restarting the mining core in {delayText}");
                viewModel.Print($"Self-heal: restarting in {delayText}...");
                viewModel.SetStatus($"Restarting in {delayText}...");
                try
                {
                    await client.ShutdownAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    client.LogException("Self-heal shutdown", ex);
                }
                DateTime resumeAt = DateTime.UtcNow + restartDelay;
                while (DateTime.UtcNow < resumeAt)
                {
                    if (viewModel.CloseRequested)
                        return;
                    await Task.Delay(1000).ConfigureAwait(false);
                }
                client.LogInfo("Self-heal: restarting the mining core now");
                viewModel.Print("Self-heal: restarting the miner...");
            }
        }
        finally
        {
            viewModel.Print("Exiting...");
            try
            {
                await client.ShutdownAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                client.LogException("Final shutdown", ex);
            }
            client.Save(force: true);
            client.LogInfo("=== Cuckoo stopped ===");
        }
    }

    /// <summary>Waits (bounded) for the client run task to finish during window close.</summary>
    public async Task WaitForClientShutdownAsync()
    {
        if (_runTask is null)
            return;
        try
        {
            await _runTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            _client?.LogWarning("Client shutdown timed out");
        }
        catch (Exception ex)
        {
            _client?.LogException("Client shutdown", ex);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Instance.Exception("Unhandled UI exception", e.Exception);
        // swallow UI exceptions to keep the app (and the miner) alive
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        Logger.Instance.Dispose(); // flush pending log entries
        base.OnExit(e);
    }
}
