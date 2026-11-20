using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Velopack;
using Velopack.Sources;

namespace RemoteMan.App.Updates;

public enum UpdateState
{
    /// <summary>Nothing to show.</summary>
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToRestart,
    Failed,
}

/// <summary>
/// Checks GitHub Releases for new versions, downloads them in the background and restarts into them.
/// One instance for the whole app; the update banner and the Help menu bind to it.
/// </summary>
public sealed partial class UpdateService : ObservableObject
{
    public const string RepositoryUrl = "https://github.com/Langeboi/RemoteMan";
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);

    public static UpdateService Instance { get; } = new();

    private readonly UpdatePrefs _prefs = UpdatePrefs.Load();
    private readonly DispatcherTimer _timer = new();
    private UpdateManager? _manager;
    private UpdateInfo? _pending;
    private CancellationTokenSource? _downloadCts;

    private UpdateService()
    {
        CurrentVersion = ReadCurrentVersion();
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = CheckInterval;
            if (_prefs.AutoCheck) await CheckAsync(userInitiated: false);
        };
    }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsVisible), nameof(IsAvailable), nameof(IsDownloading), nameof(IsReady), nameof(IsFailed), nameof(IsInfo))]
    private UpdateState _state;

    [ObservableProperty] private string _currentVersion;
    [ObservableProperty] private string? _newVersion;
    [ObservableProperty] private string? _releaseNotes;
    [ObservableProperty] private string? _sizeText;
    [ObservableProperty] private int _progress;
    [ObservableProperty] private string? _message;

    public bool IsVisible => State is not (UpdateState.Idle);
    public bool IsAvailable => State == UpdateState.Available;
    public bool IsDownloading => State == UpdateState.Downloading;
    public bool IsReady => State == UpdateState.ReadyToRestart;
    public bool IsFailed => State == UpdateState.Failed;
    /// <summary>Checking or up to date: a short informational line after Help > Check for updates.</summary>
    public bool IsInfo => State is UpdateState.Checking or UpdateState.UpToDate;

    public bool AutoCheck
    {
        get => _prefs.AutoCheck;
        set
        {
            if (_prefs.AutoCheck == value) return;
            _prefs.AutoCheck = value;
            _prefs.Save();
            OnPropertyChanged();
        }
    }

    public string ReleasesUrl => RepositoryUrl + "/releases";

    /// <summary>Starts background checks. Call once after the main window is shown.</summary>
    public void Start()
    {
        _timer.Interval = FirstCheckDelay;
        _timer.Start();
    }

    private UpdateManager? Manager
    {
        get
        {
            if (_manager is not null) return _manager;
            try
            {
                var mgr = new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: _prefs.IncludePrereleases));
                // A copy run from the build folder or an unzipped artifact isn't installed and can't self-update.
                if (!mgr.IsInstalled) return null;
                CurrentVersion = mgr.CurrentVersion?.ToString() ?? CurrentVersion;
                return _manager = mgr;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    [RelayCommand]
    private Task CheckNow() => CheckAsync(userInitiated: true);

    private async Task CheckAsync(bool userInitiated)
    {
        if (State is UpdateState.Checking or UpdateState.Downloading or UpdateState.ReadyToRestart) return;

        var mgr = Manager;
        if (mgr is null)
        {
            if (userInitiated) Fail("Updates work in the installed version of RemoteMan. This copy isn't installed.");
            return;
        }

        if (userInitiated) State = UpdateState.Checking;
        try
        {
            var info = await mgr.CheckForUpdatesAsync();
            _prefs.LastCheck = DateTimeOffset.Now;
            _prefs.Save();

            if (info is null)
            {
                if (userInitiated) ShowUpToDate();
                return;
            }

            var version = info.TargetFullRelease.Version.ToString();
            if (!userInitiated && version == _prefs.SkippedVersion) return;

            _pending = info;
            NewVersion = version;
            ReleaseNotes = string.IsNullOrWhiteSpace(info.TargetFullRelease.NotesMarkdown) ? null : info.TargetFullRelease.NotesMarkdown.Trim();
            SizeText = FormatSize(info.DeltasToTarget is { Length: > 0 } d ? d.Sum(x => x.Size) : info.TargetFullRelease.Size);
            State = UpdateState.Available;
        }
        catch (Exception ex)
        {
            // Background checks fail quietly (offline, GitHub rate limit); only a manual check reports it.
            if (userInitiated) Fail("Couldn't check for updates: " + ex.Message);
            else if (State == UpdateState.Checking) State = UpdateState.Idle;
        }
    }

    [RelayCommand]
    private async Task Update()
    {
        var mgr = Manager;
        if (mgr is null || _pending is null) return;

        _downloadCts = new CancellationTokenSource();
        Progress = 0;
        State = UpdateState.Downloading;
        var dispatcher = Application.Current.Dispatcher;
        try
        {
            await mgr.DownloadUpdatesAsync(_pending, p => dispatcher.BeginInvoke(() => Progress = p), cancelToken: _downloadCts.Token);
            State = UpdateState.ReadyToRestart;
        }
        catch (OperationCanceledException)
        {
            State = UpdateState.Available;
        }
        catch (Exception ex)
        {
            Fail("The update didn't download: " + ex.Message);
        }
        finally
        {
            _downloadCts.Dispose();
            _downloadCts = null;
        }
    }

    [RelayCommand]
    private void CancelDownload() => _downloadCts?.Cancel();

    [RelayCommand]
    private void RestartNow()
    {
        var mgr = Manager;
        if (mgr is null || _pending is null) return;
        try
        {
            // Velopack waits for this process to exit, swaps the files, then starts the new version.
            // Shutting down normally lets open sessions and the data source close cleanly first.
            mgr.WaitExitThenApplyUpdates(_pending.TargetFullRelease, silent: true, restart: true);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Fail("Couldn't start the update: " + ex.Message);
        }
    }

    /// <summary>Hides the banner. A downloaded update is applied the next time RemoteMan starts.</summary>
    [RelayCommand]
    private void Later()
    {
        if (State != UpdateState.ReadyToRestart) _pending = null;
        State = UpdateState.Idle;
    }

    [RelayCommand]
    private void SkipVersion()
    {
        _prefs.SkippedVersion = NewVersion;
        _prefs.Save();
        _pending = null;
        State = UpdateState.Idle;
    }

    [RelayCommand]
    private void Dismiss() => State = _pending is not null && State == UpdateState.Failed ? UpdateState.Available : UpdateState.Idle;

    [RelayCommand]
    private void OpenReleaseNotes()
    {
        var url = NewVersion is null ? ReleasesUrl : $"{ReleasesUrl}/tag/v{NewVersion}";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { }
    }

    private void Fail(string message)
    {
        Message = message;
        State = UpdateState.Failed;
    }

    private async void ShowUpToDate()
    {
        State = UpdateState.UpToDate;
        await Task.Delay(TimeSpan.FromSeconds(4));
        if (State == UpdateState.UpToDate) State = UpdateState.Idle;
    }

    private static string? FormatSize(long bytes) =>
        bytes <= 0 ? null : bytes < 1024 * 1024 ? $"{Math.Max(1, bytes / 1024)} KB" : $"{bytes / (1024.0 * 1024):0.#} MB";

    private static string ReadCurrentVersion()
    {
        var info = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (info is null) return "dev";
        var plus = info.IndexOf('+');
        return plus > 0 ? info[..plus] : info;
    }
}
