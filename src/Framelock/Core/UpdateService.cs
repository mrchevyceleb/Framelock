using System.Diagnostics;
using System.IO;
using System.Text;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace Framelock.Core;

/// <summary>Downloads updates without restarting a running recorder; applies them on the next normal launch.</summary>
public sealed class UpdateService : ObservableObject
{
    public const string RepositoryUrl = "https://github.com/mrchevyceleb/Framelock";
    private readonly UpdateManager _manager = new(new GithubSource(RepositoryUrl, null, false));
    private readonly CancellationTokenSource _stop = new();
    private string _statusText;
    private bool _checking;

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public bool IsChecking { get => _checking; private set => Set(ref _checking, value); }

    public UpdateService()
    {
        _statusText = _manager.IsInstalled
            ? "Updates download automatically and install when you next open Framelock."
            : "Install the GitHub release to enable automatic updates.";
    }

    /// <summary>Called only by the first instance, before creating any capture or recording state.</summary>
    public void ApplyPendingAtStartup(string[] args, string instanceMutex)
    {
        if (args.Contains("--skip-update-once")) return;
        try
        {
            if (_manager.IsInstalled && _manager.UpdatePendingRestart is { } pending)
            {
                Log.Info($"Applying downloaded update {pending.Version}");
                ApplyWithInstanceGuard(pending, args, instanceMutex);
            }
        }
        catch (Exception ex) { Log.Warn("Applying update failed; continuing with the current version: " + ex.Message); }
    }

    private static void ApplyWithInstanceGuard(VelopackAsset pending, string[] args, string instanceMutex)
    {
        var locator = VelopackLocator.Current;
        var updater = locator.UpdateExePath ?? throw new InvalidOperationException("Updater path missing.");
        var root = locator.RootAppDir ?? throw new InvalidOperationException("Installation path missing.");
        var package = Path.Combine(locator.PackagesDir!, pending.FileName);
        var launcher = Path.Combine(locator.AppContentDir!, "Framelock.exe");
        string readyName = "Framelock.UpdateReady." + Guid.NewGuid().ToString("N");
        string goName = readyName + ".Go";
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
        using var go = new EventWaitHandle(false, EventResetMode.ManualReset, goName);
        string updaterArgs = "--silent apply --norestart --waitPid " + Environment.ProcessId +
            " --package " + QuoteArgument(package) + " --rootDir " + QuoteArgument(root);
        string restartArgs = string.Join(" ", args.Where(a => a != "--skip-update-once").Append("--skip-update-once").Select(QuoteArgument));
        static string Ps(string value) => "'" + value.Replace("'", "''") + "'";
        // The helper lives outside the installation directory. Keeping this mutex handle open
        // blocks additional launches until Update.exe finishes replacing the application files.
        // It does not own the mutex, so it survives the parent's exit without abandonment.
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            $guard = [Threading.Mutex]::OpenExisting({{Ps(instanceMutex)}})
            $ready = [Threading.EventWaitHandle]::OpenExisting({{Ps(readyName)}})
            $go = [Threading.EventWaitHandle]::OpenExisting({{Ps(goName)}})
            try {
                $ready.Set() | Out-Null
                if (-not $go.WaitOne(10000)) { exit 1 }
                $info = New-Object Diagnostics.ProcessStartInfo
                $info.FileName = {{Ps(updater)}}
                $info.Arguments = {{Ps(updaterArgs)}}
                $info.UseShellExecute = $false
                $info.CreateNoWindow = $true
                $update = [Diagnostics.Process]::Start($info)
                $update.WaitForExit()
            } catch {
                # Relaunch the current version if the updater could not start. The one-shot
                # skip argument below prevents a failing updater from hiding the app in a loop.
            } finally { $guard.Dispose(); $ready.Dispose(); $go.Dispose() }
            $info = New-Object Diagnostics.ProcessStartInfo
            $info.FileName = {{Ps(launcher)}}
            $info.Arguments = {{Ps(restartArgs)}}
            $info.UseShellExecute = $false
            $info.CreateNoWindow = $true
            [Diagnostics.Process]::Start($info) | Out-Null
            """;
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System)
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(arg);
        using var helper = Process.Start(start) ?? throw new IOException("Could not start the update helper.");
        if (!ready.WaitOne(10000)) throw new IOException("Update helper did not become ready; continuing without installing the update.");
        go.Set();
        Environment.Exit(0);
    }

    // Windows argv quoting preserves spaces, embedded quotes and trailing backslashes.
    private static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    public async Task RunAsync()
    {
        if (!_manager.IsInstalled) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), _stop.Token);
            while (!_stop.IsCancellationRequested)
            {
                await CheckAsync();
                await Task.Delay(TimeSpan.FromHours(6), _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async Task CheckAsync()
    {
        if (IsChecking || _stop.IsCancellationRequested || !_manager.IsInstalled) return;
        IsChecking = true;
        StatusText = "Checking for updates…";
        try
        {
            var update = await _manager.CheckForUpdatesAsync();
            if (_stop.IsCancellationRequested) return;
            if (update == null)
            {
                StatusText = _manager.UpdatePendingRestart is { } pending
                    ? $"Version {pending.Version} is ready. It installs when you next open Framelock."
                    : "Framelock is up to date. Updates are checked automatically.";
                return;
            }
            StatusText = $"Downloading version {update.TargetFullRelease.Version}…";
            await _manager.DownloadUpdatesAsync(update, cancelToken: _stop.Token);
            StatusText = $"Version {update.TargetFullRelease.Version} is ready. It installs when you next open Framelock.";
            Log.Info("Update downloaded: " + update.TargetFullRelease.Version);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Warn("Update check/download failed: " + ex.Message);
            StatusText = "Updates couldn't be checked. We'll try again automatically, or you can check now.";
        }
        finally { IsChecking = false; }
    }

    public void Stop() => _stop.Cancel();
}
