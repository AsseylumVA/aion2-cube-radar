using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using CubeRadar.Core.Update;

namespace CubeRadar.Cli;

/// <summary>
/// Looks for a newer "Cube Radar.exe" on GitHub (at start and every 6 hours, unless switched off) and, once the user
/// agrees, downloads it, checks its SHA-256 and hands over to it (see <see cref="SelfReplace"/> for why the running exe
/// is never touched). This is the radar's only connection to the internet; nothing goes to the game. Only the single
/// exe updates itself, never a build inside the repository.
/// For tests: CUBERADAR_UPDATE_API points the check at another URL, CUBERADAR_UPDATE_AUTOINSTALL=1 installs without
/// asking.
/// </summary>
internal sealed class Updater : IDisposable
{
    /// <summary>
    /// The old radar starts the downloaded exe with <c>--install-update &lt;installed exe&gt; [its own arguments …]</c>.
    /// Every later version must keep understanding this, because the old version decides how it is called.
    /// </summary>
    public const string InstallFlag = "--install-update";

    /// <summary>Added to the arguments of the installed exe when the installer starts it (logged, see RadarCommand).</summary>
    public const string AfterUpdateFlag = "--after-update";

    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(10), Interval = TimeSpan.FromHours(6),
        CheckTimeout = TimeSpan.FromSeconds(20), DownloadTimeout = TimeSpan.FromMinutes(10);

    private readonly RadarSettings _settings;
    private readonly string _exe;
    private readonly Action _quit;
    private readonly HttpClient _http;
    private readonly UpdateClient _client;
    private readonly bool _autoInstall;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _busy = new(1, 1);
    private Phase _phase;
    private string _arg = "";
    private string _reasonKey = "";

    private enum Phase { NotChecked, Off, Checking, Available, UpToDate, CheckFailed, Downloading, Failed, NoInstall }

    public Updater(RadarSettings settings, string exe, Action quit)
    {
        _settings = settings;
        _exe = exe;
        _quit = quit;
        // Timeouts per call (a check is short, the download is ~60 MB), not one for the whole client.
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CubeRadar", Current.ToString(3)));
        var api = Environment.GetEnvironmentVariable("CUBERADAR_UPDATE_API") is { Length: > 0 } url ? new Uri(url) : UpdateClient.GitHubLatest;
        _client = new UpdateClient(_http, api);
        _autoInstall = Environment.GetEnvironmentVariable("CUBERADAR_UPDATE_AUTOINSTALL") == "1";
        _phase = settings.UpdateCheckEnabled ? Phase.NotChecked : Phase.Off;
    }

    public static Version Current { get; } = UpdateClient.Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>A newer release, once a check found one.</summary>
    public ReleaseInfo? Available { get; private set; }

    /// <summary>What the updater is doing or found, for the settings window. Follows the active interface language.</summary>
    public string State => _phase switch
    {
        Phase.Off => UiText.T("updater.off"),
        Phase.Checking => UiText.T("updater.checking"),
        Phase.Available => UiText.T("updater.available", _arg),
        Phase.UpToDate => UiText.T("updater.current"),
        Phase.CheckFailed => UiText.T("updater.checkFailed"),
        Phase.Downloading => UiText.T("updater.downloading", _arg),
        Phase.Failed => UiText.T("updater.failed", _reasonKey.Length > 0 ? UiText.T(_reasonKey, _arg) : _arg),
        Phase.NoInstall => UiText.T("updater.none"),
        _ => UiText.T("updater.notChecked"),
    };

    public bool Installing { get; private set; }

    /// <summary>Raised on a worker thread whenever <see cref="Available"/>, <see cref="State"/> or <see cref="Installing"/> change.</summary>
    public event Action? Changed;

    /// <summary>Raised on a worker thread once per newly found version, so the overlay can show a notification.</summary>
    public event Action<ReleaseInfo>? Found;

