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
	public string? WorkingDir { get; set; } // relative to workspace
}

public sealed class AgentMessage
{
	public string Message { get; set; } = "";
	public bool Done { get; set; } = false;

	// For “done” messages this may be null.
	public ToolCall? Call { get; set; }
}

/// <summary>
/// Model output can be either:
/// - a single AgentMessage
/// - an array of AgentMessage
/// - an envelope: { "messages": [ ... ] }
/// - multiple concatenated AgentMessage objects
/// We normalize into AgentBatch.
/// </summary>
public sealed class AgentBatch
{
	public List<AgentMessage> Messages { get; set; } = new();
}