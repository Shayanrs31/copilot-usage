// Summarise GitHub Copilot Chat usage from your local VS Code chat history.
//
// Read-only. VS Code keeps each Copilot Chat session under
// <user data>/User/workspaceStorage/<id>/chatSessions/. Newer versions write an operation log
// (.jsonl: kind 0 = full state, 1 = set at path, 2 = append to array, 3 = delete); older versions
// write one JSON document (.json). This app rebuilds each session and reports prompt tokens,
// credits, models, what fills the prompt, instruction files, agents and tools.
//
// Run with the .NET 10 SDK (no project file needed):
//   dotnet run copilot_usage.cs                         the workspace for the current folder
//   dotnet run copilot_usage.cs -- --repo ~/src/my-app  the workspace for another folder
//   dotnet run copilot_usage.cs -- --list               every workspace that has chat history
//   dotnet run copilot_usage.cs -- --all                every workspace together
//   dotnet run copilot_usage.cs -- --since 2026-10-01 --until 2026-10-31
//
// Output: summary.txt, sessions.csv and requests.csv in --out (default: a folder in the temp dir).
// Message text is not written unless you pass --include-messages.
// This reads internal VS Code storage, not a documented API. It can change with any release.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var options = Options.Parse(args);
if (options is null)
{
    Console.WriteLine(Options.Help);
    return 0;
}

if (options.List)
{
    var found = false;
    foreach (var ws in Storage.Workspaces())
    {
        var count = Directory.EnumerateFiles(ws.ChatFolder).Count(Storage.IsSessionFile);
        if (count == 0) continue;
        found = true;
        Console.WriteLine($"{count,5} sessions  [{ws.Edition}]  {ws.Label}");
    }
    if (!found) Console.WriteLine("No Copilot Chat history found.");
    return 0;
}

List<string> folders;
if (options.ChatFolders.Count > 0)
    folders = options.ChatFolders;
else if (options.All)
    folders = Storage.Workspaces().Select(w => w.ChatFolder).ToList();
else
{
    var target = Storage.NormalisePath(options.Repo ?? ".");
    folders = Storage.Workspaces().Where(w => w.LocalPath == target).Select(w => w.ChatFolder).ToList();
}

if (folders.Count == 0)
{
    Console.Error.WriteLine("No chat history found for this folder. Run with --list to see the workspaces that have it.");
    return 1;
}

var analysis = new Analysis(options);
foreach (var folder in folders)
    foreach (var file in Directory.EnumerateFiles(folder).Where(Storage.IsSessionFile).OrderBy(f => f, StringComparer.Ordinal))
        analysis.AddSession(file, Sessions.Load(file));

if (analysis.Requests.Count == 0)
{
    Console.Error.WriteLine("No sessions in the selected date range.");
    return 1;
}

Directory.CreateDirectory(options.Out);
analysis.WriteCsv(Path.Combine(options.Out, "sessions.csv"), Path.Combine(options.Out, "requests.csv"));
var summary = analysis.Summary();
File.WriteAllText(Path.Combine(options.Out, "summary.txt"), summary + Environment.NewLine);
Console.WriteLine(summary);
Console.WriteLine();
Console.WriteLine($"Files written to {options.Out}");
return 0;

sealed class Options
{
    public string? Repo;
    public bool All;
    public bool List;
    public List<string> ChatFolders = [];
    public DateOnly? Since;
    public DateOnly? Until;
    public string Out = Path.Combine(Path.GetTempPath(), "copilot-usage");
    public bool IncludeMessages;

    public const string Help = """
        Usage: dotnet run copilot_usage.cs -- [options]

          --repo <folder>          workspace for this folder (default: current folder)
          --all                    every workspace together
          --list                   list workspaces that have chat history, then stop
          --chat-folder <folder>   a chatSessions folder to read (can repeat)
          --since <yyyy-mm-dd>     sessions created on or after this date
          --until <yyyy-mm-dd>     sessions created on or before this date
          --out <folder>           output folder (default: copilot-usage in the temp dir)
          --include-messages       add session titles and message text (private: keep it out of git)
        """;

