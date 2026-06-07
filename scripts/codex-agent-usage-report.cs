#:property TargetFramework=net10.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var options = UsageOptions.Parse(args);

if (options.ShowHelp)
{
    UsageOptions.PrintUsage();
    return 0;
}

var report = UsageReportGenerator.Generate(options);
var output = options.Format == OutputFormat.Json
    ? report.ToJson()
    : report.ToMarkdown();

if (options.OutputPath is null)
{
    Console.Out.Write(output);
}
else
{
    await File.WriteAllTextAsync(options.OutputPath, output, Encoding.UTF8);
}

return 0;

internal enum OutputFormat
{
    Markdown,
    Json
}

internal sealed class UsageOptions
{
    public IReadOnlyList<string> LogPaths { get; init; } = [];
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public string Timezone { get; init; } = "local";
    public TimeSpan Bucket { get; init; } = TimeSpan.FromMinutes(15);
    public string? SessionIdFilter { get; init; }
    public string? CwdContains { get; init; }
    public string? ExpectedParentModel { get; init; }
    public string? ExpectedSubagentModel { get; init; }
    public int Top { get; init; } = 20;
    public OutputFormat Format { get; init; } = OutputFormat.Markdown;
    public string? OutputPath { get; init; }
    public bool ShowHelp { get; init; }

    public static UsageOptions Parse(string[] args)
    {
        var logPaths = new List<string>();
        var from = (DateTimeOffset?)null;
        var to = (DateTimeOffset?)null;
        var timezone = "local";
        var bucket = TimeSpan.FromMinutes(15);
        string? sessionId = null;
        string? cwdContains = null;
        string? expectedParent = null;
        string? expectedSubagent = null;
        var top = 20;
        var format = OutputFormat.Markdown;
        string? output = null;
        var showHelp = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--help":
                case "-h":
                case "/?":
                    showHelp = true;
                    break;

                case "--log":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("Missing value for --log");
                    }

                    logPaths.Add(args[++i]);
                    break;

                case "--from":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("Missing value for --from");
                    }

                    var fromValue = args[++i];
                    if (!DateTimeOffset.TryParse(fromValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var fromParsed))
                    {
                        throw new ArgumentException($"Invalid datetime for --from: {fromValue}");
                    }

                    from = fromParsed;
                    break;

                case "--to":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("Missing value for --to");
                    }

                    var toValue = args[++i];
                    if (!DateTimeOffset.TryParse(toValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var toParsed))
                    {
                        throw new ArgumentException($"Invalid datetime for --to: {toValue}");
                    }

                    to = toParsed;
                    break;

                case "--timezone":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("Missing value for --timezone");
                    }

                    timezone = args[++i];
                    break;

                case "--bucket":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("Missing value for --bucket");
                    }

                    bucket = ParseBucket(args[++i]);
                    break;

                case "--session-id":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("Missing value for --session-id");
                    }

                    sessionId = args[++i];
                    break;

                case "--cwd-contains":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("Missing value for --cwd-contains");
                    }

                    cwdContains = args[++i];
                    break;

                case "--expected-parent-model":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("Missing value for --expected-parent-model");
                    }

                    expectedParent = args[++i];
                    break;

                case "--expected-subagent-model":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("Missing value for --expected-subagent-model");
                    }

                    expectedSubagent = args[++i];
                    break;

                case "--top":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("Missing value for --top");
                    }

                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out top) || top < 1)
                    {
                        throw new ArgumentException("Invalid value for --top");
                    }

                    break;

                case "--format":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("Missing value for --format");
                    }

                    var formatValue = args[++i];
                    format = formatValue.Equals("json", StringComparison.OrdinalIgnoreCase)
                        ? OutputFormat.Json
                        : formatValue.Equals("markdown", StringComparison.OrdinalIgnoreCase)
                            ? OutputFormat.Markdown
                            : throw new ArgumentException($"Unknown format: {formatValue}");
                    break;

                case "--output":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("Missing value for --output");
                    }

                    output = args[++i];
                    break;

                default:
                    throw new ArgumentException($"Unknown option: {arg}");
            }
        }

        if (from.HasValue && to.HasValue && from.Value > to.Value)
        {
            throw new ArgumentException("--from must not be later than --to");
        }

        if (logPaths.Count == 0)
        {
            var defaultLog = ResolveDefaultLogPath();
            if (!string.IsNullOrWhiteSpace(defaultLog))
            {
                logPaths.Add(defaultLog);
            }
        }

        if (logPaths.Count == 0)
        {
            throw new FileNotFoundException("No log path is specified and no default log path is available.");
        }

        return new UsageOptions
        {
            LogPaths = logPaths,
            From = from,
            To = to,
            Timezone = timezone,
            Bucket = bucket,
            SessionIdFilter = sessionId,
            CwdContains = cwdContains,
            ExpectedParentModel = expectedParent,
            ExpectedSubagentModel = expectedSubagent,
            Top = top,
            Format = format,
            OutputPath = output,
            ShowHelp = showHelp
        };
    }

    public static void PrintUsage()
    {
        var usage = """
Usage:
  dotnet run --file scripts\codex-agent-usage-report.cs -- [options]

Options:
  --log <path>                  Input JSONL log file (repeatable)
  --from <datetime>             Analyze from this datetime (ISO-8601). Omit for open start.
  --to <datetime>               Analyze until this datetime (ISO-8601). Omit for open end.
  --timezone local|utc|Asia/Tokyo  Display timezone. Omit for local.
  --bucket 5m|15m|30m|1h         Timeline bucket width.
  --session-id <id>             Filter by session_id.
  --cwd-contains <text>         Filter by cwd substring.
  --expected-parent-model <v>    Check parent model expectation.
  --expected-subagent-model <v>  Check subagent model expectation.
  --top <n>                     Top command entries (default 20).
  --format markdown|json        Output format.
  --output <path>               Write output to file.
""";
        Console.WriteLine(usage);
    }

    private static TimeSpan ParseBucket(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "5m" => TimeSpan.FromMinutes(5),
            "15m" => TimeSpan.FromMinutes(15),
            "30m" => TimeSpan.FromMinutes(30),
            "1h" => TimeSpan.FromHours(1),
            _ => throw new ArgumentException($"Unknown bucket: {value}")
        };
    }

    private static string ResolveDefaultLogPath()
    {
        var explicitHome = Environment.GetEnvironmentVariable("CODEX_AGENT_USAGE_LOG");
        if (!string.IsNullOrWhiteSpace(explicitHome))
        {
            return explicitHome;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var home = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Path.Combine(userProfile, ".codex");
        }

        return Path.Combine(home, "logs", "agent-usage.jsonl");
    }
}

