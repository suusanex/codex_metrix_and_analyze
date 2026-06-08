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

internal enum AllocationBasis
{
    Weighted,
    Duration,
    ToolElapsed,
    ToolCount,
    ResponseSize
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
    public OutputFormat Format { get; init; } = OutputFormat.Markdown;
    public string? OutputPath { get; init; }
    public double? LimitBefore { get; init; }
    public double? LimitAfter { get; init; }
    public double? LimitDelta { get; init; }
    public string LimitUnit { get; init; } = "unknown";
    public AllocationBasis AllocationBasis { get; init; } = AllocationBasis.Weighted;
    public bool ShowHelp { get; init; }

    public static UsageOptions Parse(string[] args)
    {
        var logPaths = new List<string>();
        DateTimeOffset? from = null;
        DateTimeOffset? to = null;
        var timezone = "local";
        var bucket = TimeSpan.FromMinutes(15);
        string? sessionId = null;
        string? cwdContains = null;
        var format = OutputFormat.Markdown;
        string? output = null;
        double? limitBefore = null;
        double? limitAfter = null;
        double? limitDelta = null;
        var limitUnit = "unknown";
        var allocationBasis = AllocationBasis.Weighted;
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
                    logPaths.Add(RequireValue(args, ref i, "--log"));
                    break;

                case "--from":
                    from = ParseDateTimeOffset(RequireValue(args, ref i, "--from"), "--from");
                    break;

                case "--to":
                    to = ParseDateTimeOffset(RequireValue(args, ref i, "--to"), "--to");
                    break;

                case "--timezone":
                    timezone = RequireValue(args, ref i, "--timezone");
                    break;

                case "--bucket":
                    bucket = ParseBucket(RequireValue(args, ref i, "--bucket"));
                    break;

                case "--session-id":
                    sessionId = RequireValue(args, ref i, "--session-id");
                    break;

                case "--cwd-contains":
                    cwdContains = RequireValue(args, ref i, "--cwd-contains");
                    break;

                case "--format":
                    format = ParseFormat(RequireValue(args, ref i, "--format"));
                    break;

                case "--output":
                    output = RequireValue(args, ref i, "--output");
                    break;

                case "--limit-before":
                    limitBefore = ParseDouble(RequireValue(args, ref i, "--limit-before"), "--limit-before");
                    break;

                case "--limit-after":
                    limitAfter = ParseDouble(RequireValue(args, ref i, "--limit-after"), "--limit-after");
                    break;

                case "--limit-delta":
                    limitDelta = ParseDouble(RequireValue(args, ref i, "--limit-delta"), "--limit-delta");
                    break;

                case "--limit-unit":
                    limitUnit = RequireValue(args, ref i, "--limit-unit");
                    break;

                case "--allocation-basis":
                    allocationBasis = ParseAllocationBasis(RequireValue(args, ref i, "--allocation-basis"));
                    break;

                default:
                    throw new ArgumentException($"Unknown option: {arg}");
            }
        }

        if (from.HasValue && to.HasValue && from.Value > to.Value)
        {
            throw new ArgumentException("--from must not be later than --to");
        }

        if (limitDelta is null && limitBefore.HasValue && limitAfter.HasValue)
        {
            limitDelta = limitBefore.Value - limitAfter.Value;
        }

        if (limitDelta.HasValue && limitBefore.HasValue && limitAfter.HasValue)
        {
            var computed = limitBefore.Value - limitAfter.Value;
            if (Math.Abs(computed - limitDelta.Value) > 0.000001d)
            {
                throw new ArgumentException("--limit-delta does not match --limit-before - --limit-after");
            }
        }

        if (logPaths.Count == 0)
        {
            logPaths.AddRange(ResolveDefaultLogPaths());
        }

        if (logPaths.Count == 0)
        {
            throw new FileNotFoundException("No log path is specified and no default log path is available.");
        }

        return new UsageOptions
        {
            LogPaths = logPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            From = from,
            To = to,
            Timezone = timezone,
            Bucket = bucket,
            SessionIdFilter = sessionId,
            CwdContains = cwdContains,
            Format = format,
            OutputPath = output,
            LimitBefore = limitBefore,
            LimitAfter = limitAfter,
            LimitDelta = limitDelta,
            LimitUnit = limitUnit,
            AllocationBasis = allocationBasis,
            ShowHelp = showHelp
        };
    }

    public static void PrintUsage()
    {
        var usage = """
Usage:
  dotnet run --file scripts\codex-agent-usage-report.cs -- [options]

Options:
  --log <path>                    Input JSONL log file (repeatable)
  --from <datetime>               Analyze from this datetime (ISO-8601)
  --to <datetime>                 Analyze until this datetime (ISO-8601)
  --timezone local|utc|Asia/Tokyo Timeline bucket/display timezone
  --bucket 5m|15m|30m|1h          Timeline bucket width
  --session-id <id>               Filter by session_id
  --cwd-contains <text>           Filter by cwd substring
  --limit-before <number>         Manual limit value before the observation window
  --limit-after <number>          Manual limit value after the observation window
  --limit-delta <number>          Observed delta to allocate
  --limit-unit percent|credits|points|unknown
  --allocation-basis weighted|duration|tool-elapsed|tool-count|response-size
  --format markdown|json          Output format
  --output <path>                 Write output to file
""";
        Console.WriteLine(usage);
    }

    private static string RequireValue(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {optionName}");
        }

        return args[++index];
    }

    private static DateTimeOffset ParseDateTimeOffset(string value, string optionName)
    {
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            throw new ArgumentException($"Invalid datetime for {optionName}: {value}");
        }

        return parsed;
    }

    private static double ParseDouble(string value, string optionName)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new ArgumentException($"Invalid number for {optionName}: {value}");
        }

        return parsed;
    }

    private static OutputFormat ParseFormat(string value)
    {
        return value.Equals("json", StringComparison.OrdinalIgnoreCase)
            ? OutputFormat.Json
            : value.Equals("markdown", StringComparison.OrdinalIgnoreCase)
                ? OutputFormat.Markdown
                : throw new ArgumentException($"Unknown format: {value}");
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

    private static AllocationBasis ParseAllocationBasis(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "weighted" => AllocationBasis.Weighted,
            "duration" => AllocationBasis.Duration,
            "tool-elapsed" => AllocationBasis.ToolElapsed,
            "tool-count" => AllocationBasis.ToolCount,
            "response-size" => AllocationBasis.ResponseSize,
            _ => throw new ArgumentException($"Unknown allocation basis: {value}")
        };
    }

    private static IEnumerable<string> ResolveDefaultLogPaths()
    {
        foreach (var candidate in GetBaseCandidates())
        {
            foreach (var resolved in ResolveDefaultLogPathCandidates(candidate))
            {
                yield return resolved;
            }
        }
    }

    private static IEnumerable<string> GetBaseCandidates()
    {
        var explicitObservation = Environment.GetEnvironmentVariable("CODEX_AGENT_OBSERVATION_LOG");
        if (!string.IsNullOrWhiteSpace(explicitObservation))
        {
            yield return explicitObservation;
        }

        var explicitLegacy = Environment.GetEnvironmentVariable("CODEX_AGENT_USAGE_LOG");
        if (!string.IsNullOrWhiteSpace(explicitLegacy))
        {
            yield return explicitLegacy;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = Path.Combine(userProfile, ".codex");
        }

        yield return Path.Combine(codexHome, "logs", "agent-observations.jsonl");
        yield return Path.Combine(codexHome, "logs", "agent-usage.jsonl");
    }

    private static IEnumerable<string> ResolveDefaultLogPathCandidates(string basePath)
    {
        var fullBasePath = Path.GetFullPath(basePath);
        if (File.Exists(fullBasePath))
        {
            yield return fullBasePath;
            yield break;
        }

        var directory = Path.GetDirectoryName(fullBasePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            yield break;
        }

        var fileName = Path.GetFileNameWithoutExtension(fullBasePath);
        var extension = Path.GetExtension(fullBasePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".jsonl";
        }

        var today = DateTimeOffset.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var todayPath = Path.Combine(directory, $"{fileName}-{today}{extension}");
        if (File.Exists(todayPath))
        {
            yield return todayPath;
        }

        var latest = Directory
            .EnumerateFiles(directory, $"{fileName}-*{extension}")
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(latest))
        {
            yield return latest;
        }
    }
}

