using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

return await Uninstaller.RunAsync(args);

internal static class Uninstaller
{
    private const string ManifestFileName = "codex-metrix-and-analyze.manifest.json";
    private const string LegacyManifestFileName = "codex-local-config.manifest.json";

    public static async Task<int> RunAsync(string[] args)
    {
        var options = UninstallOptions.Parse(args);
        var codexHome = GetCodexHome();
        var normalizedCodexHome = Path.GetFullPath(codexHome);
        var manifestInfos = await LoadManifestsAsync(codexHome);

        if (manifestInfos.Count == 0)
        {
            Console.WriteLine("No hook install manifest found.");
            Console.WriteLine("Nothing to uninstall. Expected:");
            Console.WriteLine($"- {Path.Combine(codexHome, ManifestFileName)}");
            Console.WriteLine($"- {Path.Combine(codexHome, LegacyManifestFileName)}");
            return 0;
        }

        Console.WriteLine(options.DryRun ? "Uninstall dry-run:" : "Uninstall plan:");
        foreach (var (manifestPath, manifest) in manifestInfos)
        {
            Console.WriteLine($"- Manifest: {manifestPath}");
            foreach (var entry in manifest.Entries)
            {
                if (!TryResolveAndValidatePath(codexHome, normalizedCodexHome, entry.TargetPath, out _))
                {
                    Console.WriteLine($"[warning] Skipped out-of-scope uninstall target: {entry.TargetPath}");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(entry.BackupPath) && !TryResolveAndValidatePath(codexHome, normalizedCodexHome, entry.BackupPath, out _))
                {
                    Console.WriteLine($"[warning] Skipped restore from out-of-scope backup path: {entry.BackupPath}");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(entry.BackupPath))
                {
                    Console.WriteLine($"- restore: {entry.TargetPath} <- {entry.BackupPath}");
                }
                else
                {
                    Console.WriteLine($"- remove: {entry.TargetPath}");
                }
            }
        }

        if (options.DryRun)
        {
            return 0;
        }

        var processedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifestInfo in manifestInfos)
        {
            foreach (var entry in manifestInfo.Manifest.Entries.AsEnumerable().Reverse())
            {
                if (!TryResolveAndValidatePath(codexHome, normalizedCodexHome, entry.TargetPath, out _))
                {
                    Console.WriteLine($"[warning] Skipped out-of-scope uninstall target: {entry.TargetPath}");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(entry.BackupPath) && !TryResolveAndValidatePath(codexHome, normalizedCodexHome, entry.BackupPath, out _))
                {
                    Console.WriteLine($"[warning] Skipped restore from out-of-scope backup path: {entry.BackupPath}");
                    continue;
                }

                if (!processedTargets.Add(entry.TargetPath))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(entry.BackupPath) && File.Exists(entry.BackupPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(entry.TargetPath)!);
                    File.Copy(entry.BackupPath, entry.TargetPath, overwrite: true);
                    continue;
                }

                if (!File.Exists(entry.TargetPath))
                {
                    continue;
                }

                var currentHash = ComputeHash(entry.TargetPath);
                if (StringComparer.Ordinal.Equals(currentHash, entry.InstalledHash))
                {
                    File.Delete(entry.TargetPath);
                }
                else
                {
                    Console.WriteLine($"Skipped modified file: {entry.TargetPath}");
                }
            }
        }

        foreach (var manifestInfo in manifestInfos)
        {
            File.Delete(manifestInfo.Path);
        }

        Console.WriteLine("Uninstall complete.");
        return 0;
    }

    private static async Task<List<(string Path, InstallManifest Manifest)>> LoadManifestsAsync(string codexHome)
    {
        var manifestInfos = new List<(string, InstallManifest)>();
        var manifestPaths = new[]
        {
            Path.Combine(codexHome, ManifestFileName),
            Path.Combine(codexHome, LegacyManifestFileName)
        };

        foreach (var manifestPath in manifestPaths)
        {
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            var manifest = JsonSerializer.Deserialize(await File.ReadAllTextAsync(manifestPath), InstallManifestJsonContext.Default.InstallManifest)
                ?? new InstallManifest();

            manifestInfos.Add((manifestPath, manifest));
        }

        return manifestInfos;
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

    private static string ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool TryResolveAndValidatePath(string basePath, string normalizedBase, string path, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            resolvedPath = Path.GetFullPath(path, basePath);
        }
        catch
        {
            return false;
        }

        var normalizedPath = EnsureTrailingSeparator(Path.GetFullPath(resolvedPath));
        var normalizedRoot = EnsureTrailingSeparator(normalizedBase);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        if (path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar))
        {
            return path;
        }

        return path + Path.DirectorySeparatorChar;
    }
}

internal sealed class UninstallOptions
{
    public bool DryRun { get; init; }

    public static UninstallOptions Parse(string[] args)
    {
        foreach (var arg in args)
        {
            if (!arg.Equals("--dry-run", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

        return new UninstallOptions
        {
            DryRun = args.Contains("--dry-run", StringComparer.Ordinal)
        };
    }
}

internal sealed class InstallManifest
{
    public string InstalledAt { get; set; } = string.Empty;
    public string RepoRoot { get; set; } = string.Empty;
    public List<ManifestEntry> Entries { get; set; } = [];
}

internal sealed class ManifestEntry
{
    public string Kind { get; set; } = string.Empty;
    public string RelativeSourcePath { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public string? BackupPath { get; set; }
    public string InstalledHash { get; set; } = string.Empty;
}

[JsonSerializable(typeof(InstallManifest))]
internal partial class InstallManifestJsonContext : JsonSerializerContext
{
}