internal static class UsageReportGenerator
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly Dictionary<string, string> IanaTimezoneAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["asia/tokyo"] = "Tokyo Standard Time",
        ["asia/seoul"] = "Korea Standard Time",
        ["asia/shanghai"] = "China Standard Time",
        ["europe/london"] = "GMT Standard Time",
        ["america/new_york"] = "Eastern Standard Time",
        ["utc"] = "UTC"
    };

    public static UsageReport Generate(UsageOptions options)
    {
        var records = new List<UsageRecord>();
        var parseErrors = new List<ParseError>();

        foreach (var logPath in options.LogPaths.Distinct(PathComparer))
        {
            foreach (var (record, parseError) in ReadLogFile(logPath))
            {
                if (parseError is not null)
                {
                    parseError.SourcePath = logPath;
                    parseErrors.Add(parseError);
                    Console.Error.WriteLine(parseError.Exception);
                    continue;
                }

                if (record is not null && IsInRange(record.RecordedAt, options.From, options.To) &&
                    MatchesFilter(record, options))
                {
                    records.Add(record);
                }
            }
        }

        records.Sort((a, b) => a.RecordedAt.CompareTo(b.RecordedAt));
        DateTimeOffset? filteredMin = null;
        DateTimeOffset? filteredMax = null;
        if (records.Count > 0)
        {
            filteredMin = records.First().RecordedAt;
            filteredMax = records.Last().RecordedAt;
        }

        var tz = ResolveTimeZone(options.Timezone);
        var bucket = options.Bucket;
        var modelStats = new Dictionary<string, ModelUsageStats>(StringComparer.OrdinalIgnoreCase);
        var agentTypeStats = new Dictionary<string, AgentTypeUsageStats>(StringComparer.OrdinalIgnoreCase);
        var commandCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var timeline = new Dictionary<DateTimeOffset, TimelineStats>();
        var eventCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var turns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeSubagents = new Dictionary<string, ActiveSubagentRun>(StringComparer.OrdinalIgnoreCase);
        var completedRuns = new List<SubagentRunRecord>();
        var incompleteRuns = new List<SubagentRunRecord>();
        var orphanStops = new List<SubagentRunRecord>();

        int totalParseErrors = parseErrors.Count;

        foreach (var record in records)
        {
            if (!string.IsNullOrWhiteSpace(record.SessionId))
            {
                sessions.Add(record.SessionId);
            }

            if (!string.IsNullOrWhiteSpace(record.TurnId))
            {
                turns.Add(record.TurnId);
            }

            var eventName = record.Event;
            eventCounts[eventName] = eventCounts.TryGetValue(eventName, out var eventCount) ? eventCount + 1 : 1;

                if (record.AgentId is not null
                    && record.AgentId.Equals("00000000-0000-0000-0000-000000000000", StringComparison.OrdinalIgnoreCase))
                {
                    record.AgentId = null;
                }

            var model = record.Model ?? "(unknown)";
            var modelStat = modelStats.GetOrAdd(model);
            modelStat.Records++;

            if (!string.IsNullOrWhiteSpace(record.SessionId))
            {
                modelStat.Sessions.Add(record.SessionId);
            }

            var agentType = string.IsNullOrWhiteSpace(record.AgentType) ? "parent/unassigned" : record.AgentType;
            var agentTypeStat = agentTypeStats.GetOrAdd(agentType);
            agentTypeStat.TotalRecords++;

            if (record.DurationMs.HasValue)
            {
                switch (record.Event)
                {
                    case "SubagentStop":
                        modelStat.SubagentDurationMs += record.DurationMs.Value;
                        agentTypeStat.SubagentDurationMs += record.DurationMs.Value;
                        break;
                }
            }

            if (record.Event.Equals("PostToolUse", StringComparison.OrdinalIgnoreCase))
            {
                modelStat.ToolUseCount++;
                agentTypeStat.ToolUseCount++;

                if (record.ToolName is not null)
                {
                    agentTypeStat.ToolNameCounts[record.ToolName] =
                        agentTypeStat.ToolNameCounts.TryGetValue(record.ToolName, out var toolCount) ? toolCount + 1 : 1;
                }

                if (!string.IsNullOrWhiteSpace(record.Command))
                {
                    var normalized = NormalizeCommand(record.Command);
                    if (commandCounts.TryGetValue(normalized, out var count))
                    {
                        commandCounts[normalized] = count + 1;
                    }
                    else
                    {
                        commandCounts[normalized] = 1;
                    }
                }

                if (record.AgentId is not null
                    && activeSubagents.TryGetValue(record.AgentId, out var activeRun))
                {
                    activeRun.ToolUseCount++;
                    if (record.ToolName is not null && record.ToolName.Equals("apply_patch", StringComparison.OrdinalIgnoreCase))
                    {
                        activeRun.ApplyPatchCount++;
                    }

                    if (record.ToolName is not null && record.ToolName.Equals("Bash", StringComparison.OrdinalIgnoreCase))
                    {
                        activeRun.BashCount++;
                    }
                }
            }
            else if (record.Event.Equals("SubagentStart", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(record.AgentId))
                {
                    if (activeSubagents.ContainsKey(record.AgentId))
                    {
                        activeSubagents.Remove(record.AgentId);
                    }

                    activeSubagents[record.AgentId] = new ActiveSubagentRun
                    {
                        AgentId = record.AgentId,
                        AgentType = agentType,
                        Model = model,
                        SessionId = record.SessionId,
                        StartAt = record.RecordedAt,
                        TurnId = record.TurnId
                    };
                }

                modelStat.SubagentRunStarts++;
                agentTypeStat.SubagentRunStarts++;
            }
            else if (record.Event.Equals("SubagentStop", StringComparison.OrdinalIgnoreCase))
            {
                modelStat.SubagentRunStops++;
                agentTypeStat.SubagentRunStops++;

                if (!string.IsNullOrWhiteSpace(record.AgentId)
                    && activeSubagents.TryGetValue(record.AgentId, out var active))
                {
                    var duration = record.DurationMs ?? Math.Max(
                        0,
                        (long)Math.Round((record.RecordedAt - active.StartAt).TotalMilliseconds));
                    completedRuns.Add(new SubagentRunRecord
                    {
                        AgentId = active.AgentId,
                        AgentType = active.AgentType,
                        Model = active.Model,
                        StartAt = active.StartAt,
                        StopAt = record.RecordedAt,
                        DurationMs = duration,
                        ToolUseCount = active.ToolUseCount,
                        ApplyPatchCount = active.ApplyPatchCount,
                        BashCount = active.BashCount
                    });
                    activeSubagents.Remove(record.AgentId);
                }
                else
                {
                    orphanStops.Add(new SubagentRunRecord
                    {
                        AgentId = record.AgentId ?? "(unknown)",
                        AgentType = agentType,
                        Model = model,
                        StartAt = null,
                        StopAt = record.RecordedAt,
                        DurationMs = record.DurationMs,
                        ToolUseCount = 0,
                        ApplyPatchCount = 0,
                        BashCount = 0
                    });
                }
            }

            var bucketStart = FloorToBucket(TimeZoneInfo.ConvertTime(record.RecordedAt, tz), bucket);
            var timelineStats = timeline.GetOrAdd(bucketStart);
            timelineStats.TotalEvents++;

            if (record.Event.Equals("PostToolUse", StringComparison.OrdinalIgnoreCase))
            {
                timelineStats.ToolUseCount++;
            }

            if (record.ToolName is not null && record.ToolName.Equals("Bash", StringComparison.OrdinalIgnoreCase))
            {
                timelineStats.BashCount++;
            }

            if (record.ToolName is not null && record.ToolName.Equals("apply_patch", StringComparison.OrdinalIgnoreCase))
            {
                timelineStats.ApplyPatchCount++;
            }

            if (record.Event.Equals("SubagentStart", StringComparison.OrdinalIgnoreCase))
            {
                timelineStats.SubagentStartCount++;
            }

            if (record.Event.Equals("SubagentStop", StringComparison.OrdinalIgnoreCase))
            {
                timelineStats.SubagentStopCount++;
            }
        }

        foreach (var item in activeSubagents.Values)
        {
            incompleteRuns.Add(new SubagentRunRecord
            {
                AgentId = item.AgentId,
                AgentType = item.AgentType,
                Model = item.Model,
                StartAt = item.StartAt,
                StopAt = null,
                DurationMs = null,
                ToolUseCount = item.ToolUseCount,
                ApplyPatchCount = item.ApplyPatchCount,
                BashCount = item.BashCount
            });
        }

        var modelRows = modelStats
            .OrderByDescending(x => x.Value.Records)
            .Select(x => new ModelRow
            {
                Model = x.Key,
                Records = x.Value.Records,
                ToolUses = x.Value.ToolUseCount,
                SubagentRuns = x.Value.SubagentRunStops,
                SubagentDurationMinutes = x.Value.SubagentDurationMs / 60000.0,
                Sessions = x.Value.Sessions.Count
            })
            .ToList();

        var agentTypeRows = agentTypeStats
            .OrderBy(x => x.Key.Equals("parent/unassigned", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenByDescending(x => x.Value.SubagentRunStops)
            .Select(x => new AgentTypeRow
            {
                AgentType = x.Key,
                SubagentRuns = x.Value.SubagentRunStops,
                ToolUses = x.Value.ToolUseCount,
                Bash = x.Value.ToolNameCounts.TryGetValue("Bash", out var bash) ? bash : 0,
                ApplyPatch = x.Value.ToolNameCounts.TryGetValue("apply_patch", out var applyPatch) ? applyPatch : 0,
                TotalSubagentDurationMinutes = x.Value.SubagentDurationMs / 60000.0,
                AvgSubagentDurationSec = x.Value.SubagentRunStops == 0
                    ? null
                    : x.Value.SubagentDurationMs / 1000.0 / x.Value.SubagentRunStops
            })
            .ToList();

        var timelineRows = timeline
            .OrderBy(x => x.Key)
            .Select(x => new TimelineRow
            {
                Bucket = x.Key.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                TotalEvents = x.Value.TotalEvents,
                ToolUses = x.Value.ToolUseCount,
                Bash = x.Value.BashCount,
                ApplyPatch = x.Value.ApplyPatchCount,
                SubagentStarts = x.Value.SubagentStartCount,
                SubagentStops = x.Value.SubagentStopCount
            })
            .ToList();

        var topCommands = commandCounts
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Take(options.Top)
            .Select(x => new KeyValuePair<string, int>(x.Key, x.Value))
            .ToList();

        var workflowChecks = BuildWorkflowChecks(options, agentTypeRows, records);

        var parentApplyPatch = agentTypeRows.FirstOrDefault(x => x.AgentType == "parent/unassigned")?.ApplyPatch ?? 0;
        var subagentDurationSec = modelRows.Sum(x => x.SubagentDurationMinutes) * 60.0;

        return new UsageReport
        {
            GeneratedAt = DateTimeOffset.UtcNow.ToString("O"),
            Options = options,
            PeriodFrom = filteredMin?.ToString("O"),
            PeriodTo = filteredMax?.ToString("O"),
            Timezone = options.Timezone,
            ParsedRecordCount = records.Count,
            ParseErrorCount = totalParseErrors,
            ParseErrors = parseErrors,
            Sessions = sessions.Count,
            Turns = turns.Count,
            EventCounts = eventCounts,
            ModelRows = modelRows,
            AgentTypeRows = agentTypeRows,
            SubagentRuns = completedRuns
                .Concat(orphanStops)
                .Concat(incompleteRuns)
                .OrderBy(x => x.StartAt ?? x.StopAt ?? DateTimeOffset.MinValue)
                .ToList(),
            TimelineRows = timelineRows,
            TopCommands = topCommands,
            WorkflowChecks = workflowChecks,
            NotAvailableItems =
            [
                "Token usage (no token field in current logs).",
                "tool execution elapsed time per command (no start time field in PostToolUse logs).",
                "Bash exit code (tool success result is not stored in current logger)."
            ],
            ParentApplyPatchCount = parentApplyPatch,
            TotalSubagentDurationMinutes = subagentDurationSec / 60.0,
            IncompleteSubagentRuns = incompleteRuns.Count,
            OrphanSubagentStops = orphanStops.Count
        };
    }

    private static List<(UsageRecord? record, ParseError? parseError)> ReadLogFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Log file does not exist", path);
        }

        var rows = new List<(UsageRecord?, ParseError?)>();
        using var reader = new StreamReader(path, Encoding.UTF8);
        var lineNumber = 0;

        while (true)
        {
            var line = reader.ReadLine();
            if (line is null)
            {
                break;
            }

            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var parsed = ParseRecord(doc.RootElement);
                if (parsed is null)
                {
                    continue;
                }

                rows.Add((parsed, null));
            }
            catch (JsonException ex)
            {
                rows.Add((null, new ParseError
                {
                    SourcePath = path,
                    Line = lineNumber,
                    Message = ex.Message,
                    Exception = ex.ToString()
                }));
            }
        }

        return rows;
    }

    private static UsageRecord? ParseRecord(JsonElement element)
    {
        var recordedAt = GetDateTimeOffset(element, "recorded_at");
        if (!recordedAt.HasValue)
        {
            return null;
        }

        var duration = GetInt64(element, "duration_ms");
        return new UsageRecord
        {
            RecordedAt = recordedAt.Value,
            Event = GetString(element, "event") ?? "Unknown",
            SessionId = GetString(element, "session_id"),
            TurnId = GetString(element, "turn_id"),
            AgentId = GetString(element, "agent_id"),
            AgentType = GetString(element, "agent_type"),
            Model = GetString(element, "model"),
            PermissionMode = GetString(element, "permission_mode"),
            Cwd = GetString(element, "cwd"),
            TranscriptPath = GetString(element, "transcript_path"),
            DurationMs = duration,
            ToolName = GetString(element, "tool_name"),
            Command = GetString(element, "command"),
            ToolResponseSize = GetInt64(element, "tool_response_size"),
            ToolResponsePreview = GetString(element, "tool_response_preview")
        };
    }

    private static bool IsInRange(DateTimeOffset value, DateTimeOffset? from, DateTimeOffset? to)
    {
        if (from.HasValue && value < from.Value)
        {
            return false;
        }

        if (to.HasValue && value > to.Value)
        {
            return false;
        }

        return true;
    }

    private static bool MatchesFilter(UsageRecord record, UsageOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.SessionIdFilter)
            && !string.Equals(record.SessionId, options.SessionIdFilter, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(options.CwdContains)
            && !((record.Cwd ?? string.Empty).Contains(options.CwdContains!, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }

    private static DateTimeOffset FloorToBucket(DateTimeOffset value, TimeSpan bucket)
    {
        var minutes = value.Minute;
        var seconds = value.Second;
        var totalMinutes = value.Hour * 60 + minutes;
        var bucketMinutes = (int)bucket.TotalMinutes;
        var flooredMinutes = (totalMinutes / bucketMinutes) * bucketMinutes;
        var deltaMinutes = flooredMinutes - totalMinutes;
        var floored = value
            .AddHours(-value.Hour)
            .AddMinutes(-value.Minute)
            .AddSeconds(-value.Second)
            .AddMilliseconds(-value.Millisecond)
            .AddMinutes(deltaMinutes);
        return floored;
    }

    private static TimeZoneInfo ResolveTimeZone(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.Local;
        }

        if (value.Equals("utc", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(value);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            if (IanaTimezoneAlias.TryGetValue(value, out var windowsId))
            {
                return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
            }

            throw;
        }
    }

    private static List<WorkflowCheck> BuildWorkflowChecks(
        UsageOptions options,
        List<AgentTypeRow> agentTypeRows,
        IReadOnlyList<UsageRecord> records)
    {
        var checks = new List<WorkflowCheck>();
        int GetRuns(string agentType)
        {
            return agentTypeRows.FirstOrDefault(x => x.AgentType.Equals(agentType, StringComparison.OrdinalIgnoreCase))?.SubagentRuns ?? 0;
        }

        var slicePrepRuns = GetRuns("slice-prep");
        checks.Add(new WorkflowCheck(
            "slice-prep ran",
            slicePrepRuns > 0 ? "PASS" : "WARN",
            $"slice-prep runs = {slicePrepRuns}"
        ));

        var sliceImplRuns = GetRuns("slice-impl");
        checks.Add(new WorkflowCheck(
            "slice-impl ran",
            sliceImplRuns > 0 ? "PASS" : "WARN",
            $"slice-impl runs = {sliceImplRuns}"
        ));

        var crossChecks = GetRuns("cross-slice-verification-kernel");
        checks.Add(new WorkflowCheck(
            "cross-slice verification ran",
            crossChecks > 0 ? "PASS" : "WARN",
            $"cross-slice-verification-kernel runs = {crossChecks}"
        ));

        var residualRuns = GetRuns("residual-decision-gate");
        checks.Add(new WorkflowCheck(
            "residual decision gate ran",
            residualRuns > 0 ? "PASS" : "INFO",
            $"residual-decision-gate runs = {residualRuns}"
        ));

        var parentApplyPatch = agentTypeRows.FirstOrDefault(x => x.AgentType.Equals("parent/unassigned", StringComparison.OrdinalIgnoreCase))?.ApplyPatch ?? 0;
        checks.Add(new WorkflowCheck(
            "parent direct apply_patch",
            parentApplyPatch == 0 ? "PASS" : "WARN",
            $"parent/unassigned apply_patch = {parentApplyPatch}"
        ));

        if (!string.IsNullOrWhiteSpace(options.ExpectedParentModel))
        {
            var observed = MostFrequentModel(records.Where(x => string.IsNullOrWhiteSpace(x.AgentType)));
            if (observed is null)
            {
                checks.Add(new WorkflowCheck(
                    "parent model",
                    "WARN",
                    "no parent model data in scope"
                ));
            }
            else
            {
                var result = observed.Equals(options.ExpectedParentModel, StringComparison.OrdinalIgnoreCase) ? "PASS" : "WARN";
                checks.Add(new WorkflowCheck(
                    "parent model",
                    result,
                    $"expected={options.ExpectedParentModel}, observed={observed}"
                ));
            }
        }

        if (!string.IsNullOrWhiteSpace(options.ExpectedSubagentModel))
        {
            var observedSubagent = MostFrequentModel(records.Where(x => !string.IsNullOrWhiteSpace(x.AgentType) && !x.AgentType!.Equals("parent/unassigned", StringComparison.OrdinalIgnoreCase)));
            if (observedSubagent is null)
            {
                checks.Add(new WorkflowCheck(
                    "subagent model",
                    "WARN",
                    "no subagent model data in scope"
                ));
            }
            else
            {
                var result = observedSubagent.Equals(options.ExpectedSubagentModel, StringComparison.OrdinalIgnoreCase) ? "PASS" : "WARN";
                checks.Add(new WorkflowCheck(
                    "subagent model",
                    result,
                    $"expected={options.ExpectedSubagentModel}, observed={observedSubagent}"
                ));
            }
        }

        return checks;
    }

    private static string? MostFrequentModel(IEnumerable<UsageRecord> records)
    {
        return records
            .Select(x => x.Model)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Count())
            .Select(x => x.Key)
            .FirstOrDefault();
    }

    private static string NormalizeCommand(string command)
    {
        var compact = command.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        if (compact.Length > 80)
        {
            compact = compact[..80] + "...";
        }

        return compact;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null => null,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.ToString()
        };
    }

    private static long? GetInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetInt64(),
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static DateTimeOffset? GetDateTimeOffset(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed;
        }

        if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed2))
        {
            return parsed2;
        }

        return null;
    }
}