    // Returns null when help was asked for.
    public static Options? Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--repo": o.Repo = Next(); break;
                case "--all": o.All = true; break;
                case "--list": o.List = true; break;
                case "--chat-folder": o.ChatFolders.Add(Next()); break;
                case "--since": o.Since = DateOnly.ParseExact(Next(), "yyyy-MM-dd", CultureInfo.InvariantCulture); break;
                case "--until": o.Until = DateOnly.ParseExact(Next(), "yyyy-MM-dd", CultureInfo.InvariantCulture); break;
                case "--out": o.Out = Next(); break;
                case "--include-messages": o.IncludeMessages = true; break;
                case "-h" or "--help": return null;
                default: throw new ArgumentException($"Unknown option {args[i]}. Use --help.");
            }
        }
        if (new[] { o.Repo is not null, o.All, o.List, o.ChatFolders.Count > 0 }.Count(x => x) > 1)
            throw new ArgumentException("Use only one of --repo, --all, --list, --chat-folder.");
        return o;
    }
}

record Workspace(string Edition, string Label, string? LocalPath, string ChatFolder);

static class Storage
{
    static readonly string[] Editions = ["Code", "Code - Insiders", "VSCodium", "Code - OSS"];

    public static bool IsSessionFile(string path) =>
        path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    public static string NormalisePath(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
    }

    // The User folder of each VS Code edition that exists on this machine.
    static IEnumerable<(string Edition, string User)> UserDataRoots()
    {
        string baseDir;
        if (OperatingSystem.IsWindows())
            baseDir = Environment.GetEnvironmentVariable("APPDATA") ?? "";
        else if (OperatingSystem.IsMacOS())
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
        else
            baseDir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                      ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

        foreach (var edition in Editions)
        {
            var user = Path.Combine(baseDir, edition, "User");
            if (Directory.Exists(user)) yield return (edition, user);
        }
    }

    public static IEnumerable<Workspace> Workspaces()
    {
        foreach (var (edition, user) in UserDataRoots())
        {
            var storage = Path.Combine(user, "workspaceStorage");
            if (Directory.Exists(storage))
            {
                foreach (var dir in Directory.EnumerateDirectories(storage).OrderBy(d => d, StringComparer.Ordinal))
                {
                    var chats = Path.Combine(dir, "chatSessions");
                    if (!Directory.Exists(chats)) continue;
                    var uri = ReadWorkspaceUri(Path.Combine(dir, "workspace.json"));
                    var label = uri is null ? Path.GetFileName(dir) : Uri.UnescapeDataString(uri);
                    yield return new Workspace(edition, label, uri is null ? null : UriToPath(uri), chats);
                }
            }
            var empty = Path.Combine(user, "globalStorage", "emptyWindowChatSessions");
            if (Directory.Exists(empty))
                yield return new Workspace(edition, "(windows with no folder open)", null, empty);
        }
    }

    static string? ReadWorkspaceUri(string metaPath)
    {
        if (!File.Exists(metaPath)) return null;
        try
        {
            var meta = JsonNode.Parse(File.ReadAllText(metaPath));
            return (string?)meta?["folder"] ?? (string?)meta?["workspace"] ?? (string?)meta?["configuration"];
        }
        catch (JsonException) { return null; }
    }

    // file:// URI from workspace.json -> local path. Null for remote workspaces.
    // VS Code escapes the drive colon ("file:///c%3A/..."), which Uri.LocalPath does not decode, so unescape first.
    static string? UriToPath(string uri) =>
        Uri.TryCreate(Uri.UnescapeDataString(uri), UriKind.Absolute, out var u) && u.IsFile ? NormalisePath(u.LocalPath) : null;
}

