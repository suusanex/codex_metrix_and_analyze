using System.Diagnostics;
using System.Text;
using System.Text.Json;

return await HookTester.RunAsync();

internal static class HookTester
{
    public static async Task<int> RunAsync()
    {
        var repoRoot = FindRepoRoot();
        if (repoRoot is null)
        {
            Console.Error.WriteLine("Could not find repo root.");
            return 1;
        }

            var tempCodexHome = Path.Combine(Path.GetTempPath(), $"codex-metrix-and-analyze-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempCodexHome);

        try
        {
            var installExitCode = await RunInstallerAsync(repoRoot, tempCodexHome);
            if (installExitCode != 0)
            {
                return installExitCode;
            }

            var loggerPath = Path.Combine(tempCodexHome, "hooks", "codex-agent-usage-logger.exe");
            var logPath = Path.Combine(tempCodexHome, "logs", "agent-usage.jsonl");
            var errorLogPath = Path.Combine(tempCodexHome, "logs", "agent-usage-error.log");
            var hooksJsonPath = Path.Combine(tempCodexHome, "hooks.json");

            if (!File.Exists(hooksJsonPath))
            {
                Console.Error.WriteLine("Generated hooks.json was not created.");
                return 1;
            }

            var hooksJson = await File.ReadAllTextAsync(hooksJsonPath);
            if (hooksJson.Contains("%USERPROFILE%", StringComparison.Ordinal) || hooksJson.Contains("~/.codex", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Generated hooks.json still contains unresolved path placeholders.");
                return 1;
            }

            using (var hooksDocument = JsonDocument.Parse(hooksJson))
            {
                var commandWindows = hooksDocument.RootElement
                    .GetProperty("hooks")
                    .GetProperty("UserPromptSubmit")[0]
                    .GetProperty("hooks")[0]
                    .GetProperty("commandWindows")
                    .GetString();
                if (!StringComparer.OrdinalIgnoreCase.Equals(commandWindows, loggerPath))
                {
                    Console.Error.WriteLine("Generated hooks.json does not point to the installed executable.");
                    return 1;
                }
            }

            await InvokeHookAsync(loggerPath, tempCodexHome, logPath, errorLogPath, SerializeJson(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("hook_event_name", "SessionStart");
                writer.WriteString("session_id", "test-session");
                writer.WriteString("turn_id", "turn-1");
                writer.WriteString("agent_id", "agent-root");
                writer.WriteString("agent_type", "root");
                writer.WriteString("model", "gpt-test");
                writer.WriteString("cwd", repoRoot);
                writer.WriteString("permission_mode", "workspace-write");
                writer.WriteEndObject();
            }));

            await InvokeHookAsync(loggerPath, tempCodexHome, logPath, errorLogPath, SerializeJson(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("hook_event_name", "SubagentStart");
                writer.WriteString("session_id", "test-session");
                writer.WriteString("turn_id", "turn-1");
                writer.WriteString("agent_id", "agent-sub");
                writer.WriteString("agent_type", "delegate");
                writer.WriteString("model", "gpt-test");
                writer.WriteString("cwd", repoRoot);
                writer.WriteString("permission_mode", "workspace-write");
                writer.WriteEndObject();
            }));

            await Task.Delay(25);

            await InvokeHookAsync(loggerPath, tempCodexHome, logPath, errorLogPath, SerializeJson(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("hook_event_name", "SubagentStop");
                writer.WriteString("session_id", "test-session");
                writer.WriteString("turn_id", "turn-1");
                writer.WriteString("agent_id", "agent-sub");
                writer.WriteString("agent_type", "delegate");
                writer.WriteString("model", "gpt-test");
                writer.WriteString("cwd", repoRoot);
                writer.WriteString("permission_mode", "workspace-write");
                writer.WriteEndObject();
            }));

            await InvokeHookAsync(loggerPath, tempCodexHome, logPath, errorLogPath, SerializeJson(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("hook_event_name", "PostToolUse");
                writer.WriteString("session_id", "test-session");
                writer.WriteString("turn_id", "turn-1");
                writer.WriteString("agent_id", "agent-root");
                writer.WriteString("agent_type", "root");
                writer.WriteString("model", "gpt-test");
                writer.WriteString("cwd", repoRoot);
                writer.WriteString("permission_mode", "workspace-write");
                writer.WriteString("tool_name", "shell_command");
                writer.WritePropertyName("tool_input");
                writer.WriteStartObject();
                writer.WriteString("command", "git status --short");
                writer.WriteEndObject();
                writer.WritePropertyName("tool_response");
                writer.WriteStartObject();
                writer.WriteString("stdout", "sample output");
                writer.WriteNumber("exitCode", 0);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }));

