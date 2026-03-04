using System.Text;
using System.Text.Json;
using CodingAgent.Cli.Models;
using OpenAI.Responses;
using CodingAgent.Cli.Infrastructure;

#pragma warning disable OPENAI001

namespace CodingAgent.Cli.Brain;

public sealed class OpenAiBrain
{
    private readonly ResponsesClient _client;
    private readonly string _model;
	private readonly SessionLogger _logger;

	// Hardening limits (tune as needed)
	private const int MaxModelChars = 50_000;          // total model output cap
    private const int MaxToolContentChars = 25_000;    // write/patch content cap

    private static readonly HashSet<string> AllowedTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "list", "read", "write", "patch", "delete", "move", "search", "exec"
    };

    // Prompt file paths (relative to output directory)
    private const string SystemPromptPath = "Brain/Prompts/system.txt";
    private const string UserPromptPath = "Brain/Prompts/user.txt";
    private const string RepairPromptPath = "Brain/Prompts/repair.txt";

    // Cache prompts to avoid disk reads every step
    private static readonly Lazy<string> SystemPrompt = new(() => LoadPrompt(SystemPromptPath));
    private static readonly Lazy<string> UserTemplate = new(() => LoadPrompt(UserPromptPath));
    private static readonly Lazy<string> RepairTemplate = new(() => LoadPrompt(RepairPromptPath));

	public OpenAiBrain(string apiKey, SessionLogger logger, string model = "gpt-5.2")
	{
		_model = model;
		_client = new ResponsesClient(apiKey: apiKey);
		_logger = logger;
	}

	public async Task<AgentStep> DecideAsync(
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
                    ["WORKSPACE_LISTING"] = workspaceListing,
                    ["LAST_TOOL_RESULT"] = lastToolResult,
                    ["USER_INSTRUCTION"] = userInstruction
                })
                : Render(RepairTemplate.Value, new Dictionary<string, string>
                {
                    ["ERROR"] = lastError ?? "(unknown error)",
                    ["INVALID_OUTPUT"] = lastInvalidOutput ?? "(no output captured)",
                    ["WORKSPACE_LISTING"] = workspaceListing,
                    ["LAST_TOOL_RESULT"] = lastToolResult,
                    ["USER_INSTRUCTION"] = userInstruction
                });

            var text = await CallModelAsync(system, userPayload);

			_logger.LogBlock("USER PAYLOAD", userPayload);
			_logger.LogBlock("MODEL RESPONSE", text);

			if (text.Length > MaxModelChars)
            {
                lastInvalidOutput = text;
                lastError = $"Model output too large ({text.Length} chars). Must be <= {MaxModelChars}.";
                continue;
            }

            try
            {
                var step = ParseSingleJsonObject<AgentStep>(text);
                ValidateStep(step);
                return step;
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

    private async Task<string> CallModelAsync(string systemPrompt, string userPayload)
    {
        var options = new CreateResponseOptions { Model = _model };

        // Keep input stable: system + one user message item
        var full = $"SYSTEM:\n{systemPrompt}\n\nUSER:\n{userPayload}\n";
        options.InputItems.Add(ResponseItem.CreateUserMessageItem(full));

        ResponseResult response = await _client.CreateResponseAsync(options);
        var text = response.GetOutputText() ?? "";

        return text.Trim();
    }

    private static string LoadPrompt(string relativePath)
    {
        // Files are copied to output directory by the .csproj rule.
        var baseDir = AppContext.BaseDirectory;
        var fullPath = Path.GetFullPath(Path.Combine(baseDir, relativePath.Replace('\\', '/')));

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Prompt file not found: {fullPath}");

        return File.ReadAllText(fullPath, Encoding.UTF8);
    }

    private static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        // Very small template renderer: replaces {{KEY}} tokens.
        // This is deterministic and avoids pulling in a template library.
        var result = template;

        foreach (var kvp in values)
        {
            result = result.Replace("{{" + kvp.Key + "}}", kvp.Value ?? "", StringComparison.Ordinal);
        }

        // If any tokens remain, it's a dev error.
        if (result.Contains("{{", StringComparison.Ordinal))
            throw new InvalidOperationException("Prompt template contains unreplaced tokens.");

        return result;
    }

    /// <summary>
    /// Strictly parses exactly ONE JSON object with no leading preamble and no trailing garbage.
    /// Prevents "{...}{...}" and extra text.
    /// </summary>
    private static T ParseSingleJsonObject<T>(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Empty model output.");

        int firstNonWs = 0;
        while (firstNonWs < text.Length && char.IsWhiteSpace(text[firstNonWs])) firstNonWs++;

        if (firstNonWs >= text.Length || text[firstNonWs] != '{')
            throw new InvalidOperationException("Model output must start with a JSON object (first non-whitespace must be '{').");

        var utf8 = Encoding.UTF8.GetBytes(text);
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow
        });

        using var doc = JsonDocument.ParseValue(ref reader);

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Top-level JSON must be an object.");

        var consumed = (int)reader.BytesConsumed;
        for (int i = consumed; i < utf8.Length; i++)
        {
            byte b = utf8[i];
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
                continue;

            throw new InvalidOperationException("Model output contains extra data after the JSON object (trailing garbage or multiple JSON objects).");
        }

        var obj = doc.RootElement.Deserialize<T>(new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (obj == null)
            throw new InvalidOperationException("Failed to deserialize JSON into expected schema.");

        return obj;
    }

    private static void ValidateStep(AgentStep step)
    {
        if (step.Message == null)
            throw new InvalidOperationException("Schema violation: 'message' must be present (string).");

        if (step.Done)
        {
            if (step.Call != null)
                throw new InvalidOperationException("Schema violation: when done=true, call must be null.");
            return;
        }

        if (step.Call == null)
            throw new InvalidOperationException("Schema violation: when done=false, call must be present.");

        ValidateToolCall(step.Call);
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

            case "move":
                Require(call.Src, tool, "src");
                Require(call.Dst, tool, "dst");
                break;

            case "search":
                Require(call.Query, tool, "query");
                if (call.MaxResults <= 0 || call.MaxResults > 200)
                    call.MaxResults = 50;
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
}