static class Sessions
{
    // Rebuild one session from an operation log (.jsonl) or a full document (.json).
    public static JsonObject Load(string path)
    {
        try
        {
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [];

            JsonNode? state = null;
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (JsonNode.Parse(line) is JsonObject op) state = Apply(state, op);
                }
                catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or IndexOutOfRangeException) { }
            }
            return state as JsonObject ?? [];
        }
        catch (IOException) { return []; }
    }

    static JsonNode? Apply(JsonNode? state, JsonObject op)
    {
        var kind = (int?)op["kind"];
        // Detach the value from the parsed line so it can be attached to the session tree.
        var value = op["v"];
        op.Remove("v");
        if (kind == 0) return value;
        if (state is null || op["k"] is not JsonArray path || path.Count == 0) return state;

        var parent = state;
        for (var i = 0; i < path.Count - 1; i++)
            parent = Step(parent, path[i]!) ?? throw new InvalidOperationException("path not found");
        var last = path[^1]!;

        switch (kind)
        {
            case 1:
                if (parent is JsonObject so) so[(string)last!] = value;
                else if (parent is JsonArray sa) sa[(int)last] = value;
                break;
            case 2:
                if (Step(parent, last) is not JsonArray target)
                {
                    target = [];
                    if (parent is JsonObject po) po[(string)last!] = target;
                }
                if (op["i"] is JsonValue cut && cut.TryGetValue<int>(out var from))
                    while (target.Count > from) target.RemoveAt(target.Count - 1);
                if (value is JsonArray items)
                {
                    var moved = items.ToList();
                    items.Clear();
                    foreach (var item in moved) target.Add(item);
                }
                else target.Add(value);
                break;
            case 3:
                if (parent is JsonObject d) d.Remove((string)last!);
                break;
        }
        return state;
    }

    static JsonNode? Step(JsonNode node, JsonNode key) => node switch
    {
        JsonObject o => o[(string)key!],
        JsonArray a => a[(int)key],
        _ => null,
    };
}

record RequestRow(string Session, DateOnly Created, int Index, string Model, string Mode, long PromptTokens,
    long CompletionTokens, double Credits, int ToolCalls, double? SysPct, double? ToolDefPct, string? Message);

record SessionRow(string Session, DateOnly Created, string Title, int Requests, long PromptTokens, long CompletionTokens, double Credits);

sealed class Analysis(Options options)
{
    public List<RequestRow> Requests { get; } = [];
    readonly List<SessionRow> sessions = [];
    readonly Dictionary<string, int> tools = [], models = [], attached = [];
    readonly Dictionary<string, List<double>> categoryPct = [];

    public void AddSession(string file, JsonObject s)
    {
        if (s["requests"] is not JsonArray reqs || reqs.Count == 0) return;
        var stamp = s["creationDate"] is JsonValue cd && cd.TryGetValue<double>(out var ms) ? ms : 0;
        var created = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds((long)stamp).UtcDateTime);
        if ((options.Since is { } since && created < since) || (options.Until is { } until && created > until)) return;

        var id = Path.GetFileNameWithoutExtension(file);
        id = id.Length > 8 ? id[..8] : id;
        long totalPrompt = 0, totalCompletion = 0;
        double totalCredits = 0;

