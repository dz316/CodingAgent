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

	public AgentLoop(OpenAiBrain brain, WorkspaceTools ws, SearchTool search, ExecTool exec)
	{
		_brain = brain;
		_ws = ws;
		_search = search;
		_exec = exec;
	}

	public async Task RunAsync(string instruction, int maxSteps = 15)
	{
		var policy = new Policy();

		// Seed feedback so the first call has something predictable
		string lastToolResult = "(none yet)";

		for (int stepNo = 1; stepNo <= maxSteps; stepNo++)
		{
			var listing = _ws.List(".");

			AgentStep step = await _brain.DecideAsync(instruction, listing, lastToolResult);

			Console.WriteLine($"[STEP {stepNo}] [AGENT]: {step.Message}");

			if (step.Done || step.Call is null)
			{
				Console.WriteLine($"[STEP {stepNo}] Done.");
				return;
			}

			// Execute exactly one tool call
			lastToolResult = await ExecuteOneAsync(step.Call, policy);
		}

		Console.WriteLine($"Stopped after maxSteps={maxSteps}. Increase steps or narrow the request.");
	}

	private async Task<string> ExecuteOneAsync(ToolCall call, Policy policy)
	{
		var tool = call.Tool?.Trim().ToLowerInvariant() ?? "";
		Console.WriteLine($"[TOOL] {tool}");

		try
		{
			string output = tool switch
			{
				"list" => _ws.List(call.Path ?? "."),
				"read" => ExecRead(call, policy),
				"write" => ExecWrite(call),
				"patch" => ExecPatch(call, policy),
				"delete" => ExecDelete(call),
				"move" => ExecMove(call),
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

	private string ExecRead(ToolCall call, Policy policy)
	{
		var path = call.Path ?? throw new ArgumentException("read requires path");
		var text = _ws.Read(path);
		policy.MarkObserved(path); // Only read counts as observed
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

	private string ExecPatch(ToolCall call, Policy policy)
	{
		var path = call.Path ?? throw new ArgumentException("patch requires path");
		policy.RequireObservedBeforePatch(path);

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