internal sealed class UsageReport
{
    public string GeneratedAt { get; set; } = string.Empty;
    [JsonIgnore]
    public UsageOptions Options { get; set; } = null!;
    public string? PeriodFrom { get; set; }
    public string? PeriodTo { get; set; }
    public string Timezone { get; set; } = "local";
    public int ParsedRecordCount { get; set; }
    public int ParseErrorCount { get; set; }
    public List<ParseError> ParseErrors { get; set; } = [];
    public int Sessions { get; set; }
    public int Turns { get; set; }
    public Dictionary<string, int> EventCounts { get; set; } = [];
    public List<ModelRow> ModelRows { get; set; } = [];
    public List<AgentTypeRow> AgentTypeRows { get; set; } = [];
    public List<SubagentRunRecord> SubagentRuns { get; set; } = [];
    public List<TimelineRow> TimelineRows { get; set; } = [];
    public List<KeyValuePair<string, int>> TopCommands { get; set; } = [];
    public List<WorkflowCheck> WorkflowChecks { get; set; } = [];
    public List<string> NotAvailableItems { get; set; } = [];
    public int ParentApplyPatchCount { get; set; }
    public double TotalSubagentDurationMinutes { get; set; }
    public int IncompleteSubagentRuns { get; set; }
    public int OrphanSubagentStops { get; set; }

