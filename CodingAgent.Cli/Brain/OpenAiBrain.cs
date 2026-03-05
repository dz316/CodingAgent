using System.Text;
using System.Text.Json;
using CodingAgent.Cli.Infrastructure;
using CodingAgent.Cli.Models;
using OpenAI.Responses;

#pragma warning disable OPENAI001

namespace CodingAgent.Cli.Brain;

public sealed class OpenAiBrain
{
	private readonly ResponsesClient _client;
	private readonly string _model;
	private readonly SessionLogger _logger;

	// Limits / hardening
	private const int MaxModelChars = 80_000;
	private const int MaxToolContentChars = 25_000;
	private const int MaxMessagesPerBatch = 20;

	private static readonly HashSet<string> AllowedTools = new(StringComparer.OrdinalIgnoreCase)
	{
		"list",
		"read",
		"write",
		"patch",
		"delete",
		"move",
		"copy",
		"mkdir",
		"search",
		"exec"
	};

	// Prompt file paths (copied to output dir)
	private const string SystemPromptPath = "Brain/Prompts/system.txt";
	private const string UserPromptPath = "Brain/Prompts/user.txt";
	private const string RepairPromptPath = "Brain/Prompts/repair.txt";

	private static readonly Lazy<string> SystemPrompt = new(() => LoadPrompt(SystemPromptPath));
	private static readonly Lazy<string> UserTemplate = new(() => LoadPrompt(UserPromptPath));
	private static readonly Lazy<string> RepairTemplate = new(() => LoadPrompt(RepairPromptPath));

	public OpenAiBrain(string apiKey, SessionLogger logger, string model = "gpt-5.2")
	{
		_model = model;
		_client = new ResponsesClient(apiKey: apiKey);
		_logger = logger;
	}

	public async Task<AgentBatch> DecideAsync(
		string userInstruction,
		string workspaceListing,
		string lastToolResult)
	{
		string? lastInvalidOutput = null;
		string? lastError = null;

		for (int attempt = 1; attempt <= 2; attempt++)
		{
			var system = SystemPrompt.Value;

			var userPayload = attempt == 1
				? Render(UserTemplate.Value, new Dictionary<string, string>
				{
					["LAST_TOOL_RESULT"] = lastToolResult,
					["USER_INSTRUCTION"] = userInstruction
				})
				: Render(RepairTemplate.Value, new Dictionary<string, string>
				{
					["ERROR"] = lastError ?? "(unknown error)",
					["INVALID_OUTPUT"] = lastInvalidOutput ?? "(no output captured)",
					["LAST_TOOL_RESULT"] = lastToolResult,
					["USER_INSTRUCTION"] = userInstruction
				});

			var fullPrompt = $"SYSTEM:\n{system}\n\nUSER:\n{userPayload}\n";
			_logger.LogBlock($"MODEL INPUT (ATTEMPT {attempt})", userPayload);

			var text = await CallModelAsync(fullPrompt);

			_logger.LogBlock($"MODEL RESPONSE (ATTEMPT {attempt})", text);

			if (text.Length > MaxModelChars)
			{
				lastInvalidOutput = text;
				lastError = $"Model output too large ({text.Length} chars). Must be <= {MaxModelChars}.";
				continue;
			}

			try
			{
				var batch = ParseAgentBatch(text);
				ValidateBatch(batch);
				return batch;
			}
			catch (Exception ex)
			{
				lastInvalidOutput = text;
				lastError = ex.Message;

				if (attempt == 2)
					throw new InvalidOperationException(
						$"Model repeatedly returned invalid data: {ex.Message}\n\nLast output:\n{text}", ex);
			}
		}

		throw new InvalidOperationException("Unreachable: DecideAsync retry loop exhausted unexpectedly.");
	}

	private async Task<string> CallModelAsync(string fullPrompt)
	{
		var options = new CreateResponseOptions { Model = _model };
		options.InputItems.Add(ResponseItem.CreateUserMessageItem(fullPrompt));

		ResponseResult response = await _client.CreateResponseAsync(options);
		return (response.GetOutputText() ?? "").Trim();
	}

	/// <summary>
	/// Accepts:
	/// - single AgentMessage object
	/// - { "messages": [AgentMessage...] } envelope
	/// - [AgentMessage...]
	/// - concatenated AgentMessage objects: {...}{...}
	/// </summary>
	private static AgentBatch ParseAgentBatch(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
			throw new InvalidOperationException("Empty model output.");

		var utf8 = Encoding.UTF8.GetBytes(text);
		var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
		{
			AllowTrailingCommas = false,
			CommentHandling = JsonCommentHandling.Disallow
		});

		// Read first token
		if (!reader.Read())
			throw new InvalidOperationException("Empty JSON stream.");

		// Case 1: Array
		if (reader.TokenType == JsonTokenType.StartArray)
		{
			using var doc = JsonDocument.ParseValue(ref reader);
			var msgs = doc.RootElement.Deserialize<List<AgentMessage>>(JsonOpts)
					   ?? throw new InvalidOperationException("Failed to parse messages array.");

			EnsureNoTrailingGarbage(utf8, reader.BytesConsumed);
			return new AgentBatch { Messages = msgs };
		}

		// Case 2/3/4: One or many objects in a stream
		var messages = new List<AgentMessage>();