    /// <summary>Removes leftovers of an earlier update, then checks now and every 6 hours while the check is on.</summary>
    public void Start() => Task.Run(async () =>
    {
        try
        {
            await Task.Delay(FirstCheck, _cts.Token);
            try
            {
                if (SelfReplace.CleanUp(_exe) is > 0 and var n) Log($"update: removed {n} file(s) left by the last update");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log($"update: cleanup failed: {ex.Message}");
            }
            while (true)
            {
                if (_settings.UpdateCheckEnabled) await CheckAsync();
                await Task.Delay(Interval, _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    });

    public void SetEnabled(bool on)
    {
        _settings.UpdateCheckEnabled = on;
        _settings.Save();
        Log($"update check {(on ? "on" : "off")}");
        if (on) _ = CheckAsync();
        else if (Available is null) Set(Phase.Off);
    }

    public async Task CheckAsync()
    {
        if (!await _busy.WaitAsync(0)) return; // a check or an install is running
        ReleaseInfo? found = null;
        try
        {
            Set(Phase.Checking);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(CheckTimeout);
            var latest = await _client.LatestAsync(timeout.Token);
            if (latest is not null && UpdateClient.IsNewer(latest, Current))
            {
                if (Available?.Version != latest.Version) found = latest;
                Available = latest;
                Set(Phase.Available, latest.Version.ToString(3));
                Log($"update: {latest.Tag} available{(latest.Installable ? "" : " (no verifiable exe, only the release page)")}");
            }
            else
            {
                Available = null;
                Set(Phase.UpToDate);
                Log("update: up to date");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            if (_cts.IsCancellationRequested) return;
            Set(Phase.CheckFailed);
            Log($"update check failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _busy.Release();
        }
        if (found is null) return;
        if (_autoInstall && found.Installable) await InstallAsync();
        else Found?.Invoke(found);
    }

    /// <summary>
    /// Downloads and verifies <see cref="Available"/> next to the exe, starts it as installer and quits this radar.
    /// </summary>
    /// <returns>Null when the installer was started; otherwise what went wrong, for the user.</returns>
    public async Task<string?> InstallAsync()
    {
        if (Available is not { Installable: true } release)
        {
            Set(Phase.NoInstall);
            return State;
        }
        await _busy.WaitAsync();
        var staged = SelfReplace.StagingPath(_exe);
        try
        {
            Installing = true;
            Set(Phase.Downloading, release.Version.ToString(3));
            Log($"update: downloading {release.AssetUrl}");
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            {
                timeout.CancelAfter(DownloadTimeout);
                await _client.DownloadAsync(release, staged, timeout.Token);
            }
            Log($"update: SHA-256 ok ({release.Sha256}), starting the installer");
            var start = new ProcessStartInfo(staged) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(_exe)! };
            start.ArgumentList.Add(InstallFlag);
            start.ArgumentList.Add(_exe);
            foreach (var arg in Environment.GetCommandLineArgs().Skip(1).Where(x => x != AfterUpdateFlag)) start.ArgumentList.Add(arg);
            Process.Start(start);
        }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException or OperationCanceledException or IOException
                                       or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Installing = false;
            SelfReplace.CleanUp(_exe);
            var (arg, key) = ex switch
            {
                UnauthorizedAccessException => ("", "updater.noWrite"),
                OperationCanceledException => ("", "updater.timeout"),
                HttpRequestException => ("", "updater.noGithub"),
                System.ComponentModel.Win32Exception => (ex.Message, "updater.startFailed"),
                _ => (ex.Message, ""),
            };
            Set(Phase.Failed, arg, key);
            Log($"update failed: {ex.GetType().Name}: {ex.Message}");
            return State;
        }
        finally
        {
            _busy.Release();
        }
        Log("update: installer started, quitting");
        _quit();
        return null;
    }

    /// <summary>
    /// The downloaded exe, started with <see cref="InstallFlag"/>: waits until the old radar has closed, puts itself in
    /// place of the installed exe and starts that. If it cannot, the old version starts again.
    /// </summary>
    public static int RunInstaller(string[] a)
    {
        AppPaths.Init();
        UiText.LoadInstalled();
        if (a.Length == 0 || Environment.ProcessPath is not { } self) return 2;
        var installed = a[0];
        var args = a[1..];
        try
        {
            // The old radar holds this lock until it has closed; afterwards only its process exit remains, which
            // Install waits out by retrying.
            if (Mutex.TryOpenExisting(RadarCommand.InstanceLockName, out var radar))
                using (radar)
                {
                    try
                    {
                        if (radar.WaitOne(TimeSpan.FromMinutes(1))) radar.ReleaseMutex();
                    }
                    catch (AbandonedMutexException)
                    {
                        radar.ReleaseMutex();
                    }
                }
            SelfReplace.Install(self, installed, TimeSpan.FromSeconds(30));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NativeConsole.Notify(UiText.T("updater.installFailed", ex.Message));
            return Launch(installed, args) ? 1 : 2;
        }
        return Launch(installed, [.. args, AfterUpdateFlag]) ? 0 : 2;
    }

    private static bool Launch(string exe, IEnumerable<string> args)
    {
        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            Process.Start(start);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            NativeConsole.Notify(UiText.T("updater.launchFailed", ex.Message, Path.GetFileName(exe)));
            return false;
        }
    }

    /// <summary>The page of the available release, else the list of releases.</summary>
    public void OpenPage()
    {
        try
        {
            Elevation.Open(Available?.PageUrl ?? UpdateClient.ReleasesPage);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log($"open {UpdateClient.ReleasesPage} in a browser");
        }
    }

    private void Set(Phase phase, string arg = "", string reasonKey = "")
    {
        _phase = phase;
        _arg = arg;
        _reasonKey = reasonKey;
        Changed?.Invoke();
    }

    private static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }
}
