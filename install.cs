// install.cs — Build revue and install as a Copilot CLI plugin
// Usage: dotnet run install.cs [-- <runtime-id>]

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

var scriptDir = GetScriptDir();
var repoRoot = Capture("git", $"-C {Quote(scriptDir)} rev-parse --show-toplevel").Trim().Replace('/', Path.DirectorySeparatorChar);
var srcDir = Path.Combine(repoRoot, "src");
var skillSourceDir = Path.Combine(repoRoot, "skill");

var rid = args.Length > 0 ? args[0] : GetDefaultRid();
var tempDir = Path.Combine(Path.GetTempPath(), $"revue-install-{Guid.NewGuid():N}");
var publishDir = Path.Combine(tempDir, "publish");
var pluginDir = Path.Combine(tempDir, "plugin");
var pluginSkillDir = Path.Combine(pluginDir, "skills", "revue");

try
{
    // Assemble the plugin layout in a temp directory
    Directory.CreateDirectory(pluginSkillDir);

    File.Copy(
        Path.Combine(skillSourceDir, "plugin.json"),
        Path.Combine(pluginDir, "plugin.json"));

    File.Copy(
        Path.Combine(skillSourceDir, "skills", "revue", "SKILL.md"),
        Path.Combine(pluginSkillDir, "SKILL.md"));

    CopyDirectory(
        Path.Combine(skillSourceDir, "skills", "revue", "scripts"),
        Path.Combine(pluginSkillDir, "scripts"));

    // Publish the binary
    Console.WriteLine($"Publishing revue ({rid})...");
    Console.WriteLine();
    Execute("dotnet", string.Join(" ", [
        "publish", Quote(srcDir),
        "-c", "Release",
        "-r", rid,
        "--self-contained",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:DebugType=none",
        "-o", Quote(publishDir)
    ]));

    // Copy only the executable into the plugin
    var exeName = rid.StartsWith("win") ? "revue.exe" : "revue";
    var publishedExe = Path.Combine(publishDir, exeName);
    if (!File.Exists(publishedExe))
    {
        Console.Error.WriteLine($"Error: expected {publishedExe} but it was not found.");
        return 1;
    }
    File.Copy(publishedExe, Path.Combine(pluginSkillDir, exeName));

    // Install the plugin
    Console.WriteLine();
    Console.WriteLine("Installing Copilot plugin...");
    Execute("copilot", $"plugin install {Quote(pluginDir)}");

    Console.WriteLine();
    Console.WriteLine("Done! Restart Copilot to pick up the revue plugin.");
    return 0;
}
catch (Exception ex) when (ex.Message.Contains("copilot"))
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"Plugin was built but install failed: {ex.Message}");
    Console.Error.WriteLine($"You can install manually with: copilot plugin install {Quote(pluginDir)}");
    Console.Error.WriteLine("(temp directory preserved for manual install)");
    return 1;
}
finally
{
    if (Directory.Exists(publishDir))
        Directory.Delete(publishDir, recursive: true);
}

static string GetScriptDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

static string GetDefaultRid()
{
    var arch = RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => throw new PlatformNotSupportedException(
            $"Unsupported architecture: {RuntimeInformation.OSArchitecture}")
    };

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return $"win-{arch}";
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return $"linux-{arch}";
    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return $"osx-{arch}";
    throw new PlatformNotSupportedException("Unsupported OS");
}

static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;

static void CopyDirectory(string source, string target)
{
    if (!Directory.Exists(source)) return;
    Directory.CreateDirectory(target);
    foreach (var file in Directory.GetFiles(source))
        File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
    foreach (var dir in Directory.GetDirectories(source))
        CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
}

string Capture(string command, string arguments)
{
    using var process = Process.Start(new ProcessStartInfo(command, arguments)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    }) ?? throw new InvalidOperationException($"Failed to start: {command}");

    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();

    return process.ExitCode != 0
        ? throw new InvalidOperationException(
            $"{command} failed (exit {process.ExitCode}): {process.StandardError.ReadToEnd()}")
        : output;
}

void Execute(string command, string arguments)
{
    using var process = Process.Start(new ProcessStartInfo(command, arguments)
    {
        UseShellExecute = false
    }) ?? throw new InvalidOperationException($"Failed to start: {command}");

    process.WaitForExit();

    if (process.ExitCode != 0)
        throw new InvalidOperationException($"{command} failed with exit code {process.ExitCode}");
}