    public string ToJson()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        var json = JsonSerializer.Serialize(this, options);
        return json;
    }

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Codex Usage Report");
        sb.AppendLine();
        sb.AppendLine($"Generated: {GeneratedAt}");
        if (PeriodFrom is not null || PeriodTo is not null)
        {
            sb.AppendLine($"Period: {PeriodFrom ?? "-"} - {PeriodTo ?? "-"}");
        }
        else
        {
            sb.AppendLine("Period: no records");
        }

        sb.AppendLine($"Timezone: {Timezone}");
        sb.AppendLine();
        sb.AppendLine("## Overview");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|---|---:|");
        sb.AppendLine($"| Parsed records | {ParsedRecordCount} |");
        sb.AppendLine($"| Parse errors | {ParseErrorCount} |");
        sb.AppendLine($"| Sessions | {Sessions} |");
        sb.AppendLine($"| Turns | {Turns} |");
        sb.AppendLine($"| parent/unassigned apply_patch | {ParentApplyPatchCount} |");
        sb.AppendLine($"| incomplete subagent runs | {IncompleteSubagentRuns} |");
        sb.AppendLine($"| orphan subagent stops | {OrphanSubagentStops} |");
        sb.AppendLine($"| total subagent duration (min) | {TotalSubagentDurationMinutes:F1} |");
        sb.AppendLine();

        sb.AppendLine("| Event | Count |");
        sb.AppendLine("|---|---:|");
        foreach (var item in EventCounts.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"| {item.Key} | {item.Value} |");
        }

        sb.AppendLine();
        sb.AppendLine("## By model");
        sb.AppendLine();
        sb.AppendLine("| Model | Records | Tool uses | Subagent runs | Subagent duration (min) | Sessions |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var row in ModelRows)
        {
            sb.AppendLine(
                $"| {row.Model} | {row.Records} | {row.ToolUses} | {row.SubagentRuns} | {row.SubagentDurationMinutes:F1} | {row.Sessions} |");
        }

        sb.AppendLine();
        sb.AppendLine("## By agent type");
        sb.AppendLine();
        sb.AppendLine("| Agent type | Subagent runs | Tool uses | Bash | apply_patch | Total subagent duration (min) | Avg subagent duration (sec) |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var row in AgentTypeRows)
        {
            sb.AppendLine(
                $"| {row.AgentType} | {row.SubagentRuns} | {row.ToolUses} | {row.Bash} | {row.ApplyPatch} | {row.TotalSubagentDurationMinutes:F1} | {(row.AvgSubagentDurationSec.HasValue ? row.AvgSubagentDurationSec.Value.ToString("F1", CultureInfo.InvariantCulture) : "-")} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Subagent runs");
        sb.AppendLine();
        sb.AppendLine("| Start | End | Agent type | Agent ID | Model | Duration | Tool uses | apply_patch |");
        sb.AppendLine("|---|---|---|---|---|---:|---:|---:|");
        foreach (var row in SubagentRuns)
        {
            sb.AppendLine(
                $"| {ToDisplayDate(row.StartAt)} | {ToDisplayDate(row.StopAt)} | {row.AgentType} | {row.AgentId} | {row.Model} | {FormatDuration(row.DurationMs)} | {row.ToolUseCount} | {row.ApplyPatchCount} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Timeline");
        sb.AppendLine();
        sb.AppendLine("| Bucket | Total events | Tool uses | Bash | apply_patch | Subagent starts | Subagent stops |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var row in TimelineRows)
        {
            sb.AppendLine(
                $"| {row.Bucket} | {row.TotalEvents} | {row.ToolUses} | {row.Bash} | {row.ApplyPatch} | {row.SubagentStarts} | {row.SubagentStops} |");
        }

        sb.AppendLine();
        sb.AppendLine($"## Top {TopCommands.Count} commands");
        sb.AppendLine();
        sb.AppendLine("| Command | Count |");
        sb.AppendLine("|---|---:|");
        foreach (var item in TopCommands)
        {
            sb.AppendLine($"| `{item.Key}` | {item.Value} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Workflow check");
        sb.AppendLine();
        sb.AppendLine("| Check | Result | Evidence |");
        sb.AppendLine("|---|---|---|");
        foreach (var item in WorkflowChecks)
        {
            sb.AppendLine($"| {item.Check} | {item.Result} | {item.Evidence} |");
        }

        if (ParseErrors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Parse errors (sample)");
            sb.AppendLine();
            sb.AppendLine("| Source | Line | Message |");
            sb.AppendLine("|---|---:|---|");
            foreach (var item in ParseErrors.Take(20))
            {
                sb.AppendLine($"| {item.SourcePath} | {item.Line} | {item.Message} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Not available in current log schema");
        sb.AppendLine();
        foreach (var item in NotAvailableItems)
        {
            sb.AppendLine($"- {item}");
        }

        return sb.ToString();
    }

    private static string ToDisplayDate(DateTimeOffset? value)
    {
        return value?.ToString("O", CultureInfo.InvariantCulture) ?? "-";
    }

    private static string FormatDuration(long? ms)
    {
        if (!ms.HasValue)
        {
            return "-";
        }

        var totalSec = ms.Value / 1000.0;
        return $"{totalSec:F1}s";
    }
}

internal sealed class UsageRecord
{
    public DateTimeOffset RecordedAt { get; init; }
    public string Event { get; init; } = "Unknown";
    public string? SessionId { get; set; }
    public string? TurnId { get; init; }
    public string? AgentId { get; set; }
    public string? AgentType { get; init; }
    public string? Model { get; init; }
    public string? PermissionMode { get; init; }
    public string? Cwd { get; init; }
    public string? TranscriptPath { get; init; }
    public long? DurationMs { get; init; }
    public string? ToolName { get; init; }
    public string? Command { get; init; }
    public long? ToolResponseSize { get; init; }
    public string? ToolResponsePreview { get; init; }
}

internal sealed class ParseError
{
    public string SourcePath { get; set; } = string.Empty;
    public int Line { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Exception { get; set; } = string.Empty;
}

internal sealed class ModelUsageStats
{
    public int Records { get; set; }
    public int ToolUseCount { get; set; }
    public int SubagentRunStarts { get; set; }
    public int SubagentRunStops { get; set; }
    public long SubagentDurationMs { get; set; }
    public HashSet<string> Sessions { get; } = [];
}

internal sealed class AgentTypeUsageStats
{
    public int TotalRecords { get; set; }
    public int ToolUseCount { get; set; }
    public int SubagentRunStarts { get; set; }
    public int SubagentRunStops { get; set; }
    public long SubagentDurationMs { get; set; }
    public Dictionary<string, int> ToolNameCounts { get; } = [];
}

internal sealed class TimelineStats
{
    public int TotalEvents { get; set; }
    public int ToolUseCount { get; set; }
    public int BashCount { get; set; }
    public int ApplyPatchCount { get; set; }
    public int SubagentStartCount { get; set; }
    public int SubagentStopCount { get; set; }
}

internal sealed class ActiveSubagentRun
{
    public required string AgentId { get; set; }
    public required string AgentType { get; set; }
    public string? Model { get; set; }
    public string? SessionId { get; set; }
    public string? TurnId { get; set; }
    public DateTimeOffset StartAt { get; set; }
    public int ToolUseCount { get; set; }
    public int ApplyPatchCount { get; set; }
    public int BashCount { get; set; }
}

internal sealed class SubagentRunRecord
{
    public string AgentId { get; set; } = string.Empty;
    public string? AgentType { get; set; }
    public string? Model { get; set; }
    public DateTimeOffset? StartAt { get; set; }
    public DateTimeOffset? StopAt { get; set; }
    public long? DurationMs { get; set; }
    public int ToolUseCount { get; set; }
    public int BashCount { get; set; }
    public int ApplyPatchCount { get; set; }
}

internal sealed class WorkflowCheck
{
    public WorkflowCheck(string check, string result, string evidence)
    {
        Check = check;
        Result = result;
        Evidence = evidence;
    }

    public string Check { get; }
    public string Result { get; }
    public string Evidence { get; }
}

internal sealed class ModelRow
{
    public string Model { get; set; } = string.Empty;
    public int Records { get; set; }
    public int ToolUses { get; set; }
    public int SubagentRuns { get; set; }
    public double SubagentDurationMinutes { get; set; }
    public int Sessions { get; set; }
}

internal sealed class AgentTypeRow
{
    public string AgentType { get; set; } = string.Empty;
    public int SubagentRuns { get; set; }
    public int ToolUses { get; set; }
    public int Bash { get; set; }
    public int ApplyPatch { get; set; }
    public double TotalSubagentDurationMinutes { get; set; }
    public double? AvgSubagentDurationSec { get; set; }
}

internal sealed class TimelineRow
{
    public string Bucket { get; set; } = string.Empty;
    public int TotalEvents { get; set; }
    public int ToolUses { get; set; }
    public int Bash { get; set; }
    public int ApplyPatch { get; set; }
    public int SubagentStarts { get; set; }
    public int SubagentStops { get; set; }
}

internal static class Extensions
{
    public static TValue GetOrAdd<TKey, TValue>(this Dictionary<TKey, TValue> source, TKey key) where TKey : notnull
        where TValue : class, new()
    {
        if (!source.TryGetValue(key, out var value))
        {
            value = new TValue();
            source.Add(key, value);
        }

        return value;
    }
}