            if (!File.Exists(logPath))
            {
                Console.Error.WriteLine("Log file was not created.");
                return 1;
            }

            if (File.Exists(errorLogPath))
            {
                Console.Error.WriteLine("Error log should not exist after successful hook runs.");
                return 1;
            }

            var lines = await File.ReadAllLinesAsync(logPath);
            if (lines.Length != 4)
            {
                Console.Error.WriteLine($"Expected 4 log lines, found {lines.Length}.");
                return 1;
            }

            using var subagentStop = JsonDocument.Parse(lines[2]);
            var durationElement = subagentStop.RootElement.GetProperty("duration_ms");
            if (durationElement.ValueKind != JsonValueKind.Number || durationElement.GetInt32() <= 0)
            {
                Console.Error.WriteLine("Subagent duration was not recorded.");
                return 1;
            }

            using var toolUse = JsonDocument.Parse(lines[3]);
            if (toolUse.RootElement.GetProperty("command").GetString() != "git status --short")
            {
                Console.Error.WriteLine("Tool command was not captured.");
                return 1;
            }

            if (!toolUse.RootElement.TryGetProperty("tool_response_preview", out _))
            {
                Console.Error.WriteLine("Tool response preview was not recorded.");
                return 1;
            }

            var invalidPayloadExitCode = await InvokeHookAsync(loggerPath, tempCodexHome, logPath, errorLogPath, "{");
            if (invalidPayloadExitCode != 0)
            {
                Console.Error.WriteLine("Hook should return 0 even when payload parsing fails.");
                return 1;
            }

            if (!File.Exists(errorLogPath))
            {
                Console.Error.WriteLine("Error log was not created for the malformed payload.");
                return 1;
            }

            var errorLines = await File.ReadAllLinesAsync(errorLogPath);
            if (errorLines.Length != 1)
            {
                Console.Error.WriteLine($"Expected 1 error log line, found {errorLines.Length}.");
                return 1;
            }

            using var errorDocument = JsonDocument.Parse(errorLines[0]);
            if (errorDocument.RootElement.GetProperty("error_kind").GetString() != "payload-parse-failed")
            {
                Console.Error.WriteLine("Unexpected error kind in error log.");
                return 1;
            }

            Console.WriteLine($"Smoke test passed. Temp CODEX_HOME: {tempCodexHome}");
            return 0;
        }
        finally
        {
            if (Directory.Exists(tempCodexHome))
            {
                Directory.Delete(tempCodexHome, recursive: true);
            }
        }
    }

    private static async Task<int> RunInstallerAsync(string repoRoot, string codexHome)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = repoRoot
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--file");
        startInfo.ArgumentList.Add(Path.Combine(repoRoot, "scripts", "apply-hooks-config.cs"));
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("--force");
        startInfo.Environment["CODEX_HOME"] = codexHome;

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("failed to start install process");
        var stderr = await process.StandardError.ReadToEndAsync();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Install failed.{Environment.NewLine}{stderr}{Environment.NewLine}{stdout}");
        }

        return process.ExitCode;
    }

    private static async Task<int> InvokeHookAsync(string loggerPath, string codexHome, string logPath, string errorLogPath, string payloadJson)
    {
        var startInfo = new ProcessStartInfo(loggerPath)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.Environment["CODEX_HOME"] = codexHome;
        startInfo.Environment["CODEX_AGENT_USAGE_LOG"] = logPath;
        startInfo.Environment["CODEX_AGENT_USAGE_ERROR_LOG"] = errorLogPath;

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("failed to start hook executable");
        await process.StandardInput.WriteAsync(payloadJson);
        process.StandardInput.Close();

        await process.StandardError.ReadToEndAsync();
        await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return process.ExitCode;
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

    private static string SerializeJson(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