        for (var idx = 0; idx < reqs.Count; idx++)
        {
            if (reqs[idx] is not JsonObject r) continue;

            var modeInfo = r["modeInfo"] as JsonObject;
            var mode = Str(modeInfo?["telemetryModeName"]) ?? Str(modeInfo?["kind"]) ?? "";
            if (modeInfo is not null && modeInfo["isBuiltin"] is JsonValue b && b.TryGetValue<bool>(out var builtin) && !builtin)
                mode = "custom:" + (Str(modeInfo["modeName"]) ?? Str(modeInfo["modeId"]) ?? "?");

            var details = new Dictionary<string, double>();
            if (r["promptTokenDetails"] is JsonArray detailList)
                foreach (var d in detailList.OfType<JsonObject>())
                    if (Str(d["label"]) is { } label)
                        details[label] = Num(d["percentageOfPrompt"]);
            foreach (var (label, pct) in details)
                (categoryPct.TryGetValue(label, out var l) ? l : categoryPct[label] = []).Add(pct);

            if (r["variableData"]?["variables"] is JsonArray vars)
                foreach (var name in vars.OfType<JsonObject>()
                             .Where(v => Str(v["kind"]) == "promptFile")
                             .Select(v => Str(v["name"]) ?? "").Distinct())
                    Bump(attached, name);

            var calls = 0;
            if (r["response"] is JsonArray parts)
                foreach (var p in parts.OfType<JsonObject>())
                    if (Str(p["kind"]) is "toolInvocationSerialized" or "toolInvocation")
                    {
                        calls++;
                        Bump(tools, Str(p["toolId"]) ?? "?");
                    }

            var model = Str(r["modelId"]) ?? "";
            Bump(models, model);
            var prompt = (long)Num(r["promptTokens"]);
            var completion = (long)Num(r["completionTokens"]);
            var credits = Num(r["copilotCredits"]);
            totalPrompt += prompt;
            totalCompletion += completion;
            totalCredits += credits;

            string? message = null;
            if (options.IncludeMessages)
            {
                var text = (Str(r["message"]?["text"]) ?? "").Trim().Replace("\n", " ");
                message = text.Length > 160 ? text[..160] : text;
            }

            Requests.Add(new RequestRow(id, created, idx, model, mode, prompt, completion, Math.Round(credits, 3), calls,
                details.TryGetValue("System Instructions", out var sys) ? sys : null,
                details.TryGetValue("Tool Definitions", out var td) ? td : null,
                message));
        }

