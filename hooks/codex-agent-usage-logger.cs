using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

return await ProgramEntry.RunAsync();

internal static class ProgramEntry
{
    private const string SchemaVersion = "2.0";
    private const string LoggerName = "codex-agent-observation-logger";
    private const string LoggerVersion = "2.0.0";
    private const int PreviewLimit = 240;
    private const int LogMutexRetryDelayMilliseconds = 50;
    private const int LogMutexAcquireTimeoutMilliseconds = 5_000;
    private static readonly string[] KnownPayloadKeys =
    [
        "hook_event_name",
        "session_id",
        "turn_id",
        "agent_id",
        "agent_type",
        "model",
        "permission_mode",
        "cwd",
        "transcript_path",
        "tool_use_id",
        "tool_name",
        "tool_input",
        "tool_response",
        "source",
        "stop_reason",
        "prompt",
        "message",
        "reason",
        "decision",
        "requested_tool",
        "escalation_kind",
        "trigger",
        "before",
        "after",
        "timestamp"
    ];

    public static async Task<int> RunAsync()
    {
        string? rawInput = null;
        JsonDocument? document = null;

        try
        {
            rawInput = await Console.In.ReadToEndAsync();
            document = JsonDocument.Parse(rawInput);
            await HandlePayloadAsync(document.RootElement, rawInput);
        }
        catch (JsonException ex)
        {
            TryAppendErrorLog("payload-parse-failed", ex, rawInput, null);
        }
        catch (Exception ex)
        {
            TryAppendErrorLog("hook-processing-failed", ex, rawInput, document?.RootElement);
        }
        finally
        {
            document?.Dispose();
        }

        return 0;
    }

