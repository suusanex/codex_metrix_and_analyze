using System.Security.Cryptography;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Text;
using System.Text.Json;

return await ProgramEntry.RunAsync();

internal static class ProgramEntry
{
    private const int LogMutexRetryDelayMilliseconds = 50;
    private const int LogMutexAcquireTimeoutMilliseconds = 5_000;

    public static async Task<int> RunAsync()
    {
        string? rawInput = null;
        JsonDocument? document = null;

        try
        {
            rawInput = await Console.In.ReadToEndAsync();
            document = JsonDocument.Parse(rawInput);
            await HandlePayloadAsync(document.RootElement);
        }
        catch (JsonException ex)
        {
            TryAppendErrorLog("payload-parse-failed", ex, null);
        }
        catch (Exception ex)
        {
            TryAppendErrorLog("hook-processing-failed", ex, document?.RootElement);
        }
        finally
        {
            document?.Dispose();
        }

        return 0;
    }

    private static async Task HandlePayloadAsync(JsonElement payload)
    {
        var codexHome = GetCodexHome();
        var logPath = Environment.GetEnvironmentVariable("CODEX_AGENT_USAGE_LOG")
            ?? Path.Combine(codexHome, "logs", "agent-usage.jsonl");
        var stateDir = Environment.GetEnvironmentVariable("CODEX_AGENT_USAGE_STATE")
            ?? Path.Combine(codexHome, "hook-state");

        Directory.CreateDirectory(stateDir);

        var eventName = GetString(payload, "hook_event_name") ?? "Unknown";
        var now = DateTimeOffset.UtcNow;
        var timestamp = now.ToUnixTimeMilliseconds() / 1000.0;

        var sessionId = GetString(payload, "session_id");
        var turnId = GetString(payload, "turn_id");
        var agentId = GetString(payload, "agent_id");
        var agentType = GetString(payload, "agent_type");
        var model = GetString(payload, "model");
        var cwd = GetString(payload, "cwd");
        var permissionMode = GetString(payload, "permission_mode");
        var transcriptPath = GetString(payload, "transcript_path");

        int? durationMs = null;
        var stateKey = !string.IsNullOrWhiteSpace(sessionId) && !string.IsNullOrWhiteSpace(agentId)
            ? $"{sessionId}:{agentId}"
            : null;

        if (eventName == "SubagentStart" && stateKey is not null)
        {
            var statePath = Path.Combine(stateDir, SafeFilename(stateKey));
            await File.WriteAllTextAsync(
                statePath,
                SerializeJson(writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("start_time", timestamp);
                    writer.WriteString("started_at", ToIso8601(now));
                    WriteNullableString(writer, "session_id", sessionId);
                    WriteNullableString(writer, "turn_id", turnId);
                    WriteNullableString(writer, "agent_id", agentId);
                    WriteNullableString(writer, "agent_type", agentType);
                    WriteNullableString(writer, "model", model);
                    writer.WriteEndObject();
                }),
                Encoding.UTF8);
        }

        if (eventName == "SubagentStop" && stateKey is not null)
        {
            var statePath = Path.Combine(stateDir, SafeFilename(stateKey));
            if (File.Exists(statePath))
            {
                try
                {
                    using var stateDocument = JsonDocument.Parse(await File.ReadAllTextAsync(statePath, Encoding.UTF8));
                    if (stateDocument.RootElement.TryGetProperty("start_time", out var startTimeElement))
                    {
                        var startTime = startTimeElement.GetDouble();
                        durationMs = (int)Math.Round((timestamp - startTime) * 1000);
                    }
                }
                catch (Exception ex)
                {
                    TryAppendErrorLog("state-read-failed", ex, payload);
                    durationMs = null;
                }
            }
        }

        var toolName = GetString(payload, "tool_name");
        var command = GetNestedString(payload, "tool_input", "command");
        var toolResponse = GetNestedElement(payload, "tool_response");
        var toolResponseText = toolResponse?.GetRawText();
        var payloadKeys = payload.EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var line = SerializeJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("recorded_at", ToIso8601(now));
            writer.WriteString("event", eventName);
            WriteNullableString(writer, "session_id", sessionId);
            WriteNullableString(writer, "turn_id", turnId);
            WriteNullableString(writer, "agent_id", agentId);
            WriteNullableString(writer, "agent_type", agentType);
            WriteNullableString(writer, "model", model);
            WriteNullableString(writer, "permission_mode", permissionMode);
            WriteNullableString(writer, "cwd", cwd);
            WriteNullableString(writer, "transcript_path", transcriptPath);
            WriteNullableInt32(writer, "duration_ms", durationMs);
            WriteNullableString(writer, "tool_name", toolName);
            WriteNullableString(writer, "command", command);