        sessions.Add(new SessionRow(id, created, options.IncludeMessages ? Str(s["customTitle"]) ?? "" : "",
            reqs.Count, totalPrompt, totalCompletion, Math.Round(totalCredits, 2)));
    }

    public string Summary()
    {
        var sb = new StringBuilder();
        void Line(string text = "") => sb.AppendLine(text);
        var inv = CultureInfo.InvariantCulture;

        var totalCredits = Requests.Sum(r => r.Credits);
        if (totalCredits == 0) totalCredits = 1;
        var prompts = Requests.Where(r => r.PromptTokens > 0).Select(r => (double)r.PromptTokens).ToList();
        var credits = Requests.Where(r => r.Credits > 0).Select(r => r.Credits).ToList();
        var firsts = Requests.Where(r => r.Index == 0 && r.PromptTokens > 0 && r.SysPct is not null).ToList();

        Line($"date range: {sessions.Min(s => s.Created):yyyy-MM-dd} to {sessions.Max(s => s.Created):yyyy-MM-dd}");
        Line($"sessions: {sessions.Count}   requests: {Requests.Count}");
        Line(string.Format(inv, "prompt tokens: total {0:N0}   median per request {1:N0}", prompts.Sum(), Math.Floor(Median(prompts))));
        Line(string.Format(inv, "credits (as recorded by VS Code): total {0:N1}   median per request {1:F2}", credits.Sum(), Median(credits)));
        Line(string.Format(inv, "requests per session: median {0}   max {1}",
            Median(sessions.Select(s => (double)s.Requests).ToList()), sessions.Max(s => s.Requests)));
        if (firsts.Count > 0)
            Line(string.Format(inv, "first request of a session (recurring context): median prompt {0:N0} tokens, instructions ~{1:N0}, tool definitions ~{2:N0}",
                Math.Floor(Median(firsts.Select(r => (double)r.PromptTokens).ToList())),
                Math.Floor(Median(firsts.Select(r => r.PromptTokens * r.SysPct!.Value / 100).ToList())),
                Math.Floor(Median(firsts.Select(r => r.PromptTokens * (r.ToolDefPct ?? 0) / 100).ToList()))));

        Line();
        Line("share of credits by position in the session:");
        foreach (var (lo, hi, span) in new[] { (0, 4, "1-5"), (5, 19, "6-20"), (20, 99, "21-100"), (100, int.MaxValue, "after 100") })
        {
            var xs = Requests.Where(r => r.Index >= lo && r.Index <= hi).ToList();
            if (xs.Count == 0) continue;
            Line(string.Format(inv, "  requests {0,-10} {1,6} requests, median prompt {2,9:N0}, {3,5:F1}% of credits",
                span, xs.Count, Math.Floor(Median(xs.Select(x => (double)x.PromptTokens).ToList())),
                100 * xs.Sum(x => x.Credits) / totalCredits));
        }

        if (categoryPct.Count > 0)
        {
            Line();
            Line("average share of the prompt:");
            foreach (var (label, values) in categoryPct.OrderByDescending(c => c.Value.Average()))
                Line(string.Format(inv, "  {0}: {1:F0}%", label, values.Average()));
        }

        Line();
        Line("models (requests, median credits per request):");
        foreach (var (model, count) in models.OrderByDescending(m => m.Value))
            Line(string.Format(inv, "  {0}: {1}, {2:F2}", model == "" ? "?" : model, count,
                Median(Requests.Where(r => r.Model == model).Select(r => r.Credits).ToList())));

        Line();
        Line("modes and agents (requests):");
        foreach (var g in Requests.GroupBy(r => r.Mode).OrderByDescending(g => g.Count()))
            Line($"  {(g.Key == "" ? "(not recorded)" : g.Key)}: {g.Count()}");

        if (attached.Count > 0)
        {
            Line();
            Line("instruction and skill files attached (requests):");
            foreach (var (name, count) in attached.OrderByDescending(a => a.Value).Take(25)) Line($"  {name}: {count}");
        }
        if (tools.Count > 0)
        {
            Line();
            Line("top tools (calls):");
            foreach (var (name, count) in tools.OrderByDescending(t => t.Value).Take(20)) Line($"  {name}: {count}");
        }

        Line();
        Line("top 10 sessions by credits:");
        foreach (var s in sessions.OrderByDescending(s => s.Credits).Take(10))
            Line(string.Format(inv, "  {0:yyyy-MM-dd}  {1}  {2,9:N1} credits  {3,5} requests{4}", s.Created, s.Session, s.Credits,
                s.Requests, s.Title == "" ? "" : "  " + (s.Title.Length > 50 ? s.Title[..50] : s.Title)));

        return sb.ToString().TrimEnd();
    }

    public void WriteCsv(string sessionsPath, string requestsPath)
    {
        var inv = CultureInfo.InvariantCulture;
        var sessionLines = new List<string> { "session,created,title,requests,promptTokens,completionTokens,credits" };
        sessionLines.AddRange(sessions.Select(s => string.Join(",", Csv(s.Session), s.Created.ToString("yyyy-MM-dd", inv),
            Csv(s.Title), s.Requests, s.PromptTokens, s.CompletionTokens, s.Credits.ToString(inv))));
        File.WriteAllLines(sessionsPath, sessionLines);

        var header = "session,created,idx,model,mode,promptTokens,completionTokens,credits,toolCalls,sysPct,toolDefPct";
        if (options.IncludeMessages) header += ",message";
        var requestLines = new List<string> { header };
        requestLines.AddRange(Requests.Select(r =>
        {
            var row = string.Join(",", Csv(r.Session), r.Created.ToString("yyyy-MM-dd", inv), r.Index, Csv(r.Model), Csv(r.Mode),
                r.PromptTokens, r.CompletionTokens, r.Credits.ToString(inv), r.ToolCalls,
                r.SysPct?.ToString(inv) ?? "", r.ToolDefPct?.ToString(inv) ?? "");
            return options.IncludeMessages ? row + "," + Csv(r.Message ?? "") : row;
        }));
        File.WriteAllLines(requestsPath, requestLines);
    }

    static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    static void Bump(Dictionary<string, int> counts, string key) =>
        counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;

    static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static double Num(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

    static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(x => x).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
