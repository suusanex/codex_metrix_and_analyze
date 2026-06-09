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
            await RunInstallerAsync(repoRoot, tempCodexHome);

            var loggerPath = Path.Combine(tempCodexHome, "hooks", "codex-agent-usage-logger.exe");
            var logBasePath = Path.Combine(tempCodexHome, "logs", "agent-observations.jsonl");
            var logPath = GetDailyLogPath(logBasePath);
            var errorLogBasePath = Path.Combine(tempCodexHome, "logs", "agent-observations-error.log");
            var errorLogPath = GetDailyLogPath(errorLogBasePath);
            var legacyLogBasePath = Path.Combine(tempCodexHome, "logs", "agent-usage.jsonl");
            var legacyLogPath = GetDailyLogPath(legacyLogBasePath);

            await ValidateHooksJsonAsync(tempCodexHome, loggerPath);

            var common = new HookPayloadContext("test-session", "turn-1", repoRoot, "workspace-write", "gpt-test");

            await InvokeHookAsync(loggerPath, tempCodexHome, logBasePath, errorLogBasePath, CreateSessionStart(common));
            await InvokeHookAsync(loggerPath, tempCodexHome, logBasePath, errorLogBasePath, CreateSubagentStart(common, "agent-sub", "delegate"));
            await Task.Delay(30);
            await InvokeHookAsync(loggerPath, tempCodexHome, logBasePath, errorLogBasePath, CreatePreToolUse(common, "agent-sub", "delegate", "tool-sub", "shell_command", "git rev-parse HEAD"));
            await Task.Delay(10);
            await InvokeHookAsync(loggerPath, tempCodexHome, logBasePath, errorLogBasePath, CreatePostToolUse(common, "agent-sub", "delegate", "tool-sub", "shell_command", "git rev-parse HEAD", 0, "abc123"));
            await Task.Delay(10);
            await InvokeHookAsync(loggerPath, tempCodexHome, logBasePath, errorLogBasePath, CreateSubagentStop(common, "agent-sub", "delegate"));

            await InvokeHookAsync(loggerPath, tempCodexHome, logBasePath, errorLogBasePath, CreatePreToolUse(common, "agent-root", "root", "tool-1", "shell_command", "git status --short"));
            await Task.Delay(30);
            await InvokeHookAsync(loggerPath, tempCodexHome, logBasePath, errorLogBasePath, CreatePostToolUse(common, "agent-root", "root", "tool-1", "shell_command", "git status --short", 0, "clean"));

            await InvokeHookAsync(loggerPath, tempCodexHome, logBasePath, errorLogBasePath, CreatePostToolUse(common, "agent-root", "root", "tool-2", "shell_command", "rg schema_version", 0, "hit"));
            await InvokeHookAsync(loggerPath, tempCodexHome, logBasePath, errorLogBasePath, CreatePreToolUse(
                common,
                "agent-root",
                "root",
                "tool-3",
                "shell_command",
                "curl -H \"Authorization: Bearer SECRET123456789\" https://example.com --token ghp_secretsecret123456"));
            await InvokeHookAsync(loggerPath, tempCodexHome, logBasePath, errorLogBasePath, CreateSubagentStop(common, "agent-orphan", "delegate"));

            await RunParallelAppendAsync(loggerPath, tempCodexHome, logBasePath, errorLogBasePath, common);

            var lines = await File.ReadAllLinesAsync(logPath, Encoding.UTF8);
            if (lines.Length < 27)
            {
                Console.Error.WriteLine($"Expected at least 27 log lines, found {lines.Length}.");
                return 1;
            }

            var documents = lines.Select(line => JsonDocument.Parse(line)).ToList();
            try
            {
                ValidateSchemaVersion(documents);
                ValidateMatchedTool(documents);
                ValidateMissingPre(documents);
                ValidateSubagentCorrelation(documents);
                ValidateOrphanSubagentStop(documents);
                ValidateRedaction(documents);
            }
            finally
            {
                foreach (var doc in documents)
                {
                    doc.Dispose();
                }
            }

            if (File.Exists(errorLogPath) || File.Exists(errorLogBasePath))
            {
                Console.Error.WriteLine("Error log should not exist before malformed payload test.");
                return 1;
            }

            var invalidExitCode = await InvokeHookAsync(loggerPath, tempCodexHome, logBasePath, errorLogBasePath, "{");
            if (invalidExitCode != 0)
            {
                Console.Error.WriteLine("Hook should return 0 even when payload parsing fails.");
                return 1;
            }

            if (!File.Exists(errorLogPath) && !File.Exists(errorLogBasePath))
            {
                Console.Error.WriteLine("Error log was not created for malformed payload.");
                return 1;
            }

            await File.WriteAllTextAsync(
                legacyLogPath,
                string.Join(
                    Environment.NewLine,
                    [
                        "{\"recorded_at\":\"2026-06-08T00:00:00.0000000+00:00\",\"event\":\"PostToolUse\",\"session_id\":\"legacy-session\",\"agent_type\":\"parent\",\"model\":\"gpt-old\",\"cwd\":\"" + EscapeJson(repoRoot) + "\",\"tool_name\":\"shell_command\",\"command\":\"git diff\",\"tool_response_size\":40}",
                        "{\"recorded_at\":\"2026-06-08T00:00:05.0000000+00:00\",\"event\":\"SubagentStop\",\"session_id\":\"legacy-session\",\"agent_id\":\"legacy-agent\",\"agent_type\":\"delegate\",\"model\":\"gpt-old\",\"cwd\":\"" + EscapeJson(repoRoot) + "\",\"duration_ms\":1200}"
                    ]) + Environment.NewLine,
                Encoding.UTF8);

            await ValidateReportAsync(repoRoot, logPath, legacyLogPath);

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

    private static async Task ValidateReportAsync(string repoRoot, string logPath, string legacyLogPath)
    {
        var tempDirectory = Path.GetDirectoryName(logPath) ?? repoRoot;
        var filteredToolFixturePath = Path.Combine(tempDirectory, "filtered-tool-fixture.jsonl");
        var filteredSubagentFixturePath = Path.Combine(tempDirectory, "filtered-subagent-fixture.jsonl");

        var jsonOutput = await RunDotnetAsync(
            repoRoot,
            "run",
            "--file",
            Path.Combine(repoRoot, "scripts", "codex-agent-usage-report.cs"),
            "--",
            "--log",
            logPath,
            "--log",
            legacyLogPath,
            "--format",
            "json",
            "--limit-delta",
            "12.5",
            "--limit-unit",
            "percent",
            "--allocation-basis",
            "weighted");

        using var reportDocument = JsonDocument.Parse(jsonOutput.StandardOutput);
        var root = reportDocument.RootElement;
        EnsureProperty(root, "summary");
        EnsureProperty(root, "model_rows");
        EnsureProperty(root, "agent_type_rows");
        EnsureProperty(root, "tool_rows");
        EnsureProperty(root, "repository_rows");
        EnsureProperty(root, "timeline_rows");
        EnsureProperty(root, "correlation_issues");
        EnsureProperty(root, "allocation");
        EnsureProperty(root, "warnings");
        EnsureProperty(root, "not_available");

        var allocation = root.GetProperty("allocation");
        if (allocation.GetProperty("limit_delta").GetDouble() <= 0)
        {
            throw new InvalidOperationException("Allocation limit_delta was not emitted.");
        }

        var issues = root.GetProperty("correlation_issues").EnumerateArray().ToArray();
        if (!issues.Any(issue =>
                issue.GetProperty("status").GetString() == "missing_post" &&
                issue.GetProperty("count").GetInt32() >= 1))
        {
            throw new InvalidOperationException("Report did not detect missing_post.");
        }

        if (!issues.Any(issue =>
                issue.GetProperty("status").GetString() == "legacy_unavailable"))
        {
            throw new InvalidOperationException("Report did not emit legacy_unavailable correlation issue.");
        }

        await File.WriteAllTextAsync(
            filteredToolFixturePath,
            string.Join(
                Environment.NewLine,
                [
                    "{\"schema_version\":\"2.0\",\"recorded_at\":\"2026-06-09T00:00:00.0000000+00:00\",\"event\":\"PreToolUse\",\"session_id\":\"filtered-tool\",\"turn_id\":\"turn-1\",\"agent_id\":\"agent-root\",\"agent_type\":\"root\",\"model\":\"gpt-test\",\"cwd\":\"" + EscapeJson(repoRoot) + "\",\"tool_use_id\":\"tool-filtered\",\"tool_name\":\"shell_command\",\"tool_correlation_status\":\"unknown\"}",
                    "{\"schema_version\":\"2.0\",\"recorded_at\":\"2026-06-09T00:00:02.0000000+00:00\",\"event\":\"PostToolUse\",\"session_id\":\"filtered-tool\",\"turn_id\":\"turn-1\",\"agent_id\":\"agent-root\",\"agent_type\":\"root\",\"model\":\"gpt-test\",\"cwd\":\"" + EscapeJson(repoRoot) + "\",\"tool_use_id\":\"tool-filtered\",\"tool_name\":\"shell_command\",\"tool_correlation_status\":\"matched\",\"tool_elapsed_ms\":2000,\"tool_response_size\":20}"
                ]) + Environment.NewLine,
            Encoding.UTF8);

        var filteredToolOutput = await RunDotnetAsync(
            repoRoot,
            "run",
            "--file",
            Path.Combine(repoRoot, "scripts", "codex-agent-usage-report.cs"),
            "--",
            "--log",
            filteredToolFixturePath,
            "--format",
            "json",
            "--from",
            "2026-06-09T00:00:01Z");

        using var filteredToolDocument = JsonDocument.Parse(filteredToolOutput.StandardOutput);
        var filteredToolSummary = filteredToolDocument.RootElement.GetProperty("summary");
        if (filteredToolSummary.GetProperty("matched_tool_invocations").GetInt32() < 1)
        {
            throw new InvalidOperationException("Filtered v2 report did not preserve matched tool correlation.");
        }

        if (filteredToolSummary.GetProperty("missing_pre").GetInt32() != 0)
        {
            throw new InvalidOperationException("Filtered v2 report incorrectly counted missing_pre.");
        }

        await File.WriteAllTextAsync(
            filteredSubagentFixturePath,
            string.Join(
                Environment.NewLine,
                [
                    "{\"schema_version\":\"2.0\",\"recorded_at\":\"2026-06-09T00:00:00.0000000+00:00\",\"event\":\"SubagentStart\",\"session_id\":\"filtered-subagent\",\"turn_id\":\"turn-1\",\"agent_id\":\"agent-sub\",\"agent_type\":\"delegate\",\"model\":\"gpt-test\",\"cwd\":\"" + EscapeJson(repoRoot) + "\",\"subagent_run_id\":\"run-1\",\"subagent_correlation_status\":\"unknown\"}",
                    "{\"schema_version\":\"2.0\",\"recorded_at\":\"2026-06-09T00:00:02.0000000+00:00\",\"event\":\"SubagentStop\",\"session_id\":\"filtered-subagent\",\"turn_id\":\"turn-1\",\"agent_id\":\"agent-sub\",\"agent_type\":\"delegate\",\"model\":\"gpt-test\",\"cwd\":\"" + EscapeJson(repoRoot) + "\",\"subagent_run_id\":\"run-1\",\"subagent_correlation_status\":\"matched\",\"subagent_duration_ms\":2000}"
                ]) + Environment.NewLine,
            Encoding.UTF8);

        var filteredSubagentOutput = await RunDotnetAsync(
            repoRoot,
            "run",
            "--file",
            Path.Combine(repoRoot, "scripts", "codex-agent-usage-report.cs"),
            "--",
            "--log",
            filteredSubagentFixturePath,
            "--format",
            "json",
            "--from",
            "2026-06-09T00:00:01Z");

        using var filteredSubagentDocument = JsonDocument.Parse(filteredSubagentOutput.StandardOutput);
        var filteredSubagentSummary = filteredSubagentDocument.RootElement.GetProperty("summary");
        if (filteredSubagentSummary.GetProperty("missing_start").GetInt32() != 0)
        {
            throw new InvalidOperationException("Filtered v2 report incorrectly counted missing_start.");
        }

        var filteredAgentRows = filteredSubagentDocument.RootElement
            .GetProperty("agent_type_rows")
            .EnumerateArray()
            .ToArray();
        var delegateRow = filteredAgentRows.FirstOrDefault(row => row.GetProperty("name").GetString() == "delegate");
        if (delegateRow.ValueKind == JsonValueKind.Undefined ||
            delegateRow.GetProperty("total_subagent_duration_ms").GetInt64() <= 0)
        {
            throw new InvalidOperationException("Filtered v2 report did not preserve matched subagent duration.");
        }

        var markdownOutput = await RunDotnetAsync(
            repoRoot,
            "run",
            "--file",
            Path.Combine(repoRoot, "scripts", "codex-agent-usage-report.cs"),
            "--",
            "--log",
            logPath,
            "--format",
            "markdown",
            "--limit-delta",
            "4",
            "--allocation-basis",
            "tool-count");

        if (!markdownOutput.StandardOutput.Contains("Limit delta allocation", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Markdown report did not contain allocation section.");
        }
    }

    private static void ValidateSchemaVersion(List<JsonDocument> documents)
    {
        if (documents.Any(doc => doc.RootElement.GetProperty("schema_version").GetString() != "2.0"))
        {
            throw new InvalidOperationException("Expected schema_version 2.0 for all logger lines.");
        }
    }

    private static void ValidateMatchedTool(List<JsonDocument> documents)
    {
        var matched = documents
            .Select(doc => doc.RootElement)
            .FirstOrDefault(element =>
                element.GetProperty("event").GetString() == "PostToolUse" &&
                element.TryGetProperty("tool_use_id", out var toolUseId) &&
                toolUseId.GetString() == "tool-1");

        if (matched.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException("Matched PostToolUse was not logged.");
        }

        if (matched.GetProperty("tool_correlation_status").GetString() != "matched")
        {
            throw new InvalidOperationException("Matched tool correlation status was not recorded.");
        }

        if (matched.GetProperty("tool_elapsed_ms").GetInt64() <= 0)
        {
            throw new InvalidOperationException("tool_elapsed_ms was not recorded.");
        }

        if (matched.GetProperty("command_kind").GetString() != "git")
        {
            throw new InvalidOperationException("command_kind was not classified.");
        }

        if (matched.GetProperty("subagent_span_id").ValueKind != JsonValueKind.Null)
        {
            throw new InvalidOperationException("Parent tool event unexpectedly had subagent_span_id.");
        }

        if (matched.GetProperty("parent_span_id").GetString() != matched.GetProperty("turn_span_id").GetString())
        {
            throw new InvalidOperationException("Parent tool event did not use turn span as parent.");
        }
    }

    private static void ValidateMissingPre(List<JsonDocument> documents)
    {
        var missingPre = documents
            .Select(doc => doc.RootElement)
            .FirstOrDefault(element =>
                element.GetProperty("event").GetString() == "PostToolUse" &&
                element.TryGetProperty("tool_use_id", out var toolUseId) &&
                toolUseId.GetString() == "tool-2");

        if (missingPre.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException("missing_pre PostToolUse was not logged.");
        }

        if (missingPre.GetProperty("tool_correlation_status").GetString() != "missing_pre")
        {
            throw new InvalidOperationException("missing_pre correlation status was not recorded.");
        }
    }

    private static void ValidateSubagentCorrelation(List<JsonDocument> documents)
    {
        var stop = documents
            .Select(doc => doc.RootElement)
            .FirstOrDefault(element =>
                element.GetProperty("event").GetString() == "SubagentStop" &&
                element.GetProperty("agent_id").GetString() == "agent-sub");

        if (stop.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException("SubagentStop for matched run was not logged.");
        }

        if (stop.GetProperty("subagent_correlation_status").GetString() != "matched")
        {
            throw new InvalidOperationException("SubagentStop correlation was not matched.");
        }

        if (stop.GetProperty("subagent_duration_ms").GetInt64() <= 0)
        {
            throw new InvalidOperationException("subagent_duration_ms was not recorded.");
        }

        var subagentTool = documents
            .Select(doc => doc.RootElement)
            .FirstOrDefault(element =>
                element.GetProperty("event").GetString() == "PostToolUse" &&
                element.GetProperty("agent_id").GetString() == "agent-sub" &&
                element.TryGetProperty("tool_use_id", out var toolUseId) &&
                toolUseId.GetString() == "tool-sub");

        if (subagentTool.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException("Subagent PostToolUse was not logged.");
        }

        var subagentSpanId = subagentTool.GetProperty("subagent_span_id").GetString();
        if (string.IsNullOrWhiteSpace(subagentSpanId))
        {
            throw new InvalidOperationException("Subagent tool event did not carry subagent_span_id.");
        }

        if (subagentTool.GetProperty("parent_span_id").GetString() != subagentSpanId)
        {
            throw new InvalidOperationException("Subagent tool event did not use subagent span as parent.");
        }
    }

    private static void ValidateOrphanSubagentStop(List<JsonDocument> documents)
    {
        var orphan = documents
            .Select(doc => doc.RootElement)
            .FirstOrDefault(element =>
                element.GetProperty("event").GetString() == "SubagentStop" &&
                element.GetProperty("agent_id").GetString() == "agent-orphan");

        if (orphan.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException("Orphan SubagentStop was not logged.");
        }

        if (orphan.GetProperty("subagent_correlation_status").GetString() != "missing_start")
        {
            throw new InvalidOperationException("Orphan SubagentStop was not marked missing_start.");
        }
    }

    private static void ValidateRedaction(List<JsonDocument> documents)
    {
        var redacted = documents
            .Select(doc => doc.RootElement)
            .FirstOrDefault(element =>
                element.GetProperty("event").GetString() == "PreToolUse" &&
                element.TryGetProperty("tool_use_id", out var toolUseId) &&
                toolUseId.GetString() == "tool-3");

        if (redacted.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException("Redaction test line was not logged.");
        }

        var command = redacted.GetProperty("command_redacted").GetString() ?? string.Empty;
        var preview = redacted.GetProperty("tool_input_preview").GetString() ?? string.Empty;
        if (command.Contains("SECRET123456789", StringComparison.Ordinal) ||
            command.Contains("ghp_secretsecret123456", StringComparison.Ordinal) ||
            preview.Contains("SECRET123456789", StringComparison.Ordinal) ||
            preview.Contains("ghp_secretsecret123456", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Secrets were not redacted from command or preview.");
        }

        if (!command.Contains("[REDACTED]", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Redacted command did not include redaction marker.");
        }
    }

    private static async Task RunParallelAppendAsync(
        string loggerPath,
        string codexHome,
        string logPath,
        string errorLogPath,
        HookPayloadContext common)
    {
        var tasks = Enumerable.Range(0, 20)
            .Select(index => InvokeHookAsync(
                loggerPath,
                codexHome,
                logPath,
                errorLogPath,
                CreateSessionStart(common with { SessionId = $"parallel-{index}", TurnId = $"turn-{index}" })))
            .ToArray();

        var exitCodes = await Task.WhenAll(tasks);
        if (exitCodes.Any(code => code != 0))
        {
            throw new InvalidOperationException("Parallel append test encountered non-zero exit code.");
        }
    }

    private static async Task ValidateHooksJsonAsync(string codexHome, string loggerPath)
    {
        var hooksJsonPath = Path.Combine(codexHome, "hooks.json");
        if (!File.Exists(hooksJsonPath))
        {
            throw new InvalidOperationException("Generated hooks.json was not created.");
        }

        var hooksJson = await File.ReadAllTextAsync(hooksJsonPath, Encoding.UTF8);
        if (hooksJson.Contains("%USERPROFILE%", StringComparison.Ordinal) ||
            hooksJson.Contains("~/.codex", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Generated hooks.json still contains unresolved placeholders.");
        }

        using var doc = JsonDocument.Parse(hooksJson);
        var hooksRoot = doc.RootElement.GetProperty("hooks");
        ValidateHookEvent(hooksRoot, "SessionStart", loggerPath, expectedMatcher: "*");
        ValidateHookEvent(hooksRoot, "UserPromptSubmit", loggerPath, expectedMatcher: null);
        ValidateHookEvent(hooksRoot, "SubagentStart", loggerPath, expectedMatcher: "*");
        ValidateHookEvent(hooksRoot, "SubagentStop", loggerPath, expectedMatcher: "*");
        ValidateHookEvent(hooksRoot, "PreToolUse", loggerPath, expectedMatcher: "*");
        ValidateHookEvent(hooksRoot, "PostToolUse", loggerPath, expectedMatcher: "*");
        ValidateHookEvent(hooksRoot, "Stop", loggerPath, expectedMatcher: null);
    }

    private static void ValidateHookEvent(JsonElement hooksRoot, string eventName, string loggerPath, string? expectedMatcher)
    {
        if (!hooksRoot.TryGetProperty(eventName, out var eventArray) || eventArray.ValueKind != JsonValueKind.Array || eventArray.GetArrayLength() == 0)
        {
            throw new InvalidOperationException($"Generated hooks.json is missing hook event '{eventName}'.");
        }

        var firstEntry = eventArray[0];
        if (expectedMatcher is null)
        {
            if (firstEntry.TryGetProperty("matcher", out var matcherElement) &&
                matcherElement.ValueKind != JsonValueKind.Null &&
                !string.IsNullOrWhiteSpace(matcherElement.GetString()))
            {
                throw new InvalidOperationException($"Generated hooks.json unexpectedly set matcher for '{eventName}'.");
            }
        }
        else
        {
            var matcher = firstEntry.TryGetProperty("matcher", out var matcherElement)
                ? matcherElement.GetString()
                : null;
            if (!StringComparer.Ordinal.Equals(matcher, expectedMatcher))
            {
                throw new InvalidOperationException($"Generated hooks.json matcher for '{eventName}' was '{matcher}', expected '{expectedMatcher}'.");
            }
        }

        var commandWindows = firstEntry
            .GetProperty("hooks")[0]
            .GetProperty("commandWindows")
            .GetString();

        if (!StringComparer.OrdinalIgnoreCase.Equals(commandWindows, loggerPath))
        {
            throw new InvalidOperationException($"Generated hooks.json event '{eventName}' does not point to the installed logger.");
        }
    }

    private static async Task RunInstallerAsync(string repoRoot, string codexHome)
    {
        var result = await RunDotnetAsync(
            repoRoot,
            "run",
            "--file",
            Path.Combine(repoRoot, "scripts", "apply-hooks-config.cs"),
            "--",
            "--force",
            new Dictionary<string, string?>
            {
                ["CODEX_HOME"] = codexHome
            });

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Install failed.{Environment.NewLine}{result.StandardError}{Environment.NewLine}{result.StandardOutput}");
        }
    }

    private static async Task<int> InvokeHookAsync(
        string loggerPath,
        string codexHome,
        string logPath,
        string errorLogPath,
        string payloadJson)
    {
        var startInfo = new ProcessStartInfo(loggerPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.Environment["CODEX_HOME"] = codexHome;
        startInfo.Environment["CODEX_AGENT_OBSERVATION_LOG"] = logPath;
        startInfo.Environment["CODEX_AGENT_OBSERVATION_ERROR_LOG"] = errorLogPath;

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("failed to start hook executable");
        await process.StandardInput.WriteAsync(payloadJson);
        process.StandardInput.Close();
        await process.StandardOutput.ReadToEndAsync();
        await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static async Task<ProcessResult> RunDotnetAsync(
        string repoRoot,
        params string[] arguments)
    {
        return await RunDotnetAsync(repoRoot, arguments, environment: null);
    }

    private static async Task<ProcessResult> RunDotnetAsync(
        string repoRoot,
        string firstArgument,
        string secondArgument,
        string thirdArgument,
        string fourthArgument,
        string fifthArgument,
        string sixthArgument,
        string seventhArgument,
        string eighthArgument,
        string ninthArgument,
        string tenthArgument,
        Dictionary<string, string?>? environment = null)
    {
        return await RunDotnetAsync(
            repoRoot,
            [firstArgument, secondArgument, thirdArgument, fourthArgument, fifthArgument, sixthArgument, seventhArgument, eighthArgument, ninthArgument, tenthArgument],
            environment);
    }

    private static async Task<ProcessResult> RunDotnetAsync(
        string repoRoot,
        string[] arguments,
        Dictionary<string, string?>? environment)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = repoRoot
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (environment is not null)
        {
            foreach (var entry in environment)
            {
                startInfo.Environment[entry.Key] = entry.Value;
            }
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("failed to start dotnet process");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static async Task<ProcessResult> RunDotnetAsync(
        string repoRoot,
        string arg1,
        string arg2,
        string arg3,
        string arg4,
        string arg5,
        Dictionary<string, string?>? environment = null)
    {
        return await RunDotnetAsync(repoRoot, [arg1, arg2, arg3, arg4, arg5], environment);
    }

    private static async Task<ProcessResult> RunDotnetAsync(
        string repoRoot,
        string arg1,
        string arg2,
        string arg3,
        string arg4,
        string arg5,
        string arg6,
        Dictionary<string, string?>? environment = null)
    {
        return await RunDotnetAsync(repoRoot, [arg1, arg2, arg3, arg4, arg5, arg6], environment);
    }

    private static string CreateSessionStart(HookPayloadContext context)
    {
        return SerializeJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("hook_event_name", "SessionStart");
            writer.WriteString("session_id", context.SessionId);
            writer.WriteString("turn_id", context.TurnId);
            writer.WriteString("agent_id", "agent-root");
            writer.WriteString("agent_type", "root");
            writer.WriteString("model", context.Model);
            writer.WriteString("cwd", context.Cwd);
            writer.WriteString("permission_mode", context.PermissionMode);
            writer.WriteEndObject();
        });
    }

    private static string CreateSubagentStart(HookPayloadContext context, string agentId, string agentType)
    {
        return SerializeAgentEvent(context, "SubagentStart", agentId, agentType);
    }

    private static string CreateSubagentStop(HookPayloadContext context, string agentId, string agentType)
    {
        return SerializeAgentEvent(context, "SubagentStop", agentId, agentType);
    }

    private static string SerializeAgentEvent(HookPayloadContext context, string eventName, string agentId, string agentType)
    {
        return SerializeJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("hook_event_name", eventName);
            writer.WriteString("session_id", context.SessionId);
            writer.WriteString("turn_id", context.TurnId);
            writer.WriteString("agent_id", agentId);
            writer.WriteString("agent_type", agentType);
            writer.WriteString("model", context.Model);
            writer.WriteString("cwd", context.Cwd);
            writer.WriteString("permission_mode", context.PermissionMode);
            writer.WriteEndObject();
        });
    }

    private static string CreatePreToolUse(
        HookPayloadContext context,
        string agentId,
        string agentType,
        string toolUseId,
        string toolName,
        string command)
    {
        return SerializeJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("hook_event_name", "PreToolUse");
            writer.WriteString("session_id", context.SessionId);
            writer.WriteString("turn_id", context.TurnId);
            writer.WriteString("agent_id", agentId);
            writer.WriteString("agent_type", agentType);
            writer.WriteString("model", context.Model);
            writer.WriteString("cwd", context.Cwd);
            writer.WriteString("permission_mode", context.PermissionMode);
            writer.WriteString("tool_use_id", toolUseId);
            writer.WriteString("tool_name", toolName);
            writer.WritePropertyName("tool_input");
            writer.WriteStartObject();
            writer.WriteString("command", command);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    private static string CreatePostToolUse(
        HookPayloadContext context,
        string agentId,
        string agentType,
        string toolUseId,
        string toolName,
        string command,
        int exitCode,
        string stdout)
    {
        return SerializeJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("hook_event_name", "PostToolUse");
            writer.WriteString("session_id", context.SessionId);
            writer.WriteString("turn_id", context.TurnId);
            writer.WriteString("agent_id", agentId);
            writer.WriteString("agent_type", agentType);
            writer.WriteString("model", context.Model);
            writer.WriteString("cwd", context.Cwd);
            writer.WriteString("permission_mode", context.PermissionMode);
            writer.WriteString("tool_use_id", toolUseId);
            writer.WriteString("tool_name", toolName);
            writer.WritePropertyName("tool_input");
            writer.WriteStartObject();
            writer.WriteString("command", command);
            writer.WriteEndObject();
            writer.WritePropertyName("tool_response");
            writer.WriteStartObject();
            writer.WriteString("stdout", stdout);
            writer.WriteNumber("exitCode", exitCode);
            writer.WriteString("token", "ghp_secretsecret123456");
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    private static void EnsureProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out _))
        {
            throw new InvalidOperationException($"Expected property '{propertyName}' in report output.");
        }
    }

    private static string EscapeJson(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal);
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

    private static string GetDailyLogPath(string basePath)
    {
        var directory = Path.GetDirectoryName(basePath);
        var fileName = Path.GetFileName(basePath);
        var date = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        var extension = Path.GetExtension(fileName);
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var dailyFileName = string.IsNullOrWhiteSpace(extension)
            ? $"{fileNameWithoutExtension}-{date}"
            : $"{fileNameWithoutExtension}-{date}{extension}";
        return string.IsNullOrWhiteSpace(directory) ? dailyFileName : Path.Combine(directory, dailyFileName);
    }
}

internal sealed record HookPayloadContext(
    string SessionId,
    string TurnId,
    string Cwd,
    string PermissionMode,
    string Model);

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
