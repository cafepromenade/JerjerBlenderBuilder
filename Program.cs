using System.Diagnostics;
using System.Security.Principal;
using System.Runtime.InteropServices;

[DllImport("kernel32.dll")]
static extern bool AttachConsole(uint dwProcessId);

[DllImport("kernel32.dll")]
static extern bool AllocConsole();

[DllImport("kernel32.dll")]
static extern bool FreeConsole();

if (!AttachConsole(0xFFFFFFFF))
    AllocConsole();

Console.WriteLine("========================================");
Console.WriteLine("      JERJER BLENDER BUILDER v1.0");
Console.WriteLine("========================================");
Console.WriteLine();

bool isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
    .IsInRole(WindowsBuiltInRole.Administrator);

Console.WriteLine($"Running as administrator: {isAdmin}");
Console.WriteLine();

Console.Write("Enter folder path to download/clone Blender source: ");
string? sourceDir = Console.ReadLine()?.Trim();
if (string.IsNullOrWhiteSpace(sourceDir))
{
    Console.WriteLine("No folder specified. Exiting.");
    return;
}

string blenderRepoUrl = "https://github.com/blender/blender.git";

bool repoExists = Directory.Exists(sourceDir) && Directory.Exists(Path.Combine(sourceDir, ".git"));

if (repoExists)
{
    Console.WriteLine();
    Console.WriteLine("[1/6] Updating existing Blender source code...");
    RunProcess("git", $"-C \"{sourceDir}\" pull");
}
else
{
    if (Directory.Exists(sourceDir))
        Directory.Delete(sourceDir, recursive: true);
    Directory.CreateDirectory(sourceDir);
    Console.WriteLine();
    Console.WriteLine("[1/6] Cloning Blender source code...");
    RunProcess("git", $"clone {blenderRepoUrl} \"{sourceDir}\"");
}

Console.WriteLine();
Console.WriteLine("[2/6] Initializing library dependencies...");
RunProcess("git", $"-C \"{sourceDir}\" lfs pull");
RunProcess("git", $"-C \"{sourceDir}\" submodule update --init --force lib/windows_x64");

Console.WriteLine();
Console.WriteLine("[3/6] Configuring CMake...");
string buildDir = Path.Combine(sourceDir, "..", "build_blender_ci");
Directory.CreateDirectory(buildDir);
string installDir = Path.Combine(sourceDir, "..", "install_blender_ci");
Directory.CreateDirectory(installDir);

string cmakeConfig = $"cmake .. -G \"Visual Studio 17 2022\" -A x64 -T host=x64 "
    + $"-C \"{sourceDir}/build_files/cmake/config/blender_release.cmake\" "
    + $"-DWITH_INSTALL_PORTABLE=ON "
    + $"-DCMAKE_INSTALL_PREFIX=\"{installDir}\"";
RunProcess("cmake", cmakeConfig, buildDir);

Console.WriteLine();
Console.WriteLine("[4/6] Building Blender (this will take a while)...");
RunProcess("cmake", "cmake --build . --config Release -- /m:4 /p:UseExperimentalPCH=off", buildDir);

Console.WriteLine();
Console.WriteLine("[5/6] Installing to staging directory...");
RunProcess("cmake", $"cmake --install . --config Release --prefix \"{installDir}\"", buildDir);

Console.WriteLine();
Console.WriteLine("[6/6] Creating NSIS installer...");
// Create placeholder startup.blend
string blenderExeDir = Path.Combine(installDir, "Release");
string startupPath = Path.Combine(installDir, "Release", "release", "datafiles", "startup.blend");
Directory.CreateDirectory(Path.GetDirectoryName(startupPath)!);
byte[] dummy = new byte[2000];
new Random().NextBytes(dummy);
File.WriteAllBytes(startupPath, dummy);

RunProcess("makensis",
    $"/V4 /DINSTALL_DIR=\"{blenderExeDir}\" /DPRODUCT_VERSION=5.2 \"{sourceDir}\\build_files\\windows\\blender_installer.nsi\"",
    sourceDir);

Console.WriteLine();
Console.WriteLine("========================================");
Console.WriteLine("  JERJER");
Console.WriteLine("  Installer created: Blender_JERJER_Installer.exe");
Console.WriteLine("========================================");

Console.WriteLine();
Console.WriteLine("Press any key to exit...");
Console.ReadKey();

static void RunProcess(string fileName, string arguments, string? workingDir = null)
{
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

    using var proc = new Process { StartInfo = psi };
    proc.OutputDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine(e.Data); };
    proc.ErrorDataReceived += (s, e) => { if (e.Data != null) Console.Error.WriteLine(e.Data); };

    proc.Start();
    proc.BeginOutputReadLine();
    proc.BeginErrorReadLine();
    proc.WaitForExit();

    if (proc.ExitCode != 0)
    {
        Console.Error.WriteLine($"ERROR: {fileName} exited with code {proc.ExitCode}");
        Environment.Exit(proc.ExitCode);
    }
}