internal static class UsageReportGenerator
{
    private static readonly Dictionary<string, string> IanaTimezoneAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["asia/tokyo"] = "Tokyo Standard Time",
        ["asia/seoul"] = "Korea Standard Time",
        ["asia/shanghai"] = "China Standard Time",
        ["europe/london"] = "GMT Standard Time",
        ["america/new_york"] = "Eastern Standard Time",
        ["utc"] = "UTC"
    };

    public static UsageReportOutput Generate(UsageOptions options)
    {
        var records = new List<UsageRecord>();
        var parseErrors = new List<ParseError>();

        foreach (var logPath in options.LogPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var (record, parseError) in ReadLogFile(logPath))
            {
                if (parseError is not null)
                {
                    parseErrors.Add(parseError);
                    continue;
                }

                if (record is null)
                {
                    continue;
                }

                if (!IsInRange(record.RecordedAt, options.From, options.To))
                {
                    continue;
                }

                if (!MatchesFilter(record, options))
                {
                    continue;
                }

                records.Add(record);
            }
        }

        records.Sort((a, b) => a.RecordedAt.CompareTo(b.RecordedAt));
        var timezone = ResolveTimeZone(options.Timezone);
        var warnings = new List<string>();
        var notAvailable = new List<string>();
        if (records.Any(x => !x.IsV2))
        {
            warnings.Add("旧 agent-usage ログでは tool_use_id / trace/span / repository metadata など一部の v2 項目が欠落する。");
            notAvailable.Add("旧ログ行では trace/span IDs, unknown_payload_keys, capture_policy を復元できない。");
        }

        var toolInvocations = BuildToolInvocations(records, warnings);
        var subagentRuns = BuildSubagentRuns(records, warnings);
        var correlationIssues = BuildCorrelationIssues(toolInvocations, subagentRuns);

        var modelAcc = new Dictionary<string, WorkAccumulator>(StringComparer.OrdinalIgnoreCase);
        var agentTypeAcc = new Dictionary<string, WorkAccumulator>(StringComparer.OrdinalIgnoreCase);
        var toolAcc = new Dictionary<string, WorkAccumulator>(StringComparer.OrdinalIgnoreCase);
        var repoAcc = new Dictionary<string, WorkAccumulator>(StringComparer.OrdinalIgnoreCase);
        var timelineAcc = new Dictionary<string, WorkAccumulator>(StringComparer.OrdinalIgnoreCase);
        var sessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var turns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var repositories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var eventCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var commandKindCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

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

            repositories.Add(record.RepositoryKey);
            models.Add(record.ModelKey);
            Increment(eventCounts, record.Event);

            var bucket = FloorToBucket(TimeZoneInfo.ConvertTime(record.RecordedAt, timezone), options.Bucket)
                .ToString("O", CultureInfo.InvariantCulture);

            AddRecord(modelAcc.GetOrAdd(record.ModelKey), record);
            AddRecord(agentTypeAcc.GetOrAdd(record.AgentTypeKey), record);
            AddRecord(toolAcc.GetOrAdd(record.ToolKey), record);
            AddRecord(repoAcc.GetOrAdd(record.RepositoryKey), record);
            AddRecord(timelineAcc.GetOrAdd(bucket), record);
        }

        foreach (var invocation in toolInvocations)
        {
            AddInvocation(modelAcc.GetOrAdd(invocation.ModelKey), invocation, commandKindCounts);
            AddInvocation(agentTypeAcc.GetOrAdd(invocation.AgentTypeKey), invocation, null);
            AddInvocation(toolAcc.GetOrAdd(invocation.ToolKey), invocation, null);
            AddInvocation(repoAcc.GetOrAdd(invocation.RepositoryKey), invocation, null);

            var bucket = FloorToBucket(TimeZoneInfo.ConvertTime(invocation.RecordedAt, timezone), options.Bucket)
                .ToString("O", CultureInfo.InvariantCulture);
            AddInvocation(timelineAcc.GetOrAdd(bucket), invocation, null);
        }

        foreach (var run in subagentRuns)
        {
            AddSubagentRun(modelAcc.GetOrAdd(run.ModelKey), run);
            AddSubagentRun(agentTypeAcc.GetOrAdd(run.AgentTypeKey), run);
            AddSubagentRun(toolAcc.GetOrAdd("(subagent-run)"), run);
            AddSubagentRun(repoAcc.GetOrAdd(run.RepositoryKey), run);

            var bucket = FloorToBucket(TimeZoneInfo.ConvertTime(run.RecordedAt, timezone), options.Bucket)
                .ToString("O", CultureInfo.InvariantCulture);
            AddSubagentRun(timelineAcc.GetOrAdd(bucket), run);
        }

        var modelRows = BuildRows(modelAcc)
            .Select(entry => CreateWorkRow(entry.Key, entry.Value))
            .OrderByDescending(x => x.weighted_work_score)
            .ThenBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        NormalizeShares(modelRows);

        var agentTypeRows = BuildRows(agentTypeAcc)
            .Select(entry => CreateWorkRow(entry.Key, entry.Value))
            .OrderByDescending(x => x.weighted_work_score)
            .ThenBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        NormalizeShares(agentTypeRows);

        var toolRows = BuildRows(toolAcc)
            .Select(entry => CreateWorkRow(entry.Key, entry.Value))
            .OrderByDescending(x => x.weighted_work_score)
            .ThenBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        NormalizeShares(toolRows);

        var repositoryRows = BuildRows(repoAcc)
            .Select(entry => CreateWorkRow(entry.Key, entry.Value))
            .OrderByDescending(x => x.weighted_work_score)
            .ThenBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        NormalizeShares(repositoryRows);

        var timelineRows = BuildRows(timelineAcc)
            .Select(entry => CreateWorkRow(entry.Key, entry.Value))
            .OrderBy(x => x.name, StringComparer.Ordinal)
            .ToList();
        NormalizeShares(timelineRows);

        var allocation = BuildAllocation(options, modelRows);
        if (allocation is null)
        {
            notAvailable.Add("limit delta allocation は --limit-delta または --limit-before / --limit-after 指定時のみ算出する。");
        }

        var summary = new SummarySection
        {
            generated_at = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            period_from = records.FirstOrDefault()?.RecordedAt.ToString("O", CultureInfo.InvariantCulture),
            period_to = records.LastOrDefault()?.RecordedAt.ToString("O", CultureInfo.InvariantCulture),
            timezone = timezone.Id,
            parsed_records = records.Count,
            parse_errors = parseErrors.Count,
            sessions = sessions.Count,
            turns = turns.Count,
            repositories = repositories.Count,
            models = models.Count,
            tool_invocations = toolInvocations.Count,
            matched_tool_invocations = toolInvocations.Count(x => x.CorrelationStatus == "matched"),
            missing_pre = toolInvocations.Count(x => x.CorrelationStatus == "missing_pre"),
            missing_post = toolInvocations.Count(x => x.CorrelationStatus == "missing_post"),
            duplicate_tool = toolInvocations.Count(x => x.CorrelationStatus == "duplicate"),
            subagent_runs = subagentRuns.Count,
            missing_start = subagentRuns.Count(x => x.CorrelationStatus == "missing_start"),
            missing_stop = subagentRuns.Count(x => x.CorrelationStatus == "missing_stop"),
            weighted_score_total = modelRows.Sum(x => x.weighted_work_score),
            command_kind_counts = commandKindCounts,
            event_counts = eventCounts,
            limit_delta = allocation?.limit_delta,
            limit_unit = allocation?.limit_unit
        };

        return new UsageReportOutput
        {
            summary = summary,
            model_rows = modelRows,
            agent_type_rows = agentTypeRows,
            tool_rows = toolRows,
            repository_rows = repositoryRows,
            timeline_rows = timelineRows,
            correlation_issues = correlationIssues,
            allocation = allocation,
            warnings = warnings,
            not_available = notAvailable,
            parse_errors = parseErrors
        };
    }

    private static AllocationSection? BuildAllocation(UsageOptions options, List<WorkRow> modelRows)
    {
        if (!options.LimitDelta.HasValue)
        {
            return null;
        }

        double GetBasisValue(WorkRow row)
        {
            return options.AllocationBasis switch
            {
                AllocationBasis.Duration => row.total_subagent_duration_ms + row.total_tool_elapsed_ms,
                AllocationBasis.ToolElapsed => row.total_tool_elapsed_ms,
                AllocationBasis.ToolCount => row.tool_invocations,
                AllocationBasis.ResponseSize => row.tool_response_bytes,
                _ => row.weighted_work_score
            };
        }

        var rows = modelRows
            .Select(row => new AllocationRow
            {
                model = row.name,
                basis_value = GetBasisValue(row)
            })
            .ToList();

        var total = rows.Sum(x => x.basis_value);
        foreach (var row in rows)
        {
            row.share = total <= 0 ? 0 : row.basis_value / total;
            row.estimated_limit_delta = options.LimitDelta.Value * row.share;
        }

        return new AllocationSection
        {
            limit_before = options.LimitBefore,
            limit_after = options.LimitAfter,
            limit_delta = options.LimitDelta.Value,
            limit_unit = options.LimitUnit,
            allocation_basis = GetAllocationBasisName(options.AllocationBasis),
            rows = rows
        };
    }

    private static string GetAllocationBasisName(AllocationBasis value)
    {
        return value switch
        {
            AllocationBasis.ToolElapsed => "tool-elapsed",
            AllocationBasis.ToolCount => "tool-count",
            AllocationBasis.ResponseSize => "response-size",
            _ => value.ToString().ToLowerInvariant()
        };
    }

    private static List<CorrelationIssueRow> BuildCorrelationIssues(
        IReadOnlyList<ToolInvocation> toolInvocations,
        IReadOnlyList<SubagentRun> subagentRuns)
    {
        var rows = new List<CorrelationIssueRow>();
        AddIssue(rows, "tool", "missing_pre", toolInvocations.Count(x => x.CorrelationStatus == "missing_pre"));
        AddIssue(rows, "tool", "missing_post", toolInvocations.Count(x => x.CorrelationStatus == "missing_post"));
        AddIssue(rows, "tool", "duplicate", toolInvocations.Count(x => x.CorrelationStatus == "duplicate"));
        AddIssue(rows, "tool", "legacy_unavailable", toolInvocations.Count(x => x.CorrelationStatus == "legacy_unavailable"));
        AddIssue(rows, "subagent", "missing_start", subagentRuns.Count(x => x.CorrelationStatus == "missing_start"));
        AddIssue(rows, "subagent", "missing_stop", subagentRuns.Count(x => x.CorrelationStatus == "missing_stop"));
        AddIssue(rows, "subagent", "duplicate", subagentRuns.Count(x => x.CorrelationStatus == "duplicate"));
        return rows;
    }

    private static void AddIssue(List<CorrelationIssueRow> rows, string area, string status, int count)
    {
        if (count <= 0)
        {
            return;
        }

        rows.Add(new CorrelationIssueRow
        {
            area = area,
            status = status,
            count = count
        });
    }

    private static IReadOnlyList<SubagentRun> BuildSubagentRuns(
        IReadOnlyList<UsageRecord> records,
        List<string> warnings)
    {
        var pending = new Dictionary<string, UsageRecord>(StringComparer.OrdinalIgnoreCase);
        var runs = new List<SubagentRun>();

        foreach (var record in records)
        {
            if (record.Event.Equals("SubagentStart", StringComparison.OrdinalIgnoreCase))
            {
                var key = GetSubagentCorrelationKey(record);
                if (key is null)
                {
                    runs.Add(SubagentRun.FromStart(record, "unknown"));
                    continue;
                }

                if (pending.ContainsKey(key))
                {
                    runs.Add(SubagentRun.FromStart(record, "duplicate"));
                    continue;
                }

                pending[key] = record;
            }
            else if (record.Event.Equals("SubagentStop", StringComparison.OrdinalIgnoreCase))
            {
                var key = GetSubagentCorrelationKey(record);
                if (key is null)
                {
                    runs.Add(SubagentRun.FromStop(record, "missing_start"));
                    continue;
                }

                if (pending.TryGetValue(key, out var started))
                {
                    pending.Remove(key);
                    runs.Add(SubagentRun.FromMatched(started, record));
                }
                else if (record.SubagentCorrelationStatus == "missing_start")
                {
                    runs.Add(SubagentRun.FromStop(record, "missing_start"));
                }
                else
                {
                    runs.Add(SubagentRun.FromStop(record, "missing_start"));
                }
            }
        }

        foreach (var item in pending.Values)
        {
            runs.Add(SubagentRun.FromStart(item, "missing_stop"));
        }

        if (runs.Any(x => x.CorrelationStatus == "unknown"))
        {
            warnings.Add("一部の subagent event に相関キーが不足しているため、duration を復元できない。");
        }

        return runs.OrderBy(x => x.RecordedAt).ToList();
    }

    private static IReadOnlyList<ToolInvocation> BuildToolInvocations(
        IReadOnlyList<UsageRecord> records,
        List<string> warnings)
    {
        var pending = new Dictionary<string, UsageRecord>(StringComparer.OrdinalIgnoreCase);
        var invocations = new List<ToolInvocation>();

        foreach (var record in records)
        {
            if (record.Event.Equals("PreToolUse", StringComparison.OrdinalIgnoreCase))
            {
                var key = GetToolCorrelationKey(record);
                if (key is null)
                {
                    invocations.Add(ToolInvocation.FromSingle(record, "unknown"));
                    continue;
                }

                if (pending.ContainsKey(key))
                {
                    invocations.Add(ToolInvocation.FromSingle(record, "duplicate"));
                    continue;
                }

                pending[key] = record;
            }
            else if (record.Event.Equals("PostToolUse", StringComparison.OrdinalIgnoreCase))
            {
                var key = GetToolCorrelationKey(record);
                if (key is null)
                {
                    invocations.Add(ToolInvocation.FromSingle(record, record.IsV2 ? "unknown" : "legacy_unavailable"));
                    continue;
                }

                if (pending.TryGetValue(key, out var started))
                {
                    pending.Remove(key);
                    invocations.Add(ToolInvocation.FromMatched(started, record));
                }
                else if (record.ToolCorrelationStatus == "missing_pre")
                {
                    invocations.Add(ToolInvocation.FromSingle(record, "missing_pre"));
                }
                else
                {
                    invocations.Add(ToolInvocation.FromSingle(record, record.IsV2 ? "missing_pre" : "legacy_unavailable"));
                }
            }
        }

        foreach (var item in pending.Values)
        {
            invocations.Add(ToolInvocation.FromSingle(item, "missing_post"));
        }

        if (invocations.Any(x => x.CorrelationStatus == "legacy_unavailable"))
        {
            warnings.Add("旧 agent-usage ログ行の PostToolUse は PreToolUse 相関なしで集計した。");
        }

        return invocations.OrderBy(x => x.RecordedAt).ToList();
    }

    private static string? GetToolCorrelationKey(UsageRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.ToolUseId))
        {
            return null;
        }

        return string.Join("|", record.SessionId ?? "-", record.TurnId ?? "-", record.AgentId ?? "parent", record.ToolUseId);
    }

    private static string? GetSubagentCorrelationKey(UsageRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.SessionId) || string.IsNullOrWhiteSpace(record.AgentId))
        {
            return null;
        }

        return string.Join("|", record.SessionId, record.AgentId);
    }

    private static void AddRecord(WorkAccumulator accumulator, UsageRecord record)
    {
        accumulator.Events++;
        accumulator.EventCounts.Increment(record.Event);
    }

    private static void AddInvocation(WorkAccumulator accumulator, ToolInvocation invocation, Dictionary<string, int>? summaryCommandKinds)
    {
        accumulator.ToolInvocations++;
        if (invocation.CorrelationStatus == "matched")
        {
            accumulator.MatchedToolInvocations++;
        }

        if (invocation.CorrelationStatus is "missing_pre" or "missing_post" or "duplicate")
        {
            accumulator.MissingCorrelationCount++;
        }

        accumulator.TotalToolElapsedMs += invocation.ToolElapsedMs ?? 0;
        accumulator.ToolInputBytes += invocation.ToolInputBytes ?? 0;
        accumulator.ToolResponseBytes += invocation.ToolResponseBytes ?? 0;
        accumulator.ToolNameCounts.Increment(invocation.ToolKey);

        if (!string.IsNullOrWhiteSpace(invocation.CommandKind))
        {
            accumulator.CommandKindCounts.Increment(invocation.CommandKind);
            summaryCommandKinds?.Increment(invocation.CommandKind);
        }

        if (invocation.ToolKey.Equals("Bash", StringComparison.OrdinalIgnoreCase) ||
            invocation.ToolKey.Equals("shell_command", StringComparison.OrdinalIgnoreCase))
        {
            accumulator.BashCount++;
        }

        if (invocation.ToolKey.Equals("apply_patch", StringComparison.OrdinalIgnoreCase))
        {
            accumulator.ApplyPatchCount++;
        }
    }

    private static void AddSubagentRun(WorkAccumulator accumulator, SubagentRun run)
    {
        accumulator.SubagentRuns++;
        if (run.CorrelationStatus is "missing_start" or "missing_stop" or "duplicate")
        {
            accumulator.MissingCorrelationCount++;
        }

        accumulator.TotalSubagentDurationMs += run.DurationMs ?? 0;
    }

    private static List<KeyValuePair<string, WorkAccumulator>> BuildRows(Dictionary<string, WorkAccumulator> source)
    {
        foreach (var entry in source.Values)
        {
            entry.WeightedWorkScore = CalculateWeightedScore(entry);
        }

        return source.ToList();
    }

    private static double CalculateWeightedScore(WorkAccumulator value)
    {
        return value.TotalToolElapsedMs
            + value.TotalSubagentDurationMs
            + (value.ToolInvocations * 750d)
            + (value.ApplyPatchCount * 1_500d)
            + (value.BashCount * 500d)
            + (value.ToolInputBytes / 10d)
            + (value.ToolResponseBytes / 20d);
    }

    private static WorkRow CreateWorkRow(string name, WorkAccumulator value)
    {
        return new WorkRow
        {
            name = name,
            events = value.Events,
            tool_invocations = value.ToolInvocations,
            matched_tool_invocations = value.MatchedToolInvocations,
            missing_correlation = value.MissingCorrelationCount,
            total_tool_elapsed_ms = value.TotalToolElapsedMs,
            total_subagent_duration_ms = value.TotalSubagentDurationMs,
            bash_count = value.BashCount,
            apply_patch_count = value.ApplyPatchCount,
            tool_input_bytes = value.ToolInputBytes,
            tool_response_bytes = value.ToolResponseBytes,
            weighted_work_score = Math.Round(value.WeightedWorkScore, 3),
            weighted_work_share = 0,
            command_kind_counts = new Dictionary<string, int>(value.CommandKindCounts, StringComparer.OrdinalIgnoreCase),
            event_counts = new Dictionary<string, int>(value.EventCounts, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static void NormalizeShares(List<WorkRow> rows)
    {
        var total = rows.Sum(x => x.weighted_work_score);
        foreach (var row in rows)
        {
            row.weighted_work_share = total <= 0 ? 0 : row.weighted_work_score / total;
        }
    }

    private static IEnumerable<(UsageRecord? record, ParseError? parseError)> ReadLogFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Log file does not exist", path);
        }

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

            yield return ParseLine(line, path, lineNumber);
        }
    }

    private static (UsageRecord? record, ParseError? parseError) ParseLine(string line, string path, int lineNumber)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return (ParseRecord(document.RootElement), null);
        }
        catch (JsonException ex)
        {
            return (null, new ParseError
            {
                source_path = path,
                line = lineNumber,
                message = ex.Message,
                exception = ex.ToString()
            });
        }
    }

    private static UsageRecord? ParseRecord(JsonElement element)
    {
        var recordedAt = GetDateTimeOffset(element, "recorded_at");
        if (!recordedAt.HasValue)
        {
            return null;
        }

        var schemaVersion = GetString(element, "schema_version");
        var isV2 = string.Equals(schemaVersion, "2.0", StringComparison.OrdinalIgnoreCase);
        var durationMs = GetInt64(element, "subagent_duration_ms") ?? GetInt64(element, "duration_ms");
        return new UsageRecord
        {
            SchemaVersion = schemaVersion,
            IsV2 = isV2,
            RecordedAt = recordedAt.Value,
            Event = GetString(element, "event") ?? "Unknown",
            SessionId = GetString(element, "session_id"),
            TurnId = GetString(element, "turn_id"),
            AgentId = NormalizeAgentId(GetString(element, "agent_id")),
            AgentType = GetString(element, "agent_type"),
            Model = GetString(element, "model"),
            Cwd = GetString(element, "cwd"),
            RepoRoot = GetString(element, "repo_root"),
            RepoName = GetString(element, "repo_name"),
            GitBranch = GetString(element, "git_branch"),
            GitCommit = GetString(element, "git_commit"),
            PermissionMode = GetString(element, "permission_mode"),
            TranscriptPath = GetString(element, "transcript_path"),
            ToolUseId = GetString(element, "tool_use_id"),
            ToolName = GetString(element, "tool_name"),
            ToolCategory = GetString(element, "tool_category"),
            Command = GetString(element, "command_redacted") ?? GetString(element, "command"),
            CommandKind = GetString(element, "command_kind"),
            CommandRisk = GetString(element, "command_risk"),
            ToolResultClass = GetString(element, "tool_result_class"),
            ToolElapsedMs = GetInt64(element, "tool_elapsed_ms"),
            ToolCorrelationStatus = GetString(element, "tool_correlation_status"),
            ToolInputSize = GetInt64(element, "tool_input_size"),
            ToolResponseSize = GetInt64(element, "tool_response_size"),
            SubagentRunId = GetString(element, "subagent_run_id"),
            SubagentDurationMs = durationMs,
            SubagentCorrelationStatus = GetString(element, "subagent_correlation_status"),
            StartedAt = GetDateTimeOffset(element, "subagent_started_at"),
            StoppedAt = GetDateTimeOffset(element, "subagent_stopped_at")
        };
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
        if (!string.IsNullOrWhiteSpace(options.SessionIdFilter) &&
            !string.Equals(record.SessionId, options.SessionIdFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(options.CwdContains) &&
            !(record.Cwd ?? string.Empty).Contains(options.CwdContains, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static DateTimeOffset FloorToBucket(DateTimeOffset value, TimeSpan bucket)
    {
        var bucketTicks = bucket.Ticks;
        var flooredTicks = (value.Ticks / bucketTicks) * bucketTicks;
        return new DateTimeOffset(flooredTicks, value.Offset);
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
            if (IanaTimezoneAlias.TryGetValue(value, out var alias))
            {
                return TimeZoneInfo.FindSystemTimeZoneById(alias);
            }

            throw;
        }
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

    private static long? GetInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.GetInt64(),
            JsonValueKind.String when long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
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

    private static void Increment(Dictionary<string, int> source, string key)
    {
        if (source.TryGetValue(key, out var count))
        {
            source[key] = count + 1;
        }
        else
        {
            source[key] = 1;
        }
    }
}

internal sealed class UsageReportOutput
{
    public SummarySection summary { get; set; } = new();
    public List<WorkRow> model_rows { get; set; } = [];
    public List<WorkRow> agent_type_rows { get; set; } = [];
    public List<WorkRow> tool_rows { get; set; } = [];
    public List<WorkRow> repository_rows { get; set; } = [];
    public List<WorkRow> timeline_rows { get; set; } = [];
    public List<CorrelationIssueRow> correlation_issues { get; set; } = [];
    public AllocationSection? allocation { get; set; }
    public List<string> warnings { get; set; } = [];
    public List<string> not_available { get; set; } = [];
    public List<ParseError> parse_errors { get; set; } = [];

    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();

            writer.WritePropertyName("summary");
            WriteSummary(writer, summary);

            writer.WritePropertyName("model_rows");
            WriteWorkRows(writer, model_rows);

            writer.WritePropertyName("agent_type_rows");
            WriteWorkRows(writer, agent_type_rows);

            writer.WritePropertyName("tool_rows");
            WriteWorkRows(writer, tool_rows);

            writer.WritePropertyName("repository_rows");
            WriteWorkRows(writer, repository_rows);

            writer.WritePropertyName("timeline_rows");
            WriteWorkRows(writer, timeline_rows);

            writer.WritePropertyName("correlation_issues");
            writer.WriteStartArray();
            foreach (var item in correlation_issues)
            {
                writer.WriteStartObject();
                writer.WriteString("area", item.area);
                writer.WriteString("status", item.status);
                writer.WriteNumber("count", item.count);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WritePropertyName("allocation");
            if (allocation is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                WriteAllocation(writer, allocation);
            }

            writer.WritePropertyName("warnings");
            WriteStringArray(writer, warnings);

            writer.WritePropertyName("not_available");
            WriteStringArray(writer, not_available);

            writer.WritePropertyName("parse_errors");
            writer.WriteStartArray();
            foreach (var item in parse_errors)
            {
                writer.WriteStartObject();
                writer.WriteString("source_path", item.source_path);
                writer.WriteNumber("line", item.line);
                writer.WriteString("message", item.message);
                writer.WriteString("exception", item.exception);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Codex Observation Report");
        sb.AppendLine();
        sb.AppendLine($"Generated: {summary.generated_at}");
        sb.AppendLine($"Period: {summary.period_from ?? "-"} - {summary.period_to ?? "-"}");
        sb.AppendLine($"Timezone: {summary.timezone}");
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|---|---:|");
        sb.AppendLine($"| Parsed records | {summary.parsed_records} |");
        sb.AppendLine($"| Parse errors | {summary.parse_errors} |");
        sb.AppendLine($"| Sessions | {summary.sessions} |");
        sb.AppendLine($"| Turns | {summary.turns} |");
        sb.AppendLine($"| Repositories | {summary.repositories} |");
        sb.AppendLine($"| Models | {summary.models} |");
        sb.AppendLine($"| Tool invocations | {summary.tool_invocations} |");
        sb.AppendLine($"| Matched tool invocations | {summary.matched_tool_invocations} |");
        sb.AppendLine($"| missing_pre | {summary.missing_pre} |");
        sb.AppendLine($"| missing_post | {summary.missing_post} |");
        sb.AppendLine($"| missing_start | {summary.missing_start} |");
        sb.AppendLine($"| missing_stop | {summary.missing_stop} |");
        sb.AppendLine($"| Weighted score total | {summary.weighted_score_total:F1} |");
        if (summary.limit_delta.HasValue)
        {
            sb.AppendLine($"| Limit delta | {summary.limit_delta.Value.ToString("F3", CultureInfo.InvariantCulture)} {summary.limit_unit} |");
        }

        sb.AppendLine();
        sb.AppendLine("## By model");
        AppendWorkTable(sb, model_rows);
        sb.AppendLine();
        sb.AppendLine("## By agent type");
        AppendWorkTable(sb, agent_type_rows);
        sb.AppendLine();
        sb.AppendLine("## By tool");
        AppendWorkTable(sb, tool_rows);
        sb.AppendLine();
        sb.AppendLine("## By repository");
        AppendWorkTable(sb, repository_rows);
        sb.AppendLine();
        sb.AppendLine("## Timeline");
        AppendWorkTable(sb, timeline_rows);

        if (correlation_issues.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Correlation issues");
            sb.AppendLine();
            sb.AppendLine("| Area | Status | Count |");
            sb.AppendLine("|---|---|---:|");
            foreach (var item in correlation_issues)
            {
                sb.AppendLine($"| {item.area} | {item.status} | {item.count} |");
            }
        }

        if (allocation is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## Limit delta allocation");
            sb.AppendLine();
            sb.AppendLine($"Basis: `{allocation.allocation_basis}`");
            sb.AppendLine();
            sb.AppendLine("| Model | Basis value | Share | Estimated delta |");
            sb.AppendLine("|---|---:|---:|---:|");
            foreach (var row in allocation.rows.OrderByDescending(x => x.estimated_limit_delta))
            {
                sb.AppendLine(
                    $"| {row.model} | {row.basis_value.ToString("F3", CultureInfo.InvariantCulture)} | {(row.share * 100).ToString("F1", CultureInfo.InvariantCulture)}% | {row.estimated_limit_delta.ToString("F3", CultureInfo.InvariantCulture)} |");
            }
        }

        if (warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Warnings");
            sb.AppendLine();
            foreach (var item in warnings)
            {
                sb.AppendLine($"- {item}");
            }
        }

        if (not_available.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Not available");
            sb.AppendLine();
            foreach (var item in not_available)
            {
                sb.AppendLine($"- {item}");
            }
        }

        if (parse_errors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Parse errors");
            sb.AppendLine();
            sb.AppendLine("| Source | Line | Message |");
            sb.AppendLine("|---|---:|---|");
            foreach (var item in parse_errors.Take(20))
            {
                sb.AppendLine($"| {item.source_path} | {item.line} | {item.message} |");
            }
        }

        return sb.ToString();
    }

    private static void AppendWorkTable(StringBuilder sb, List<WorkRow> rows)
    {
        sb.AppendLine();
        sb.AppendLine("| Name | Events | Tool invocations | Matched | Missing corr | Tool elapsed (s) | Subagent duration (s) | Score | Share | Cmd kinds |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var row in rows)
        {
            sb.AppendLine(
                $"| {row.name} | {row.events} | {row.tool_invocations} | {row.matched_tool_invocations} | {row.missing_correlation} | {(row.total_tool_elapsed_ms / 1000d).ToString("F1", CultureInfo.InvariantCulture)} | {(row.total_subagent_duration_ms / 1000d).ToString("F1", CultureInfo.InvariantCulture)} | {row.weighted_work_score.ToString("F1", CultureInfo.InvariantCulture)} | {(row.weighted_work_share * 100).ToString("F1", CultureInfo.InvariantCulture)}% | {FormatCommandKinds(row.command_kind_counts)} |");
        }
    }

    private static string FormatCommandKinds(Dictionary<string, int> value)
    {
        if (value.Count == 0)
        {
            return "-";
        }

        return string.Join(", ", value.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}:{x.Value}"));
    }

    private static void WriteSummary(Utf8JsonWriter writer, SummarySection value)
    {
        writer.WriteStartObject();
        writer.WriteString("generated_at", value.generated_at);
        WriteNullableString(writer, "period_from", value.period_from);
        WriteNullableString(writer, "period_to", value.period_to);
        writer.WriteString("timezone", value.timezone);
        writer.WriteNumber("parsed_records", value.parsed_records);
        writer.WriteNumber("parse_errors", value.parse_errors);
        writer.WriteNumber("sessions", value.sessions);
        writer.WriteNumber("turns", value.turns);
        writer.WriteNumber("repositories", value.repositories);
        writer.WriteNumber("models", value.models);
        writer.WriteNumber("tool_invocations", value.tool_invocations);
        writer.WriteNumber("matched_tool_invocations", value.matched_tool_invocations);
        writer.WriteNumber("missing_pre", value.missing_pre);
        writer.WriteNumber("missing_post", value.missing_post);
        writer.WriteNumber("duplicate_tool", value.duplicate_tool);
        writer.WriteNumber("subagent_runs", value.subagent_runs);
        writer.WriteNumber("missing_start", value.missing_start);
        writer.WriteNumber("missing_stop", value.missing_stop);
        writer.WriteNumber("weighted_score_total", value.weighted_score_total);
        writer.WritePropertyName("command_kind_counts");
        WriteIntDictionary(writer, value.command_kind_counts);
        writer.WritePropertyName("event_counts");
        WriteIntDictionary(writer, value.event_counts);
        WriteNullableDouble(writer, "limit_delta", value.limit_delta);
        WriteNullableString(writer, "limit_unit", value.limit_unit);
        writer.WriteEndObject();
    }

    private static void WriteWorkRows(Utf8JsonWriter writer, List<WorkRow> rows)
    {
        writer.WriteStartArray();
        foreach (var row in rows)
        {
            writer.WriteStartObject();
            writer.WriteString("name", row.name);
            writer.WriteNumber("events", row.events);
            writer.WriteNumber("tool_invocations", row.tool_invocations);
            writer.WriteNumber("matched_tool_invocations", row.matched_tool_invocations);
            writer.WriteNumber("missing_correlation", row.missing_correlation);
            writer.WriteNumber("total_tool_elapsed_ms", row.total_tool_elapsed_ms);
            writer.WriteNumber("total_subagent_duration_ms", row.total_subagent_duration_ms);
            writer.WriteNumber("bash_count", row.bash_count);
            writer.WriteNumber("apply_patch_count", row.apply_patch_count);
            writer.WriteNumber("tool_input_bytes", row.tool_input_bytes);
            writer.WriteNumber("tool_response_bytes", row.tool_response_bytes);
            writer.WriteNumber("weighted_work_score", row.weighted_work_score);
            writer.WriteNumber("weighted_work_share", row.weighted_work_share);
            writer.WritePropertyName("command_kind_counts");
            WriteIntDictionary(writer, row.command_kind_counts);
            writer.WritePropertyName("event_counts");
            WriteIntDictionary(writer, row.event_counts);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteAllocation(Utf8JsonWriter writer, AllocationSection value)
    {
        writer.WriteStartObject();
        WriteNullableDouble(writer, "limit_before", value.limit_before);
        WriteNullableDouble(writer, "limit_after", value.limit_after);
        writer.WriteNumber("limit_delta", value.limit_delta);
        writer.WriteString("limit_unit", value.limit_unit);
        writer.WriteString("allocation_basis", value.allocation_basis);
        writer.WritePropertyName("rows");
        writer.WriteStartArray();
        foreach (var row in value.rows)
        {
            writer.WriteStartObject();
            writer.WriteString("model", row.model);
            writer.WriteNumber("basis_value", row.basis_value);
            writer.WriteNumber("share", row.share);
            writer.WriteNumber("estimated_limit_delta", row.estimated_limit_delta);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteStringArray(Utf8JsonWriter writer, List<string> values)
    {
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WriteIntDictionary(Utf8JsonWriter writer, Dictionary<string, int> values)
    {
        writer.WriteStartObject();
        foreach (var item in values.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            writer.WriteNumber(item.Key, item.Value);
        }

        writer.WriteEndObject();
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

    private static void WriteNullableDouble(Utf8JsonWriter writer, string propertyName, double? value)
    {
        if (!value.HasValue)
        {
            writer.WriteNull(propertyName);
            return;
        }

        writer.WriteNumber(propertyName, value.Value);
    }
}

internal sealed class SummarySection
{
    public string generated_at { get; set; } = string.Empty;
    public string? period_from { get; set; }
    public string? period_to { get; set; }
    public string timezone { get; set; } = "local";
    public int parsed_records { get; set; }
    public int parse_errors { get; set; }
    public int sessions { get; set; }
    public int turns { get; set; }
    public int repositories { get; set; }
    public int models { get; set; }
    public int tool_invocations { get; set; }
    public int matched_tool_invocations { get; set; }
    public int missing_pre { get; set; }
    public int missing_post { get; set; }
    public int duplicate_tool { get; set; }
    public int subagent_runs { get; set; }
    public int missing_start { get; set; }
    public int missing_stop { get; set; }
    public double weighted_score_total { get; set; }
    public Dictionary<string, int> command_kind_counts { get; set; } = [];
    public Dictionary<string, int> event_counts { get; set; } = [];
    public double? limit_delta { get; set; }
    public string? limit_unit { get; set; }
}

internal sealed class WorkRow
{
    public string name { get; set; } = string.Empty;
    public int events { get; set; }
    public int tool_invocations { get; set; }
    public int matched_tool_invocations { get; set; }
    public int missing_correlation { get; set; }
    public long total_tool_elapsed_ms { get; set; }
    public long total_subagent_duration_ms { get; set; }
    public int bash_count { get; set; }
    public int apply_patch_count { get; set; }
    public long tool_input_bytes { get; set; }
    public long tool_response_bytes { get; set; }
    public double weighted_work_score { get; set; }
    public double weighted_work_share { get; set; }
    public Dictionary<string, int> command_kind_counts { get; set; } = [];
    public Dictionary<string, int> event_counts { get; set; } = [];
}

internal sealed class CorrelationIssueRow
{
    public string area { get; set; } = string.Empty;
    public string status { get; set; } = string.Empty;
    public int count { get; set; }
}

internal sealed class AllocationSection
{
    public double? limit_before { get; set; }
    public double? limit_after { get; set; }
    public double limit_delta { get; set; }
    public string limit_unit { get; set; } = "unknown";
    public string allocation_basis { get; set; } = "weighted";
    public List<AllocationRow> rows { get; set; } = [];
}

internal sealed class AllocationRow
{
    public string model { get; set; } = string.Empty;
    public double basis_value { get; set; }
    public double share { get; set; }
    public double estimated_limit_delta { get; set; }
}

internal sealed class ParseError
{
    public string source_path { get; set; } = string.Empty;
    public int line { get; set; }
    public string message { get; set; } = string.Empty;
    public string exception { get; set; } = string.Empty;
}

internal sealed class UsageRecord
{
    public string? SchemaVersion { get; init; }
    public bool IsV2 { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
    public string Event { get; init; } = "Unknown";
    public string? SessionId { get; init; }
    public string? TurnId { get; init; }
    public string? AgentId { get; init; }
    public string? AgentType { get; init; }
    public string? Model { get; init; }
    public string? Cwd { get; init; }
    public string? RepoRoot { get; init; }
    public string? RepoName { get; init; }
    public string? GitBranch { get; init; }
    public string? GitCommit { get; init; }
    public string? PermissionMode { get; init; }
    public string? TranscriptPath { get; init; }
    public string? ToolUseId { get; init; }
    public string? ToolName { get; init; }
    public string? ToolCategory { get; init; }
    public string? Command { get; init; }
    public string? CommandKind { get; init; }
    public string? CommandRisk { get; init; }
    public string? ToolResultClass { get; init; }
    public long? ToolElapsedMs { get; init; }
    public string? ToolCorrelationStatus { get; init; }
    public long? ToolInputSize { get; init; }
    public long? ToolResponseSize { get; init; }
    public string? SubagentRunId { get; init; }
    public long? SubagentDurationMs { get; init; }
    public string? SubagentCorrelationStatus { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? StoppedAt { get; init; }

    public string ModelKey => string.IsNullOrWhiteSpace(Model) ? "(unknown)" : Model!;
    public string AgentTypeKey => string.IsNullOrWhiteSpace(AgentType) ? "parent/unassigned" : AgentType!;
    public string ToolKey => string.IsNullOrWhiteSpace(ToolName) ? "(none)" : ToolName!;
    public string RepositoryKey => !string.IsNullOrWhiteSpace(RepoName)
        ? RepoName!
        : !string.IsNullOrWhiteSpace(RepoRoot)
            ? Path.GetFileName(RepoRoot!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : !string.IsNullOrWhiteSpace(Cwd)
                ? Path.GetFileName(Cwd!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : "(unknown)";
}

internal sealed class ToolInvocation
{
    public DateTimeOffset RecordedAt { get; init; }
    public string CorrelationStatus { get; init; } = "unknown";
    public string ModelKey { get; init; } = "(unknown)";
    public string AgentTypeKey { get; init; } = "parent/unassigned";
    public string ToolKey { get; init; } = "(none)";
    public string RepositoryKey { get; init; } = "(unknown)";
    public string? CommandKind { get; init; }
    public long? ToolElapsedMs { get; init; }
    public long? ToolInputBytes { get; init; }
    public long? ToolResponseBytes { get; init; }

    public static ToolInvocation FromMatched(UsageRecord pre, UsageRecord post)
    {
        return new ToolInvocation
        {
            RecordedAt = post.RecordedAt,
            CorrelationStatus = "matched",
            ModelKey = post.ModelKey,
            AgentTypeKey = post.AgentTypeKey,
            ToolKey = post.ToolKey,
            RepositoryKey = post.RepositoryKey,
            CommandKind = post.CommandKind ?? pre.CommandKind,
            ToolElapsedMs = post.ToolElapsedMs ?? Math.Max(0L, (long)Math.Round((post.RecordedAt - pre.RecordedAt).TotalMilliseconds)),
            ToolInputBytes = pre.ToolInputSize ?? post.ToolInputSize,
            ToolResponseBytes = post.ToolResponseSize
        };
    }

    public static ToolInvocation FromSingle(UsageRecord record, string status)
    {
        return new ToolInvocation
        {
            RecordedAt = record.RecordedAt,
            CorrelationStatus = status,
            ModelKey = record.ModelKey,
            AgentTypeKey = record.AgentTypeKey,
            ToolKey = record.ToolKey,
            RepositoryKey = record.RepositoryKey,
            CommandKind = record.CommandKind,
            ToolElapsedMs = record.ToolElapsedMs,
            ToolInputBytes = record.ToolInputSize,
            ToolResponseBytes = record.ToolResponseSize
        };
    }
}

internal sealed class SubagentRun
{
    public DateTimeOffset RecordedAt { get; init; }
    public string CorrelationStatus { get; init; } = "unknown";
    public string ModelKey { get; init; } = "(unknown)";
    public string AgentTypeKey { get; init; } = "parent/unassigned";
    public string RepositoryKey { get; init; } = "(unknown)";
    public long? DurationMs { get; init; }

    public static SubagentRun FromMatched(UsageRecord start, UsageRecord stop)
    {
        return new SubagentRun
        {
            RecordedAt = stop.RecordedAt,
            CorrelationStatus = "matched",
            ModelKey = stop.ModelKey,
            AgentTypeKey = stop.AgentTypeKey,
            RepositoryKey = stop.RepositoryKey,
            DurationMs = stop.SubagentDurationMs ?? Math.Max(0L, (long)Math.Round((stop.RecordedAt - start.RecordedAt).TotalMilliseconds))
        };
    }

    public static SubagentRun FromStart(UsageRecord start, string status)
    {
        return new SubagentRun
        {
            RecordedAt = start.RecordedAt,
            CorrelationStatus = status,
            ModelKey = start.ModelKey,
            AgentTypeKey = start.AgentTypeKey,
            RepositoryKey = start.RepositoryKey,
            DurationMs = start.SubagentDurationMs
        };
    }

    public static SubagentRun FromStop(UsageRecord stop, string status)
    {
        return new SubagentRun
        {
            RecordedAt = stop.RecordedAt,
            CorrelationStatus = status,
            ModelKey = stop.ModelKey,
            AgentTypeKey = stop.AgentTypeKey,
            RepositoryKey = stop.RepositoryKey,
            DurationMs = stop.SubagentDurationMs
        };
    }
}

internal sealed class WorkAccumulator
{
    public int Events { get; set; }
    public int ToolInvocations { get; set; }
    public int MatchedToolInvocations { get; set; }
    public int MissingCorrelationCount { get; set; }
    public long TotalToolElapsedMs { get; set; }
    public long TotalSubagentDurationMs { get; set; }
    public int BashCount { get; set; }
    public int ApplyPatchCount { get; set; }
    public long ToolInputBytes { get; set; }
    public long ToolResponseBytes { get; set; }
    public double WeightedWorkScore { get; set; }
    public Dictionary<string, int> CommandKindCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> EventCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> ToolNameCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int SubagentRuns { get; set; }
}

internal static class Extensions
{
    public static TValue GetOrAdd<TKey, TValue>(this Dictionary<TKey, TValue> source, TKey key)
        where TKey : notnull
        where TValue : class, new()
    {
        if (!source.TryGetValue(key, out var value))
        {
            value = new TValue();
            source.Add(key, value);
        }

        return value;
    }

    public static void Increment(this Dictionary<string, int> source, string key)
    {
        if (source.TryGetValue(key, out var count))
        {
            source[key] = count + 1;
        }
        else
        {
            source[key] = 1;
        }
    }
}
