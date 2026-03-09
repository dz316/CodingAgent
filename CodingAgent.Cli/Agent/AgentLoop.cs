using CodingAgent.Cli.Brain;
using CodingAgent.Cli.Models;
using CodingAgent.Cli.Tools;

namespace CodingAgent.Cli.Agent;

public sealed class AgentLoop
{
	private readonly OpenAiBrain _brain;
	private readonly WorkspaceTools _ws;
	private readonly SearchTool _search;
	private readonly ExecTool _exec;
	private readonly Policy _policy;

	public AgentLoop(OpenAiBrain brain, WorkspaceTools ws, SearchTool search, ExecTool exec)
	{
		_brain = brain;
		_ws = ws;
		_search = search;
		_exec = exec;
		_policy = new Policy();
	}

	public async Task RunAsync(string instruction, int maxSteps = 15)
	{
		string lastToolResult = "(none yet)";

		for (int stepNo = 1; stepNo <= maxSteps; stepNo++)
		{
			var listing = _ws.List(".");
			var batch = await _brain.DecideAsync(instruction, lastToolResult);

			// Print agent messages
			foreach (var m in batch.Messages)
				Console.WriteLine($"[STEP {stepNo}] [AGENT]: {m.Message}");

			// If any message says done=true and has no tool call, we can stop
			// (If model mixes done with calls, we still process calls and stop next iteration.)
			var calls = batch.Messages
				.Where(m => !m.Done && m.Call != null)
				.Select(m => m.Call!)
				.ToList();

			if (calls.Count == 0)
			{
				Console.WriteLine($"[STEP {stepNo}] Done.");
				return;
			}

			// Decide scheduling per your rules
			bool hasConflict = HasConflict(calls);
			hasConflict = false;

			List<string> results;
			if (hasConflict)
			{
				results = new List<string>(calls.Count);
				foreach (var c in calls)
					results.Add(await ExecuteOneAsync(c));
			}
			else
			{
				var tasks = calls.Select(ExecuteOneAsync).ToArray();
				results = (await Task.WhenAll(tasks)).ToList();
			}

			lastToolResult = CombineResults(results);
		}

		Console.WriteLine($"Stopped after maxSteps={maxSteps}. Increase steps or narrow the request.");
	}

	private bool HasConflict(List<ToolCall> calls)
	{
		// Rule A: if same tool used more than once ? sequential
		var toolGroups = calls.GroupBy(c => (c.Tool ?? "").Trim().ToLowerInvariant());
		if (toolGroups.Any(g => g.Count() > 1))
			return true;

		// Rule B: if same file path is used across tool calls ? sequential
		// "file being sent to more than one tool" includes Path/Src/Dst.
		var fileRefs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		foreach (var c in calls)
		{
			foreach (var p in GetFileRefs(c))
			{
				if (fileRefs.TryGetValue(p, out var count))
					fileRefs[p] = count + 1;
				else
					fileRefs[p] = 1;
			}
		}

		return fileRefs.Values.Any(v => v > 1);
	}

	private IEnumerable<string> GetFileRefs(ToolCall c)
	{
		// Normalize to forward slashes; treat empty/null as none
		static string? Norm(string? p)
		{
			if (string.IsNullOrWhiteSpace(p)) return null;
			return p.Replace('\\', '/').Trim();
		}

		// For exec/search, we do NOT treat WorkingDir/Path as “the same file” unless you want to.
		// Your spec says "same file being sent to more than one tool"; we interpret that as explicit file paths.
		var tool = (c.Tool ?? "").Trim().ToLowerInvariant();

		if (tool is "read" or "write" or "patch" or "delete" or "list")
		{
			var p = Norm(c.Path);
			if (p != null) yield return p;
		}
		else if (tool == "move" || tool == "copy")
		{
			var s = Norm(c.Src);
			var d = Norm(c.Dst);
			if (s != null) yield return s;
			if (d != null) yield return d;
		}
		// search/exec intentionally omitted from “file refs” to avoid false conflicts
	}