    private static async Task HandlePayloadAsync(JsonElement payload, string rawInput)
    {
        var settings = ObservationSettings.Load();
        settings.EnsureDirectories();
        CleanupStaleStateFiles(settings.ToolStateDirectory);
        CleanupStaleStateFiles(settings.SubagentStateDirectory);

        var now = DateTimeOffset.UtcNow;
        var eventName = GetString(payload, "hook_event_name") ?? "Unknown";
        var sessionId = GetString(payload, "session_id");
        var turnId = GetString(payload, "turn_id");
        var agentId = NormalizeAgentId(GetString(payload, "agent_id"));
        var agentType = GetString(payload, "agent_type");
        var model = GetString(payload, "model");
        var permissionMode = GetString(payload, "permission_mode");
        var cwd = GetString(payload, "cwd");
        var transcriptPath = GetString(payload, "transcript_path");
        var payloadKeys = payload.EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var unknownPayloadKeys = payloadKeys
            .Where(key => Array.IndexOf(KnownPayloadKeys, key) < 0)
            .ToArray();

        var redactedRawPayload = RedactSecrets(rawInput);
        var rawPayloadSize = Encoding.UTF8.GetByteCount(rawInput);
        var rawPayloadHash = HashString(redactedRawPayload);

        var repo = TryGetRepositoryMetadata(cwd);
        var transcript = TryGetTranscriptMetadata(transcriptPath, settings);
        var tool = BuildToolObservation(payload, settings);
        var toolCorrelation = await CorrelateToolAsync(
            eventName,
            now,
            sessionId,
            turnId,
            agentId,
            tool.ToolUseId,
            tool.ToolName,
            settings);
        var subagentCorrelation = await CorrelateSubagentAsync(
            eventName,
            now,
            sessionId,
            turnId,
            agentId,
            agentType,
            model,
            settings);

        var traceId = CreateTraceId(sessionId);
        var turnSpanId = CreateSpanId("turn", sessionId, turnId);
        var subagentSpanId = CreateSpanId("subagent", subagentCorrelation.SubagentRunId, null);
        var toolSpanId = CreateSpanId("tool", tool.ToolUseId, null);
        var parentSpanId = !string.IsNullOrWhiteSpace(subagentSpanId)
            ? subagentSpanId
            : turnSpanId;
        var spanId = !string.IsNullOrWhiteSpace(toolSpanId)
            ? toolSpanId
            : !string.IsNullOrWhiteSpace(subagentSpanId)
                ? subagentSpanId
                : turnSpanId;

        var line = SerializeJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", SchemaVersion);
            writer.WriteString("logger_name", LoggerName);
            writer.WriteString("logger_version", LoggerVersion);
            writer.WriteString("record_id", Guid.NewGuid().ToString());
            writer.WriteString("recorded_at", ToIso8601(now));
            writer.WriteString("event", eventName);
            writer.WriteString("event_category", GetEventCategory(eventName));
            WriteNullableString(writer, "session_id", sessionId);
            WriteNullableString(writer, "turn_id", turnId);
            WriteNullableString(writer, "agent_id", agentId);
            WriteNullableString(writer, "agent_type", agentType);
            WriteNullableString(writer, "model", model);
            WriteNullableString(writer, "permission_mode", permissionMode);
            WriteNullableString(writer, "cwd", cwd);
            WriteNullableString(writer, "repo_root", repo.RepoRoot);
            WriteNullableString(writer, "repo_name", repo.RepoName);
            WriteNullableString(writer, "git_branch", repo.GitBranch);
            WriteNullableString(writer, "git_commit", repo.GitCommit);
            WriteNullableString(writer, "transcript_path", transcriptPath);
            WriteNullableInt64(writer, "transcript_file_size", transcript.FileSize);
            WriteNullableString(writer, "transcript_file_mtime", transcript.FileMtime);
            WriteNullableString(writer, "transcript_file_hash", transcript.FileHash);
            WriteNullableString(writer, "transcript_status", transcript.Status);

            writer.WritePropertyName("raw_payload_keys");
            WriteStringArray(writer, payloadKeys);
            writer.WritePropertyName("unknown_payload_keys");
            WriteStringArray(writer, unknownPayloadKeys);
            writer.WriteNumber("raw_payload_size", rawPayloadSize);
            writer.WriteString("raw_payload_hash", rawPayloadHash);

            writer.WritePropertyName("capture_policy");
            settings.WriteCapturePolicy(writer);

            writer.WritePropertyName("host");
            WriteHost(writer);

            WriteNullableString(writer, "trace_id", traceId);
            WriteNullableString(writer, "turn_span_id", turnSpanId);
            WriteNullableString(writer, "subagent_span_id", subagentSpanId);
            WriteNullableString(writer, "tool_span_id", toolSpanId);
            WriteNullableString(writer, "parent_span_id", parentSpanId);
            WriteNullableString(writer, "span_id", spanId);
            writer.WriteString("severity_text", "Info");
            writer.WriteNumber("severity_number", 9);

            WriteNullableString(writer, "tool_use_id", tool.ToolUseId);
            WriteNullableString(writer, "tool_name", tool.ToolName);
            WriteNullableString(writer, "tool_category", tool.ToolCategory);
            WriteNullableInt64(writer, "tool_input_size", tool.ToolInputSize);
            WriteNullableString(writer, "tool_input_hash", tool.ToolInputHash);
            WriteNullableString(writer, "tool_input_preview", tool.ToolInputPreview);
            WriteNullableString(writer, "command", tool.CommandRedacted);
            WriteNullableString(writer, "command_redacted", tool.CommandRedacted);
            WriteNullableString(writer, "command_hash", tool.CommandHash);
            WriteNullableString(writer, "command_normalized", tool.CommandNormalized);
            WriteNullableString(writer, "command_kind", tool.CommandKind);
            WriteNullableString(writer, "command_risk", tool.CommandRisk);
            WriteNullableInt64(writer, "tool_response_size", tool.ToolResponseSize);
            WriteNullableString(writer, "tool_response_hash", tool.ToolResponseHash);
            WriteNullableString(writer, "tool_response_preview", tool.ToolResponsePreview);
            WriteNullableString(writer, "tool_result_class", tool.ToolResultClass);
            WriteNullableInt64(writer, "tool_elapsed_ms", toolCorrelation.ToolElapsedMs);
            WriteNullableString(writer, "tool_correlation_status", toolCorrelation.Status);

            WriteNullableString(writer, "subagent_run_id", subagentCorrelation.SubagentRunId);
            WriteNullableString(writer, "subagent_started_at", subagentCorrelation.StartedAt);
            WriteNullableString(writer, "subagent_stopped_at", subagentCorrelation.StoppedAt);
            WriteNullableInt64(writer, "subagent_duration_ms", subagentCorrelation.DurationMs);
            WriteNullableString(writer, "subagent_correlation_status", subagentCorrelation.Status);
            WriteNullableInt64(writer, "subagent_tool_use_count", subagentCorrelation.ToolUseCount);
            WriteNullableInt64(writer, "subagent_apply_patch_count", subagentCorrelation.ApplyPatchCount);
            WriteNullableInt64(writer, "subagent_bash_count", subagentCorrelation.BashCount);
            WriteNullableInt64(writer, "duration_ms", subagentCorrelation.DurationMs);

            WriteEventSpecificFields(writer, payload, eventName, settings);

            if (settings.CaptureRawPayload)
            {
                writer.WriteString("raw_payload_redacted", redactedRawPayload);
            }

            if (settings.CaptureFullToolInput && tool.FullToolInputRedacted is not null)
            {
                writer.WriteString("tool_input_redacted", tool.FullToolInputRedacted);
            }

            if (settings.CaptureFullToolResponse && tool.FullToolResponseRedacted is not null)
            {
                writer.WriteString("tool_response_redacted", tool.FullToolResponseRedacted);
            }

            writer.WriteNull("error");
            writer.WriteEndObject();
        });

        AppendLine(settings.LogBasePath, line);
    }

    private static void WriteEventSpecificFields(
        Utf8JsonWriter writer,
        JsonElement payload,
        string eventName,
        ObservationSettings settings)
    {
        if (eventName.Equals("SessionStart", StringComparison.OrdinalIgnoreCase))
        {
            WriteNullableString(writer, "source", GetString(payload, "source"));
        }

        if (eventName.Equals("Stop", StringComparison.OrdinalIgnoreCase))
        {
            WriteNullableString(writer, "stop_reason", GetString(payload, "stop_reason"));
        }

        if (eventName.Equals("PermissionRequest", StringComparison.OrdinalIgnoreCase))
        {
            WriteNullableString(writer, "requested_tool", GetString(payload, "requested_tool") ?? GetString(payload, "tool_name"));
            WriteNullableString(writer, "permission_decision", GetString(payload, "decision"));
            WriteNullableString(writer, "permission_reason", GetString(payload, "reason"));
            WriteNullableString(writer, "permission_escalation_kind", GetString(payload, "escalation_kind"));
        }

        if (eventName.Equals("PreCompact", StringComparison.OrdinalIgnoreCase) ||
            eventName.Equals("PostCompact", StringComparison.OrdinalIgnoreCase))
        {
            WriteNullableString(writer, "compact_trigger", GetString(payload, "trigger"));
        }

        if (eventName.Equals("UserPromptSubmit", StringComparison.OrdinalIgnoreCase))
        {
            var prompt = GetString(payload, "prompt") ?? GetString(payload, "message");
            if (prompt is not null)
            {
                var redactedPrompt = RedactSecrets(prompt);
                writer.WriteNumber("prompt_size", Encoding.UTF8.GetByteCount(prompt));
                writer.WriteString("prompt_hash", HashString(redactedPrompt));
                if (settings.CapturePromptPreview)
                {
                    writer.WriteString("prompt_preview", CreatePreview(redactedPrompt));
                }
            }
            else
            {
                writer.WriteNull("prompt_size");
                writer.WriteNull("prompt_hash");
                writer.WriteNull("prompt_preview");
            }
        }
    }

    private static ToolObservation BuildToolObservation(JsonElement payload, ObservationSettings settings)
    {
        var toolName = GetString(payload, "tool_name");
        var toolUseId = GetString(payload, "tool_use_id");
        var toolInput = GetNestedElement(payload, "tool_input");
        var toolResponse = GetNestedElement(payload, "tool_response");

        var toolInputRaw = toolInput?.GetRawText();
        var toolInputRedacted = toolInputRaw is null ? null : RedactSecrets(toolInputRaw);
        var toolResponseRaw = toolResponse?.GetRawText();
        var toolResponseRedacted = toolResponseRaw is null ? null : RedactSecrets(toolResponseRaw);

        var command = GetNestedString(payload, "tool_input", "command");
        if (command is null && toolName is not null && toolName.Equals("apply_patch", StringComparison.OrdinalIgnoreCase))
        {
            command = GetNestedString(payload, "tool_input", "patch");
        }

        var commandRedacted = command is null ? null : RedactSecrets(command);
        return new ToolObservation
        {
            ToolUseId = toolUseId,
            ToolName = toolName,
            ToolCategory = GetToolCategory(toolName),
            ToolInputSize = toolInputRaw is null ? null : Encoding.UTF8.GetByteCount(toolInputRaw),
            ToolInputHash = toolInputRedacted is null ? null : HashString(toolInputRedacted),
            ToolInputPreview = toolInputRedacted is null ? null : CreatePreview(toolInputRedacted),
            CommandRedacted = commandRedacted,
            CommandHash = commandRedacted is null ? null : HashString(commandRedacted),
            CommandNormalized = NormalizeCommand(commandRedacted),
            CommandKind = GetCommandKind(toolName, commandRedacted),
            CommandRisk = GetCommandRisk(toolName, commandRedacted),
            ToolResponseSize = toolResponseRaw is null ? null : Encoding.UTF8.GetByteCount(toolResponseRaw),
            ToolResponseHash = toolResponseRedacted is null ? null : HashString(toolResponseRedacted),
            ToolResponsePreview = toolResponseRedacted is null ? null : CreatePreview(toolResponseRedacted),
            ToolResultClass = GetToolResultClass(toolResponse),
            FullToolInputRedacted = settings.CaptureFullToolInput ? toolInputRedacted : null,
            FullToolResponseRedacted = settings.CaptureFullToolResponse ? toolResponseRedacted : null
        };
    }

    private static async Task<ToolCorrelationResult> CorrelateToolAsync(
        string eventName,
        DateTimeOffset recordedAt,
        string? sessionId,
        string? turnId,
        string? agentId,
        string? toolUseId,
        string? toolName,
        ObservationSettings settings)
    {
        if (!eventName.Equals("PreToolUse", StringComparison.OrdinalIgnoreCase) &&
            !eventName.Equals("PostToolUse", StringComparison.OrdinalIgnoreCase))
        {
            return ToolCorrelationResult.Empty;
        }

        if (string.IsNullOrWhiteSpace(toolUseId))
        {
            return new ToolCorrelationResult("unknown", null);
        }

        var key = BuildToolStateKey(sessionId, turnId, agentId, toolUseId);
        var statePath = Path.Combine(settings.ToolStateDirectory, SafeFilename(key));

        if (eventName.Equals("PreToolUse", StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(statePath))
            {
                return new ToolCorrelationResult("duplicate", null);
            }

            var state = SerializeJson(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("recorded_at", ToIso8601(recordedAt));
                WriteNullableString(writer, "tool_use_id", toolUseId);
                WriteNullableString(writer, "tool_name", toolName);
                writer.WriteEndObject();
            });
            await File.WriteAllTextAsync(statePath, state, Encoding.UTF8);
            return new ToolCorrelationResult("unknown", null);
        }

        if (!File.Exists(statePath))
        {
            return new ToolCorrelationResult("missing_pre", null);
        }

        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(statePath, Encoding.UTF8));
            var startedAt = GetDateTimeOffset(document.RootElement, "recorded_at");
            TryDeleteFile(statePath);
            if (!startedAt.HasValue)
            {
                return new ToolCorrelationResult("missing_pre", null);
            }

            var elapsedMs = Math.Max(0L, (long)Math.Round((recordedAt - startedAt.Value).TotalMilliseconds));
            return new ToolCorrelationResult("matched", elapsedMs);
        }
        catch (Exception ex)
        {
            TryAppendErrorLog("tool-state-read-failed", ex, null, null);
            return new ToolCorrelationResult("missing_pre", null);
        }
    }

    private static async Task<SubagentCorrelationResult> CorrelateSubagentAsync(
        string eventName,
        DateTimeOffset recordedAt,
        string? sessionId,
        string? turnId,
        string? agentId,
        string? agentType,
        string? model,
        ObservationSettings settings)
    {
        if (!eventName.Equals("SubagentStart", StringComparison.OrdinalIgnoreCase) &&
            !eventName.Equals("SubagentStop", StringComparison.OrdinalIgnoreCase))
        {
            return SubagentCorrelationResult.Empty;
        }

        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(agentId))
        {
            return new SubagentCorrelationResult(null, null, null, null, "unknown", null, null, null);
        }

        var key = BuildSubagentStateKey(sessionId, agentId);
        var statePath = Path.Combine(settings.SubagentStateDirectory, SafeFilename(key));

        if (eventName.Equals("SubagentStart", StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(statePath))
            {
                return new SubagentCorrelationResult(null, ToIso8601(recordedAt), null, null, "duplicate", null, null, null);
            }

            var runId = CreateRunId(sessionId, agentId, recordedAt);
            var state = SerializeJson(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("subagent_run_id", runId);
                writer.WriteString("started_at", ToIso8601(recordedAt));
                WriteNullableString(writer, "session_id", sessionId);
                WriteNullableString(writer, "turn_id", turnId);
                WriteNullableString(writer, "agent_id", agentId);
                WriteNullableString(writer, "agent_type", agentType);
                WriteNullableString(writer, "model", model);
                writer.WriteEndObject();
            });
            await File.WriteAllTextAsync(statePath, state, Encoding.UTF8);
            return new SubagentCorrelationResult(runId, ToIso8601(recordedAt), null, null, "unknown", null, null, null);
        }

        if (!File.Exists(statePath))
        {
            return new SubagentCorrelationResult(null, null, ToIso8601(recordedAt), null, "missing_start", null, null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(statePath, Encoding.UTF8));
            var start = GetDateTimeOffset(document.RootElement, "started_at");
            var runId = GetString(document.RootElement, "subagent_run_id");
            TryDeleteFile(statePath);

            if (!start.HasValue)
            {
                return new SubagentCorrelationResult(runId, null, ToIso8601(recordedAt), null, "missing_start", null, null, null);
            }

            var duration = Math.Max(0L, (long)Math.Round((recordedAt - start.Value).TotalMilliseconds));
            return new SubagentCorrelationResult(
                runId,
                ToIso8601(start.Value),
                ToIso8601(recordedAt),
                duration,
                "matched",
                null,
                null,
                null);
        }
        catch (Exception ex)
        {
            TryAppendErrorLog("subagent-state-read-failed", ex, null, null);
            return new SubagentCorrelationResult(null, null, ToIso8601(recordedAt), null, "missing_start", null, null, null);
        }
    }

    private static TranscriptMetadata TryGetTranscriptMetadata(string? transcriptPath, ObservationSettings settings)
    {
        if (string.IsNullOrWhiteSpace(transcriptPath))
        {
            return TranscriptMetadata.None("missing");
        }

        try
        {
            if (!File.Exists(transcriptPath))
            {
                return TranscriptMetadata.None("not_found");
            }

            var fileInfo = new FileInfo(transcriptPath);
            var hash = settings.CaptureTranscriptSnapshot
                ? ComputeFileHash(transcriptPath)
                : null;

            return new TranscriptMetadata(
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc.ToString("O"),
                hash,
                "metadata_only");
        }
        catch (Exception ex)
        {
            TryAppendErrorLog("transcript-metadata-failed", ex, null, null);
            return TranscriptMetadata.None("error");
        }
    }

    private static RepositoryMetadata TryGetRepositoryMetadata(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))
        {
            return RepositoryMetadata.Empty;
        }

        try
        {
            var repoRoot = TryRunGit(cwd, "rev-parse --show-toplevel");
            if (string.IsNullOrWhiteSpace(repoRoot))
            {
                return new RepositoryMetadata(null, Path.GetFileName(cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), null, null);
            }

            var repoName = TryRunGit(repoRoot, "config --get remote.origin.url");
            repoName = NormalizeRepositoryName(repoName) ?? Path.GetFileName(repoRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            return new RepositoryMetadata(
                repoRoot,
                repoName,
                TryRunGit(repoRoot, "rev-parse --abbrev-ref HEAD"),
                TryRunGit(repoRoot, "rev-parse HEAD"));
        }
        catch (Exception ex)
        {
            TryAppendErrorLog("git-metadata-failed", ex, null, null);
            return RepositoryMetadata.Empty;
        }
    }

    private static string? TryRunGit(string workingDirectory, string arguments)
    {
        var startInfo = new ProcessStartInfo("git", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        var stdout = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(1500);
        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // nop
            }

            return null;
        }

        if (process.ExitCode != 0)
        {
            return null;
        }

        var trimmed = stdout.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static void CleanupStaleStateFiles(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            var threshold = DateTime.UtcNow.AddDays(-7);
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                var lastWrite = File.GetLastWriteTimeUtc(path);
                if (lastWrite < threshold)
                {
                    TryDeleteFile(path);
                }
            }
        }
        catch (Exception ex)
        {
            TryAppendErrorLog("state-cleanup-failed", ex, null, null);
        }
    }

    private static void WriteHost(Utf8JsonWriter writer)
    {
        var hostSeed = $"{Environment.MachineName}|{Environment.UserName}|{Environment.UserDomainName}";
        writer.WriteStartObject();
        writer.WriteString("host_id", HashString(hostSeed)[..16]);
        writer.WriteString("os_description", RuntimeInformation.OSDescription);
        writer.WriteString("os_architecture", RuntimeInformation.OSArchitecture.ToString());
        writer.WriteString("process_architecture", RuntimeInformation.ProcessArchitecture.ToString());
        writer.WriteString("framework_description", RuntimeInformation.FrameworkDescription);
        writer.WriteNumber("process_id", Environment.ProcessId);
        writer.WriteString("process_name", Process.GetCurrentProcess().ProcessName);
        writer.WriteEndObject();
    }

    private static void TryAppendErrorLog(string errorKind, Exception exception, string? rawPayload, JsonElement? payload)
    {
        try
        {
            var settings = ObservationSettings.Load();
            settings.EnsureDirectories();
            var line = SerializeJson(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("recorded_at", ToIso8601(DateTimeOffset.UtcNow));
                writer.WriteString("error_kind", errorKind);
                writer.WriteString("exception_type", exception.GetType().FullName);
                writer.WriteString("message", exception.Message);
                writer.WriteString("exception", exception.ToString());
                if (payload.HasValue)
                {
                    WriteNullableString(writer, "event", GetString(payload.Value, "hook_event_name"));
                    WriteNullableString(writer, "session_id", GetString(payload.Value, "session_id"));
                    WriteNullableString(writer, "turn_id", GetString(payload.Value, "turn_id"));
                    WriteNullableString(writer, "agent_id", NormalizeAgentId(GetString(payload.Value, "agent_id")));
                }
                else
                {
                    writer.WriteNull("event");
                    writer.WriteNull("session_id");
                    writer.WriteNull("turn_id");
                    writer.WriteNull("agent_id");
                }

                if (!string.IsNullOrWhiteSpace(rawPayload))
                {
                    var redacted = RedactSecrets(rawPayload);
                    writer.WriteNumber("raw_payload_size", Encoding.UTF8.GetByteCount(rawPayload));
                    writer.WriteString("raw_payload_hash", HashString(redacted));
                    writer.WriteString("raw_payload_preview", CreatePreview(redacted));
                }
                else
                {
                    writer.WriteNull("raw_payload_size");
                    writer.WriteNull("raw_payload_hash");
                    writer.WriteNull("raw_payload_preview");
                }

                writer.WriteEndObject();
            });

            AppendLine(settings.ErrorLogBasePath, line);
        }
        catch (Exception loggingEx)
        {
            Trace.TraceError(exception.ToString());
            Trace.TraceError(loggingEx.ToString());
        }
    }

    private static string CreateRunId(string sessionId, string agentId, DateTimeOffset recordedAt)
    {
        return $"{sessionId}:{agentId}:{recordedAt.ToUnixTimeMilliseconds()}";
    }

    private static string BuildToolStateKey(string? sessionId, string? turnId, string? agentId, string toolUseId)
    {
        return string.Join("|", sessionId ?? "-", turnId ?? "-", agentId ?? "parent", toolUseId);
    }

    private static string BuildSubagentStateKey(string sessionId, string agentId)
    {
        return $"{sessionId}|{agentId}";
    }

    private static string? NormalizeAgentId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Equals("00000000-0000-0000-0000-000000000000", StringComparison.OrdinalIgnoreCase)
            ? null
            : value;
    }

    private static string GetEventCategory(string eventName)
    {
        return eventName switch
        {
            "SessionStart" or "Stop" => "session",
            "UserPromptSubmit" => "turn",
            "SubagentStart" or "SubagentStop" => "subagent",
            "PreToolUse" or "PostToolUse" => "tool",
            "PermissionRequest" => "permission",
            "PreCompact" or "PostCompact" => "compact",
            _ => "unknown"
        };
    }

    private static string? GetToolCategory(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return null;
        }

        if (toolName.Equals("apply_patch", StringComparison.OrdinalIgnoreCase))
        {
            return "patch";
        }

        if (toolName.Contains("shell", StringComparison.OrdinalIgnoreCase) ||
            toolName.Contains("bash", StringComparison.OrdinalIgnoreCase) ||
            toolName.Contains("command", StringComparison.OrdinalIgnoreCase))
        {
            return "shell";
        }

        if (toolName.Contains("mcp", StringComparison.OrdinalIgnoreCase) ||
            toolName.Contains(".", StringComparison.OrdinalIgnoreCase))
        {
            return "mcp";
        }

        return "unknown";
    }

    private static string? GetToolResultClass(JsonElement? toolResponse)
    {
        if (!toolResponse.HasValue)
        {
            return null;
        }

        var element = toolResponse.Value;
        if (TryGetNestedInt64(element, "exitCode", out var exitCode))
        {
            return exitCode == 0 ? "success" : "failure";
        }

        if (TryGetNestedBoolean(element, "success", out var success))
        {
            return success ? "success" : "failure";
        }

        var responseText = element.GetRawText();
        if (responseText.Contains("blocked", StringComparison.OrdinalIgnoreCase) ||
            responseText.Contains("approval", StringComparison.OrdinalIgnoreCase))
        {
            return "blocked";
        }

        return "unknown";
    }

    private static string? GetCommandKind(string? toolName, string? command)
    {
        if (toolName is not null && toolName.Equals("apply_patch", StringComparison.OrdinalIgnoreCase))
        {
            return "edit";
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var normalized = command.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        if (normalized.Length == 0)
        {
            return null;
        }

        if (ContainsAny(normalized, "rg", "grep", "findstr", "Select-String"))
        {
            return "search";
        }

        if (ContainsAny(normalized, "Get-Content", "cat", "type "))
        {
            return "read";
        }

        if (ContainsAny(normalized, "Get-ChildItem", "dir", "ls"))
        {
            return "list";
        }

        if (ContainsAny(normalized, "dotnet test", "npm test", "pytest", "cargo test"))
        {
            return "test";
        }

        if (ContainsAny(normalized, "dotnet build", "dotnet publish", "npm run build", "cargo build"))
        {
            return "build";
        }

        if (ContainsAny(normalized, "dotnet run", "python", "node ", "pwsh ", "powershell "))
        {
            return "run";
        }

        if (ContainsAny(normalized, "git "))
        {
            return "git";
        }

        if (ContainsAny(normalized, "curl", "Invoke-WebRequest", "wget", "dotnet restore", "npm install", "nuget"))
        {
            return "network";
        }

        if (ContainsAny(normalized, "Set-Content", "Add-Content", "Out-File", "Move-Item", "Copy-Item", "Remove-Item", "New-Item"))
        {
            return "edit";
        }

        return "unknown";
    }

    private static string? GetCommandRisk(string? toolName, string? command)
    {
        if (toolName is not null && toolName.Equals("apply_patch", StringComparison.OrdinalIgnoreCase))
        {
            return "medium";
        }

        var kind = GetCommandKind(toolName, command);
        if (string.IsNullOrWhiteSpace(command))
        {
            return kind is null ? null : kind switch
            {
                "read" or "search" or "list" => "low",
                "test" or "build" or "run" or "git" or "edit" or "network" => "medium",
                _ => "unknown"
            };
        }

        if (ContainsAny(command, "Remove-Item", "del ", "rm ", "git reset", "--hard", "credential", "secret", "token"))
        {
            return "high";
        }

        return kind switch
        {
            "read" or "search" or "list" => "low",
            "test" or "build" or "run" or "git" or "network" or "edit" => "medium",
            _ => "unknown"
        };
    }

    private static string? NormalizeCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var compact = Regex.Replace(command, @"\s+", " ").Trim();
        return compact.Length <= 120 ? compact : compact[..120] + "...";
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetNestedInt64(JsonElement element, string propertyName, out long value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number)
        {
            value = property.GetInt64();
            return true;
        }

        if (property.ValueKind == JsonValueKind.String &&
            long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static bool TryGetNestedBoolean(JsonElement element, string propertyName, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }

        if (property.ValueKind == JsonValueKind.False)
        {
            value = false;
            return true;
        }

        if (property.ValueKind == JsonValueKind.String &&
            bool.TryParse(property.GetString(), out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static string CreateTraceId(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        }

        return HashString($"trace:{sessionId}")[..32];
    }

    private static string? CreateSpanId(string prefix, string? value, string? fallback)
    {
        var seed = value ?? fallback;
        if (string.IsNullOrWhiteSpace(seed))
        {
            return null;
        }

        return HashString($"{prefix}:{seed}")[..16];
    }

    private static string SafeFilename(string value)
    {
        return $"{HashString(value)[..32]}.json";
    }

    private static string CreatePreview(string value)
    {
        var compact = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        compact = Regex.Replace(compact, @"\s+", " ").Trim();
        return compact.Length <= PreviewLimit ? compact : compact[..PreviewLimit] + "...";
    }

    private static string RedactSecrets(string input)
    {
        var output = input;
        output = Regex.Replace(output, "(?i)(\"(?:api[_-]?key|token|secret|password|authorization)\"\\s*:\\s*\")([^\"]+)(\")", "$1[REDACTED]$3");
        output = Regex.Replace(output, "(?i)(Bearer\\s+)[A-Za-z0-9_\\-\\.]{8,}", "$1[REDACTED]");
        output = Regex.Replace(output, "(?i)(--?(?:api[_-]?key|token|secret|password)\\s+[\"']?)([^\\s\"']+)", "$1[REDACTED]");
        output = Regex.Replace(output, "(?i)((?:api[_-]?key|token|secret|password)\\s*[=:]\\s*[\"']?)([^\\s\"',}]+)", "$1[REDACTED]");
        output = Regex.Replace(output, "(?i)(gh[pousr]_[A-Za-z0-9_]{10,})", "[REDACTED_GITHUB_TOKEN]");
        output = Regex.Replace(output, "(?i)(sk-[A-Za-z0-9_\\-]{10,})", "[REDACTED_OPENAI_KEY]");
        output = Regex.Replace(output, "(?i)(xox[baprs]-[A-Za-z0-9\\-]{10,})", "[REDACTED_SLACK_TOKEN]");
        output = Regex.Replace(output, "(?i)(Authorization\\s*:\\s*)([^\\s,]+)", "$1[REDACTED]");
        return output;
    }

    private static string HashString(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string? ComputeFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? NormalizeRepositoryName(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return null;
        }

        var cleaned = remoteUrl.Trim();
        if (cleaned.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[..^4];
        }

        var slashIndex = cleaned.LastIndexOf('/');
        var colonIndex = cleaned.LastIndexOf(':');
        var splitIndex = Math.Max(slashIndex, colonIndex);
        if (splitIndex <= 0 || splitIndex >= cleaned.Length - 1)
        {
            return cleaned;
        }

        var ownerSeparator = cleaned.LastIndexOf('/', splitIndex - 1);
        if (ownerSeparator < 0)
        {
            ownerSeparator = cleaned.LastIndexOf(':', splitIndex - 1);
        }

        return ownerSeparator >= 0
            ? cleaned[(ownerSeparator + 1)..]
            : cleaned[(splitIndex + 1)..];
    }

    private static string ToIso8601(DateTimeOffset timestamp)
    {
        return timestamp.ToString("O", CultureInfo.InvariantCulture);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Null => null,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => property.ToString()
        };
    }

    private static DateTimeOffset? GetDateTimeOffset(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(property.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? GetNestedString(JsonElement element, string parentPropertyName, string childPropertyName)
    {
        if (!element.TryGetProperty(parentPropertyName, out var parent) || parent.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return GetString(parent, childPropertyName);
    }

    private static JsonElement? GetNestedElement(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.Clone();
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

    private static string GetDailyLogPath(string path)
    {
        var directory = Path.GetDirectoryName(path);
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return path;
        }

        var date = DateTimeOffset.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var extension = Path.GetExtension(fileName);
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var dailyFileName = string.IsNullOrWhiteSpace(extension)
            ? $"{fileNameWithoutExtension}-{date}"
            : $"{fileNameWithoutExtension}-{date}{extension}";

        return string.IsNullOrWhiteSpace(directory) ? dailyFileName : Path.Combine(directory, dailyFileName);
    }

    private static string GetLogMutexName(string path)
    {
        var normalizedPath = Path.GetFullPath(path).ToLowerInvariant();
        return $@"Global\CodexAgentObservationLogger-{HashString(normalizedPath)[..32]}";
    }

    private static void AppendLine(string baseLogPath, string line)
    {
        var logPath = GetDailyLogPath(baseLogPath);
        var mutexName = GetLogMutexName(logPath);
        bool hasLock = false;
        var endTime = DateTimeOffset.UtcNow.AddMilliseconds(LogMutexAcquireTimeoutMilliseconds);

        using var mutex = new Mutex(false, mutexName);
        try
        {
            while (!hasLock)
            {
                var remaining = endTime - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException($"Timeout waiting for log file mutex: {logPath}");
                }

                var waitMilliseconds = (int)Math.Min(LogMutexRetryDelayMilliseconds, remaining.TotalMilliseconds);
                try
                {
                    hasLock = mutex.WaitOne(waitMilliseconds);
                }
                catch (AbandonedMutexException)
                {
                    hasLock = true;
                }
            }

            var directory = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
        finally
        {
            if (hasLock)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (Exception ex)
                {
                    Trace.TraceError(ex.ToString());
                }
                finally
                {
                    mutex.Close();
                }
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError(ex.ToString());
        }
    }

    private static void WriteStringArray(Utf8JsonWriter writer, IEnumerable<string> values)
    {
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
            return;
        }

        writer.WriteString(propertyName, value);
    }

    private static void WriteNullableInt64(Utf8JsonWriter writer, string propertyName, long? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(propertyName, value.Value);
            return;
        }

        writer.WriteNull(propertyName);
    }
}

internal sealed record ObservationSettings(
    string LogBasePath,
    string ErrorLogBasePath,
    string StateDirectory,
    bool CaptureRawPayload,
    bool CaptureFullToolInput,
    bool CaptureFullToolResponse,
    bool CapturePromptPreview,
    bool CaptureTranscriptSnapshot)
{
    public string ToolStateDirectory => Path.Combine(StateDirectory, "tool");
    public string SubagentStateDirectory => Path.Combine(StateDirectory, "subagent");

    public static ObservationSettings Load()
    {
        var codexHome = GetCodexHome();
        var logPath = GetFirstEnvironmentValue("CODEX_AGENT_OBSERVATION_LOG", "CODEX_AGENT_USAGE_LOG")
            ?? Path.Combine(codexHome, "logs", "agent-observations.jsonl");
        var statePath = GetFirstEnvironmentValue("CODEX_AGENT_OBSERVATION_STATE", "CODEX_AGENT_USAGE_STATE")
            ?? Path.Combine(codexHome, "hook-state", "agent-observations");
        var errorLogPath = GetFirstEnvironmentValue("CODEX_AGENT_OBSERVATION_ERROR_LOG", "CODEX_AGENT_USAGE_ERROR_LOG")
            ?? Path.Combine(codexHome, "logs", "agent-observations-error.log");

        return new ObservationSettings(
            logPath,
            errorLogPath,
            statePath,
            GetFlag("CODEX_AGENT_OBSERVATION_CAPTURE_RAW_PAYLOAD"),
            GetFlag("CODEX_AGENT_OBSERVATION_CAPTURE_FULL_TOOL_INPUT"),
            GetFlag("CODEX_AGENT_OBSERVATION_CAPTURE_FULL_TOOL_RESPONSE"),
            GetFlag("CODEX_AGENT_OBSERVATION_CAPTURE_PROMPT_PREVIEW"),
            GetFlag("CODEX_AGENT_OBSERVATION_TRANSCRIPT_SNAPSHOT"));
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogBasePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(ErrorLogBasePath)!);
        Directory.CreateDirectory(ToolStateDirectory);
        Directory.CreateDirectory(SubagentStateDirectory);
    }

    public void WriteCapturePolicy(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("redaction_enabled", true);
        writer.WriteString("hash_basis", "redacted");
        writer.WriteBoolean("capture_raw_payload", CaptureRawPayload);
        writer.WriteBoolean("capture_full_tool_input", CaptureFullToolInput);
        writer.WriteBoolean("capture_full_tool_response", CaptureFullToolResponse);
        writer.WriteBoolean("capture_prompt_preview", CapturePromptPreview);
        writer.WriteBoolean("capture_transcript_snapshot", CaptureTranscriptSnapshot);
        writer.WriteString("command_storage", "redacted");
        writer.WriteNumber("preview_max_chars", 240);
        writer.WriteString("classification_version", "1.0");
        writer.WriteEndObject();
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

    private static string? GetFirstEnvironmentValue(params string[] names)
    {
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool GetFlag(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return value is not null &&
               (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed record ToolObservation
{
    public string? ToolUseId { get; init; }
    public string? ToolName { get; init; }
    public string? ToolCategory { get; init; }
    public long? ToolInputSize { get; init; }
    public string? ToolInputHash { get; init; }
    public string? ToolInputPreview { get; init; }
    public string? CommandRedacted { get; init; }
    public string? CommandHash { get; init; }
    public string? CommandNormalized { get; init; }
    public string? CommandKind { get; init; }
    public string? CommandRisk { get; init; }
    public long? ToolResponseSize { get; init; }
    public string? ToolResponseHash { get; init; }
    public string? ToolResponsePreview { get; init; }
    public string? ToolResultClass { get; init; }
    public string? FullToolInputRedacted { get; init; }
    public string? FullToolResponseRedacted { get; init; }
}

internal sealed record ToolCorrelationResult(string? Status, long? ToolElapsedMs)
{
    public static ToolCorrelationResult Empty { get; } = new(null, null);
}

internal sealed record SubagentCorrelationResult(
    string? SubagentRunId,
    string? StartedAt,
    string? StoppedAt,
    long? DurationMs,
    string? Status,
    long? ToolUseCount,
    long? ApplyPatchCount,
    long? BashCount)
{
    public static SubagentCorrelationResult Empty { get; } = new(null, null, null, null, null, null, null, null);
}

internal sealed record TranscriptMetadata(long? FileSize, string? FileMtime, string? FileHash, string Status)
{
    public static TranscriptMetadata None(string status) => new(null, null, null, status);
}

internal sealed record RepositoryMetadata(string? RepoRoot, string? RepoName, string? GitBranch, string? GitCommit)
{
    public static RepositoryMetadata Empty { get; } = new(null, null, null, null);
}
