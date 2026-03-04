namespace CodingAgent.Cli.Models;

public sealed class ToolCall
{
	public string Tool { get; set; } = "";

	// Common
	public string? Path { get; set; }
	public string? Src { get; set; }
	public string? Dst { get; set; }

	// write/patch
	public string? Content { get; set; }
	public bool Append { get; set; }

	// search
	public string? Query { get; set; }
	public string[]? Globs { get; set; }
	public int MaxResults { get; set; } = 50;

	// exec
	public string? Command { get; set; }
	public string[]? Args { get; set; }
	public int TimeoutSec { get; set; } = 30;
	public string? WorkingDir { get; set; }
}

public sealed class AgentStep
{
	public string Message { get; set; } = "";
	public bool Done { get; set; } = false;

	// At most ONE call. If Done=true, Call should be null.
	public ToolCall? Call { get; set; }
}