	private async Task<string> ExecuteOneAsync(ToolCall call)
	{
		var tool = (call.Tool ?? "").Trim().ToLowerInvariant();
		try
		{
			string output = tool switch
			{
				"list" => _ws.List(call.Path ?? "."),
				"read" => ExecRead(call),
				"write" => ExecWrite(call),
				"patch" => ExecPatch(call),
				"delete" => ExecDelete(call),
				"move" => ExecMove(call),
				"copy" => ExecCopy(call),
				"mkdir" => ExecMkdir(call),
				"search" => _search.Search(
					call.Query ?? throw new ArgumentException("search requires query"),
					call.Path ?? ".",
					call.Globs,
					call.MaxResults <= 0 ? 50 : call.MaxResults
				),
				"exec" => await _exec.ExecAsync(
					call.Command ?? throw new ArgumentException("exec requires command"),
					call.Args,
					call.WorkingDir ?? ".",
					call.TimeoutSec <= 0 ? 30 : call.TimeoutSec
				),
				_ => $"ERROR: Unknown tool '{call.Tool}'"
			};

			output = Truncate(output, 12_000);
			return $"OK\nCALL: {Summarize(call)}\nRESULT:\n{output}";
		}
		catch (Exception ex)
		{
			return $"ERROR\nCALL: {Summarize(call)}\n{ex.GetType().Name}: {ex.Message}";
		}
	}

	private string ExecRead(ToolCall call)
	{
		var path = call.Path ?? throw new ArgumentException("read requires path");
		var text = _ws.Read(path);
		_policy.MarkObserved(path); // read counts as observed
		return text;
	}

	private string ExecWrite(ToolCall call)
	{
		_ws.Write(
			call.Path ?? throw new ArgumentException("write requires path"),
			call.Content ?? "",
			call.Append
		);
		return "OK";
	}

	private string ExecPatch(ToolCall call)
	{
		var path = call.Path ?? throw new ArgumentException("patch requires path");
		_policy.RequireObservedBeforePatch(path);

		_ws.Patch(
			path,
			call.Content ?? throw new ArgumentException("patch requires content (PATCH v1)")
		);
		return "OK";
	}

	private string ExecDelete(ToolCall call)
	{
		_ws.Delete(call.Path ?? throw new ArgumentException("delete requires path"));
		return "OK";
	}

	private string ExecMove(ToolCall call)
	{
		_ws.Move(
			call.Src ?? throw new ArgumentException("move requires src"),
			call.Dst ?? throw new ArgumentException("move requires dst")
		);
		return "OK";
	}

	private string ExecCopy(ToolCall call)
	{
		_ws.Copy(
			call.Src ?? throw new ArgumentException("copy requires src"),
			call.Dst ?? throw new ArgumentException("copy requires dst")
		);
		return "OK";
	}

	private string ExecMkdir(ToolCall call)
	{
		_ws.Mkdir(
			call.Path ?? throw new ArgumentException("mkdir requires path")
		);
		return "OK";
	}

	private static string CombineResults(List<string> results)
	{
		// “Combine all responses into a single response”
		// Keep it deterministic and readable.
		return string.Join("\n\n----\n\n", results);
	}

	private static string Truncate(string s, int maxChars)
		=> s.Length <= maxChars ? s : s[..maxChars] + "\n...<truncated>...";

	private static string Summarize(ToolCall c)
	{
		var parts = new List<string> { $"tool={c.Tool}" };
		if (!string.IsNullOrWhiteSpace(c.Path)) parts.Add($"path={c.Path}");
		if (!string.IsNullOrWhiteSpace(c.Src)) parts.Add($"src={c.Src}");
		if (!string.IsNullOrWhiteSpace(c.Dst)) parts.Add($"dst={c.Dst}");
		if (!string.IsNullOrWhiteSpace(c.Query)) parts.Add($"query={c.Query}");
		if (!string.IsNullOrWhiteSpace(c.Command)) parts.Add($"cmd={c.Command}");
		if (c.Args is { Length: > 0 }) parts.Add($"args=[{string.Join(" ", c.Args)}]");
		if (!string.IsNullOrWhiteSpace(c.WorkingDir)) parts.Add($"wd={c.WorkingDir}");
		return string.Join(" ", parts);
	}
}