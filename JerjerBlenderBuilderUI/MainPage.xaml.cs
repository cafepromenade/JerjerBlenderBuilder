using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace JerjerBlenderBuilderUI;

public sealed partial class MainPage : Page
{
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    private static readonly string RepoUrl = "https://github.com/blender/blender.git";

    public MainPage()
    {
        InitializeComponent();
        Loaded += (_, _) => RegisterWindowCloseGuard();
    }

    private void RegisterWindowCloseGuard()
    {
        var window = App.GetWindow();
        if (window is null) return;

        if (window.AppWindow is not null)
        {
            window.AppWindow.Closing += (_, args) =>
            {
                if (_isRunning)
                {
                    args.Cancel = true;
                }
            };
        }
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        var sourceDir = SourceDirBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(sourceDir))
        {
            await ShowError("Please enter a source directory.");
            return;
        }

        _isRunning = true;
        StartButton.IsEnabled = false;
        _cts = new CancellationTokenSource();
        LogOutput.Text = "";
        StepLabel.Text = "";
        ProgressBar.Value = 0;

        try
        {
            await RunBuild(sourceDir, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Build cancelled.");
        }
        catch (Exception ex)
        {
            await ShowError($"Build failed: {ex.Message}");
        }
        finally
        {
            _isRunning = false;
            StartButton.IsEnabled = true;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task RunBuild(string sourceDir, CancellationToken ct)
    {
        bool repoExists = Directory.Exists(sourceDir) && Directory.Exists(Path.Combine(sourceDir, ".git"));

        if (repoExists)
        {
            await RunStep(1, "Updating existing Blender source code...",
                () => RunProcessAsync("git", $"-C \"{sourceDir}\" pull", sourceDir, ct));
        }
        else
        {
            if (Directory.Exists(sourceDir))
                Directory.Delete(sourceDir, recursive: true);
            Directory.CreateDirectory(sourceDir);
            await RunStep(1, "Cloning Blender source code...",
                () => RunProcessAsync("git", $"clone {RepoUrl} \"{sourceDir}\"", sourceDir, ct));
        }

        await RunStep(2, "Initializing library dependencies...",
            () => RunProcessAsync("git", $"-C \"{sourceDir}\" lfs pull", sourceDir, ct));
        await RunStep(2, "Initializing library dependencies...",
            () => RunProcessAsync("git", $"-C \"{sourceDir}\" submodule update --init --force lib/windows_x64", sourceDir, ct));

        var buildDir = Path.Combine(sourceDir, "..", "build_blender_ci");
        var installDir = Path.Combine(sourceDir, "..", "install_blender_ci");
        Directory.CreateDirectory(buildDir);
        Directory.CreateDirectory(installDir);

        var cmakeArgs = $".. -G \"Visual Studio 17 2022\" -A x64 -T host=x64 "
                      + $"-C \"{sourceDir}/build_files/cmake/config/blender_release.cmake\" "
                      + $"-DWITH_INSTALL_PORTABLE=ON "
                      + $"-DCMAKE_INSTALL_PREFIX=\"{installDir}\"";
        await RunStep(3, "Configuring CMake...",
            () => RunProcessAsync("cmake", cmakeArgs, buildDir, ct));

        await RunStep(4, "Building Blender (this will take a while)...",
            () => RunProcessAsync("cmake", "cmake --build . --config Release -- /m:4 /p:UseExperimentalPCH=off", buildDir, ct));

        await RunStep(5, "Installing to staging directory...",
            () => RunProcessAsync("cmake", $"cmake --install . --config Release --prefix \"{installDir}\"", buildDir, ct));

        await RunStep(6, "Creating NSIS installer...", async () =>
        {
            var blenderExeDir = Path.Combine(installDir, "Release");
            var startupPath = Path.Combine(installDir, "Release", "release", "datafiles", "startup.blend");
            Directory.CreateDirectory(Path.GetDirectoryName(startupPath)!);
            var dummy = new byte[2000];
            new Random().NextBytes(dummy);
            File.WriteAllBytes(startupPath, dummy);

            var nsiArgs = $"/V4 /DINSTALL_DIR=\"{blenderExeDir}\" /DPRODUCT_VERSION=5.2 \"{sourceDir}\\build_files\\windows\\blender_installer.nsi\"";
            await RunProcessAsync("makensis", nsiArgs, sourceDir, ct);
        });

        AppendLog("========================================");
        AppendLog("  JERJER - Build complete!");
        AppendLog("  Installer: Blender_JERJER_Installer.exe");
        AppendLog("========================================");
    }

    private async Task RunStep(int step, string label, Func<Task> action)
    {
        ct().ThrowIfCancellationRequested();

        await DispatcherQueue.EnqueueAsync(() =>
        {
            ProgressBar.Value = step - 1;
            StepLabel.Text = $"[{step}/6] {label}";
            AppendLog($"--- [{step}/6] {label} ---");
        });

        await action();

        await DispatcherQueue.EnqueueAsync(() =>
        {
            ProgressBar.Value = step;
        });

        CancellationToken ct() => _cts?.Token ?? CancellationToken.None;
    }

    private async Task RunProcessAsync(string fileName, string arguments, string? workingDir, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource();

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDir ?? "",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
                _ = DispatcherQueue.EnqueueAsync(() => AppendLog(e.Data));
        };

        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                _ = DispatcherQueue.EnqueueAsync(() => AppendLog($"[ERR] {e.Data}"));
        };

        proc.Exited += (_, _) =>
        {
            if (proc.ExitCode == 0)
                tcs.TrySetResult();
            else
                tcs.TrySetException(new Exception($"Exit code {proc.ExitCode}"));
        };

        ct.Register(() =>
        {
            try { proc.Kill(); } catch { }
            tcs.TrySetCanceled();
        });

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await tcs.Task;
    }

    private void AppendLog(string line)
    {
        LogOutput.Text += line + "\n";
    }

    private async Task ShowError(string message)
    {
        await DispatcherQueue.EnqueueAsync(async () =>
        {
            var dialog = new ContentDialog
            {
                Title = "Error",
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = XamlRoot
            };
            await dialog.ShowAsync();
        });
    }

    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        picker.FileTypeFilter.Add("*");

        var window = App.GetWindow();
        if (window is null) return;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
            SourceDirBox.Text = folder.Path;
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        Application.Current.Exit();
    }
}

internal static class DispatcherQueueExtensions
{
    public static Task EnqueueAsync(this Microsoft.UI.Dispatching.DispatcherQueue queue, Action action)
    {
        var tcs = new TaskCompletionSource();
        if (!queue.TryEnqueue(() =>
        {
            try
            {
                action();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }))
        {
            tcs.TrySetException(new InvalidOperationException("Failed to enqueue dispatcher action"));
        }
        return tcs.Task;
    }
}