		while (true)
		{
			// We expect an object start; if token isn't StartObject, skip whitespace by advancing.
			if (reader.TokenType != JsonTokenType.StartObject)
			{
				if (!reader.Read()) break;
				continue;
			}

			using var doc = JsonDocument.ParseValue(ref reader);
			if (doc.RootElement.ValueKind != JsonValueKind.Object)
				throw new InvalidOperationException("Top-level JSON values must be objects.");

			// Envelope?
			if (doc.RootElement.TryGetProperty("messages", out var msgProp) &&
				msgProp.ValueKind == JsonValueKind.Array)
			{
				var msgs = msgProp.Deserialize<List<AgentMessage>>(JsonOpts)
						   ?? throw new InvalidOperationException("Failed to parse envelope.messages.");

				messages.AddRange(msgs);
			}
			else
			{
				var msg = doc.RootElement.Deserialize<AgentMessage>(JsonOpts)
						  ?? throw new InvalidOperationException("Failed to parse AgentMessage object.");

				messages.Add(msg);
			}

			// Advance to next token (if any)
			if (!reader.Read())
				break;

			// If next token is another StartObject, loop continues.
			// If it's whitespace/end, loop ends naturally.
		}

		EnsureNoTrailingGarbage(utf8, reader.BytesConsumed);

		return new AgentBatch { Messages = messages };
	}

	private static void EnsureNoTrailingGarbage(byte[] utf8, long bytesConsumed)
	{
		for (int i = (int)bytesConsumed; i < utf8.Length; i++)
		{
			byte b = utf8[i];
			if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') continue;
			throw new InvalidOperationException("Trailing garbage detected after JSON content.");
		}
	}

	private static readonly JsonSerializerOptions JsonOpts = new()
	{
		PropertyNameCaseInsensitive = true
	};

	private static void ValidateBatch(AgentBatch batch)
	{
		if (batch.Messages.Count == 0)
			throw new InvalidOperationException("Batch contains no messages.");

		if (batch.Messages.Count > MaxMessagesPerBatch)
			throw new InvalidOperationException($"Too many messages in one response ({batch.Messages.Count}). Limit is {MaxMessagesPerBatch}.");

		foreach (var m in batch.Messages)
			ValidateMessage(m);
	}

	private static void ValidateMessage(AgentMessage msg)
	{
		if (msg.Message is null)
			throw new InvalidOperationException("Schema violation: message must be a string.");

		if (msg.Done)
		{
			// done=true can have call=null
			return;
		}

		if (msg.Call is null)
			throw new InvalidOperationException("Schema violation: done=false requires call.");

		ValidateToolCall(msg.Call);
	}

	private static void ValidateToolCall(ToolCall call)
	{
		if (string.IsNullOrWhiteSpace(call.Tool))
			throw new InvalidOperationException("Tool call invalid: 'tool' is required.");

		if (!AllowedTools.Contains(call.Tool))
			throw new InvalidOperationException($"Tool call invalid: tool '{call.Tool}' is not allowed.");

		var tool = call.Tool.Trim().ToLowerInvariant();

		switch (tool)
		{
			case "list":
				break;

			case "read":
			case "delete":
				Require(call.Path, tool, "path");
				break;

			case "write":
				Require(call.Path, tool, "path");
				if (call.Content == null)
					throw new InvalidOperationException("write requires 'content' (can be empty string).");
				EnforceContentLimit(tool, call.Content);
				break;

			case "patch":
				Require(call.Path, tool, "path");
				if (call.Content == null)
					throw new InvalidOperationException("patch requires 'content' containing PATCH v1 text.");
				EnforceContentLimit(tool, call.Content);

				if (!call.Content.Replace("\r\n", "\n").StartsWith("PATCH v1\n", StringComparison.Ordinal))
					throw new InvalidOperationException("patch content must start with 'PATCH v1'.");
				break;

			case "copy":
			case "move":
				Require(call.Src, tool, "src");
				Require(call.Dst, tool, "dst");
				break;

			case "search":
				Require(call.Query, tool, "query");
				if (call.MaxResults <= 0 || call.MaxResults > 200)
					call.MaxResults = 50;
				break;

			case "mkdir":
				Require(call.Path, tool, "path");
				break;

			case "exec":
				Require(call.Command, tool, "command");
				if (!string.Equals(call.Command, "dotnet", StringComparison.OrdinalIgnoreCase))
					throw new InvalidOperationException("exec only allows command 'dotnet'.");
				if (call.TimeoutSec <= 0 || call.TimeoutSec > 120)
					call.TimeoutSec = 30;
				break;
		}
	}

	private static void EnforceContentLimit(string tool, string content)
	{
		if (content.Length > MaxToolContentChars)
			throw new InvalidOperationException($"{tool} content too large ({content.Length} chars). Use smaller incremental edits.");
	}

	private static void Require(string? value, string tool, string field)
	{
		if (string.IsNullOrWhiteSpace(value))
			throw new InvalidOperationException($"{tool} requires '{field}'.");
	}

	private static string LoadPrompt(string relativePath)
	{
		var baseDir = AppContext.BaseDirectory;
		var fullPath = Path.GetFullPath(Path.Combine(baseDir, relativePath.Replace('\\', '/')));

		if (!File.Exists(fullPath))
			throw new FileNotFoundException($"Prompt file not found: {fullPath}");

		return File.ReadAllText(fullPath, Encoding.UTF8);
	}

	private static string Render(string template, IReadOnlyDictionary<string, string> values)
	{
		var result = template;
		foreach (var kvp in values)
			result = result.Replace("{{" + kvp.Key + "}}", kvp.Value ?? "", StringComparison.Ordinal);

		if (result.Contains("{{", StringComparison.Ordinal))
			throw new InvalidOperationException("Prompt template contains unreplaced tokens.");

		return result;
	}
}