            if (toolResponseText is not null)
            {
                writer.WriteNumber("tool_response_size", toolResponseText.Length);
                writer.WriteString("tool_response_preview", toolResponseText[..Math.Min(500, toolResponseText.Length)]);
            }

            writer.WritePropertyName("raw_payload_keys");
            writer.WriteStartArray();
            foreach (var key in payloadKeys)
            {
                writer.WriteStringValue(key);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });

        AppendLine(logPath, line);
    }

    private static void TryAppendErrorLog(string errorKind, Exception exception, JsonElement? payload)
    {
        try
        {
            var codexHome = GetCodexHome();
            var errorLogPath = Environment.GetEnvironmentVariable("CODEX_AGENT_USAGE_ERROR_LOG")
                ?? Path.Combine(codexHome, "logs", "agent-usage-error.log");

            Directory.CreateDirectory(Path.GetDirectoryName(errorLogPath)!);

            var line = SerializeJson(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("recorded_at", ToIso8601(DateTimeOffset.UtcNow));
                writer.WriteString("error_kind", errorKind);
                writer.WriteString("message", exception.Message);
                writer.WriteString("exception_type", exception.GetType().FullName);
                writer.WriteString("stack_trace", exception.ToString());

                if (payload.HasValue)
                {
                    WriteNullableString(writer, "event", GetString(payload.Value, "hook_event_name"));
                    WriteNullableString(writer, "session_id", GetString(payload.Value, "session_id"));
                    WriteNullableString(writer, "turn_id", GetString(payload.Value, "turn_id"));
                    WriteNullableString(writer, "agent_id", GetString(payload.Value, "agent_id"));
                }
                else
                {
                    writer.WriteNull("event");
                    writer.WriteNull("session_id");
                    writer.WriteNull("turn_id");
                    writer.WriteNull("agent_id");
                }

                writer.WriteEndObject();
            });

            AppendLine(errorLogPath, line);
        }
        catch
        {
            Trace.TraceError(exception.ToString());
            Trace.TraceError($"Failed to append hook error log: {errorKind}");
        }
    }

    private static string GetCodexHome()
    {
        var explicitHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(explicitHome))
        {
            return explicitHome;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".codex");
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
            _ => property.ToString()
        };
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

    private static string SafeFilename(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"{hex[..32]}.json";
    }

    private static string ToIso8601(DateTimeOffset timestamp)
    {
        return timestamp.ToString("O");
    }

    private static string GetDailyLogPath(string path)
    {
        var directory = Path.GetDirectoryName(path);
        var fileName = Path.GetFileName(path);

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return path;
        }

        var localDate = DateTimeOffset.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var extension = Path.GetExtension(fileName);
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var dailyFileName = string.IsNullOrEmpty(extension)
            ? $"{fileNameWithoutExtension}-{localDate}"
            : $"{fileNameWithoutExtension}-{localDate}{extension}";
        return string.IsNullOrWhiteSpace(directory) ? dailyFileName : Path.Combine(directory, dailyFileName);
    }

    private static string GetLogMutexName(string path)
    {
        var normalizedPath = Path.GetFullPath(path).ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath));
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return $@"Global\CodexAgentUsageLogger-{hex[..32]}";
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

            using var stream = new FileStream(
                logPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);

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
                    catch (Exception releaseEx)
                    {
                        Trace.TraceError(releaseEx.ToString());
                    }
                    finally
                    {
                        mutex.Close();
                    }
            }
        }
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

    private static void WriteNullableString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
            return;
        }

        writer.WriteString(propertyName, value);
    }

    private static void WriteNullableInt32(Utf8JsonWriter writer, string propertyName, int? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(propertyName, value.Value);
            return;
        }

        writer.WriteNull(propertyName);
    }
}
