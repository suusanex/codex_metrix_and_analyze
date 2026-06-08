using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

return await Installer.RunAsync(args);

internal static class Installer
{
    private const string ManifestFileName = "codex-metrix-and-analyze.manifest.json";
    private const string HookTemplateRelativePath = "codex\\hooks.json";
    private const string LoggerSourceRelativePath = "hooks\\codex-agent-usage-logger.cs";
    private const string LoggerExecutableName = "codex-agent-usage-logger.exe";
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task<int> RunAsync(string[] args)
    {
        var options = InstallOptions.Parse(args);
        var repoRoot = FindRepoRoot();
        if (repoRoot is null)
        {
            Console.Error.WriteLine("Could not find repo root. Run this command from this repository.");
            return 1;
        }

        var codexHome = GetCodexHome();
        var targetHooksDir = Path.Combine(codexHome, "hooks");
        var targetLogsDir = Path.Combine(codexHome, "logs");
        var manifestPath = Path.Combine(codexHome, ManifestFileName);
        var backupRoot = options.BackupDirectory
            ?? Path.Combine(codexHome, "backups", "codex-metrix-and-analyze", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
        var loggerSourcePath = Path.Combine(repoRoot, LoggerSourceRelativePath);
        var tempPublishDir = Path.Combine(Path.GetTempPath(), $"codex-metrix-and-analyze-publish-{Guid.NewGuid():N}");
        var publishedExePath = Path.Combine(tempPublishDir, LoggerExecutableName);
        var hookExePath = Path.Combine(targetHooksDir, LoggerExecutableName);
        var generatedHooksJson = BuildHooksJson(hookExePath);

        Directory.CreateDirectory(targetHooksDir);

        var hookOperation = BuildGeneratedJsonOperation(codexHome, generatedHooksJson);
        var publishedExeOperation = BuildGeneratedOperation(targetHooksDir, LoggerExecutableName);
        var conflicts = new List<string>();

        if (hookOperation.RequiresInstall && hookOperation.TargetExists && !options.Force)
        {
            conflicts.Add(hookOperation.TargetPath);
        }

        if (publishedExeOperation.TargetExists && !options.Force)
        {
            conflicts.Add(publishedExeOperation.TargetPath);
        }

        PrintPlan(options, codexHome, targetLogsDir, backupRoot, manifestPath, tempPublishDir, hookOperation, publishedExeOperation);

        if (conflicts.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Conflicts detected:");
            foreach (var conflict in conflicts)
            {
                Console.WriteLine($"- {conflict}");
            }

            if (!options.DryRun)
            {
                Console.Error.WriteLine("Re-run with --force to replace the existing files.");
                return 1;
            }

            Console.WriteLine("Re-run with --force to apply the plan.");
        }

        if (options.DryRun)
        {
            return 0;
        }

        Directory.CreateDirectory(targetLogsDir);
        Directory.CreateDirectory(tempPublishDir);

        try
        {
            var publishExitCode = await PublishLoggerAsync(loggerSourcePath, tempPublishDir);
            if (publishExitCode != 0)
            {
                Console.Error.WriteLine("NativeAOT publish failed. Install was not applied.");
                return publishExitCode;
            }

            if (!File.Exists(publishedExePath))
            {
                Console.Error.WriteLine($"Publish did not produce the expected executable: {publishedExePath}");
                return 1;
            }

            var publishedExeHash = ComputeFileHash(publishedExePath);
            publishedExeOperation = publishedExeOperation with
            {
                InstalledHash = publishedExeHash,
                RequiresInstall = !publishedExeOperation.TargetExists || !StringComparer.Ordinal.Equals(publishedExeHash, publishedExeOperation.TargetHash)
            };

            if (publishedExeOperation.RequiresInstall && publishedExeOperation.TargetExists && !options.Force)
            {
                Console.Error.WriteLine($"Refusing to overwrite existing executable without --force: {publishedExeOperation.TargetPath}");
                return 1;
            }

            var manifest = new InstallManifest
            {
                InstalledAt = DateTimeOffset.UtcNow.ToString("O"),
                RepoRoot = repoRoot
            };

            ApplyGeneratedJsonOperation(hookOperation, generatedHooksJson, backupRoot, manifest);
            ApplyGeneratedOperation(publishedExeOperation, publishedExePath, backupRoot, manifest);

            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            var manifestJson = JsonSerializer.Serialize(manifest, InstallManifestJsonContext.Default.InstallManifest);
            await File.WriteAllTextAsync(manifestPath, manifestJson, Encoding.UTF8);
        }
        finally
        {
            if (Directory.Exists(tempPublishDir))
            {
                Directory.Delete(tempPublishDir, recursive: true);
            }
        }

        Console.WriteLine();
        Console.WriteLine("Install complete.");
        Console.WriteLine("Next steps:");
        Console.WriteLine("1. Open Codex and run /hooks to trust the new command hooks.");
        Console.WriteLine("2. Run `dotnet run --file scripts/test-hook.cs` from this repo.");
        Console.WriteLine($"3. Review {Path.Combine(codexHome, "logs", "agent-usage.jsonl")} after a Codex turn.");
        Console.WriteLine($"4. If the hook ever fails, inspect {Path.Combine(codexHome, "logs", "agent-usage-error.log")}.");
        return 0;
    }

    private static FileOperation BuildGeneratedJsonOperation(string codexHome, string generatedHooksJson)
    {
        var targetPath = Path.Combine(codexHome, "hooks.json");
        var generatedHash = ComputeContentHash(generatedHooksJson);
        var targetExists = File.Exists(targetPath);
        var targetHash = targetExists ? ComputeFileHash(targetPath) : null;

        return new FileOperation(
            Kind: "generated-json",
            RelativePath: HookTemplateRelativePath,
            SourcePath: null,
            TargetPath: targetPath,
            InstalledHash: generatedHash,
            TargetHash: targetHash,
            RequiresInstall: !targetExists || !StringComparer.Ordinal.Equals(generatedHash, targetHash),
            TargetExists: targetExists);
    }

    private static FileOperation BuildGeneratedOperation(string targetHooksDir, string fileName)
    {
        var targetPath = Path.Combine(targetHooksDir, fileName);
        var targetExists = File.Exists(targetPath);
        var targetHash = targetExists ? ComputeFileHash(targetPath) : null;

        return new FileOperation(
            Kind: "generated",
            RelativePath: $"generated/{fileName}",
            SourcePath: null,
            TargetPath: targetPath,
            InstalledHash: null,
            TargetHash: targetHash,
            RequiresInstall: true,
            TargetExists: targetExists);
    }

    private static void PrintPlan(
        InstallOptions options,
        string codexHome,
        string targetLogsDir,
        string backupRoot,
        string manifestPath,
        string tempPublishDir,
        FileOperation hookOperation,
        FileOperation publishedExeOperation)
    {
        Console.WriteLine(options.DryRun ? "Install dry-run:" : "Install plan:");
        Console.WriteLine($"- Codex home: {codexHome}");
        Console.WriteLine($"- Logs dir: {targetLogsDir}");
        Console.WriteLine($"- Error log: {Path.Combine(codexHome, "logs", "agent-usage-error.log")}");
        Console.WriteLine($"- Backup root: {backupRoot}");
        Console.WriteLine($"- Manifest: {manifestPath}");
        Console.WriteLine($"- Publish: {LoggerSourceRelativePath} -> {tempPublishDir} using --use-current-runtime");
        Console.WriteLine($"- Hook command: {publishedExeOperation.TargetPath}");
        Console.WriteLine($"- {DescribeAction(hookOperation)}: {hookOperation.RelativePath} -> {hookOperation.TargetPath}");
        Console.WriteLine($"- {DescribeAction(publishedExeOperation)}: {publishedExeOperation.RelativePath} -> {publishedExeOperation.TargetPath}");
    }

    private static string DescribeAction(FileOperation operation)
    {
        if (!operation.RequiresInstall)
        {
            return "keep";
        }

        return operation.TargetExists ? "overwrite" : "copy";
    }

    private static async Task<int> PublishLoggerAsync(string sourcePath, string outputDirectory)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("publish");
        startInfo.ArgumentList.Add(sourcePath);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--use-current-runtime");
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(outputDirectory);
        startInfo.ArgumentList.Add("--disable-build-servers");

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            Console.Error.WriteLine("Failed to start dotnet publish.");
            return 1;
        }

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            if (!string.IsNullOrWhiteSpace(stdout))
            {
                Console.Error.WriteLine(stdout.TrimEnd());
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                Console.Error.WriteLine(stderr.TrimEnd());
            }
        }

        return process.ExitCode;
    }

    private static void ApplyGeneratedJsonOperation(FileOperation operation, string generatedHooksJson, string backupRoot, InstallManifest manifest)
    {
        if (!operation.RequiresInstall)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(operation.TargetPath)!);
        string? backupPath = null;
        if (operation.TargetExists)
        {
            backupPath = Path.Combine(backupRoot, operation.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.Copy(operation.TargetPath, backupPath, overwrite: true);
        }

        File.WriteAllText(operation.TargetPath, generatedHooksJson, Utf8NoBom);
        manifest.Entries.Add(new ManifestEntry
        {
            Kind = operation.Kind,
            RelativeSourcePath = operation.RelativePath.Replace('\\', '/'),
            TargetPath = operation.TargetPath,
            BackupPath = backupPath,
            InstalledHash = operation.InstalledHash!
        });
    }

    private static void ApplyGeneratedOperation(FileOperation operation, string publishedExePath, string backupRoot, InstallManifest manifest)
    {
        if (!operation.RequiresInstall)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(operation.TargetPath)!);
        string? backupPath = null;
        if (operation.TargetExists)
        {
            backupPath = Path.Combine(backupRoot, operation.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.Copy(operation.TargetPath, backupPath, overwrite: true);
        }

        File.Copy(publishedExePath, operation.TargetPath, overwrite: true);
        manifest.Entries.Add(new ManifestEntry
        {
            Kind = operation.Kind,
            RelativeSourcePath = operation.RelativePath.Replace('\\', '/'),
            TargetPath = operation.TargetPath,
            BackupPath = backupPath,
            InstalledHash = operation.InstalledHash!
        });
    }

    private static string GetCodexHome()
    {
        var explicitHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(explicitHome))
        {
            return explicitHome;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    private static string? FindRepoRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            var hooksPath = Path.Combine(directory.FullName, "codex", "hooks.json");
            var apmPath = Path.Combine(directory.FullName, "apm.yml");
            if (File.Exists(hooksPath) && File.Exists(apmPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string ComputeFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeContentHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string BuildHooksJson(string hookExePath)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("hooks");
            writer.WriteStartObject();

            WriteHookEvent(writer, "SessionStart", "Logging session start", hookExePath, matcher: "*");
            WriteHookEvent(writer, "UserPromptSubmit", "Logging user prompt", hookExePath);
            WriteHookEvent(writer, "SubagentStart", "Logging subagent start", hookExePath, matcher: "*");
            WriteHookEvent(writer, "SubagentStop", "Logging subagent stop", hookExePath, matcher: "*");
            WriteHookEvent(writer, "PostToolUse", "Logging tool use", hookExePath, matcher: "Bash|apply_patch|Edit|Write");
            WriteHookEvent(writer, "Stop", "Logging turn stop", hookExePath);

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteHookEvent(Utf8JsonWriter writer, string eventName, string statusMessage, string hookExePath, string? matcher = null)
    {
        writer.WritePropertyName(eventName);
        writer.WriteStartArray();
        writer.WriteStartObject();

        if (!string.IsNullOrWhiteSpace(matcher))
        {
            writer.WriteString("matcher", matcher);
        }

        writer.WritePropertyName("hooks");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WriteString("type", "command");
        writer.WriteString("command", hookExePath);
        writer.WriteString("commandWindows", hookExePath);
        writer.WriteNumber("timeout", 10);
        writer.WriteString("statusMessage", statusMessage);
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndArray();
    }

    private sealed record FileOperation(
        string Kind,
        string RelativePath,
        string? SourcePath,
        string TargetPath,
        string? InstalledHash,
        string? TargetHash,
        bool RequiresInstall,
        bool TargetExists);
}

internal sealed class InstallOptions
{
    public bool DryRun { get; init; }
    public bool Force { get; init; }
    public string? BackupDirectory { get; init; }

    public static InstallOptions Parse(string[] args)
    {
        string? backupDirectory = null;
        var dryRun = false;
        var force = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--force":
                    force = true;
                    break;
                case "--backup-dir":
                    if (index + 1 >= args.Length)
                    {
                        throw new ArgumentException("--backup-dir requires a path");
                    }

                    backupDirectory = args[++index];
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[index]}");
            }
        }

        return new InstallOptions
        {
            DryRun = dryRun,
            Force = force,
            BackupDirectory = backupDirectory
        };
    }
}

internal sealed class InstallManifest
{
    public string InstalledAt { get; set; } = string.Empty;
    public string RepoRoot { get; set; } = string.Empty;
    public List<ManifestEntry> Entries { get; } = [];
}

internal sealed class ManifestEntry
{
    public string Kind { get; set; } = string.Empty;
    public string RelativeSourcePath { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public string? BackupPath { get; set; }
    public string InstalledHash { get; set; } = string.Empty;
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(InstallManifest))]
internal partial class InstallManifestJsonContext : JsonSerializerContext
{
}
