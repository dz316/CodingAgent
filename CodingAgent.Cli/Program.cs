using CodingAgent.Cli.Agent;
using CodingAgent.Cli.Brain;
using CodingAgent.Cli.Tools;
using CodingAgent.Cli.Infrastructure;

namespace CodingAgent.Cli;

internal static class Program
{
	private static async Task<int> Main()
	{
		var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
		if (string.IsNullOrWhiteSpace(apiKey))
		{
			Console.WriteLine("Missing OPENAI_API_KEY environment variable.");
			return 1;
		}

		var workspaceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "workspace"));
		var ws = new WorkspaceTools(workspaceRoot);

		// Create session logger
		var logger = new SessionLogger(workspaceRoot);
		Console.WriteLine($"Session log: {logger.FilePath}");

		// Brain
		var brain = new OpenAiBrain(apiKey, logger, model: "gpt-5.2");

		// Tools
		var search = new SearchTool(ws);
		var exec = new ExecTool(ws, allowedCommands: new[] { "dotnet" });

		var agent = new AgentLoop(brain, ws, search, exec);

		Console.WriteLine("Coding Agent CLI");
		Console.WriteLine($"Workspace: {workspaceRoot}");
		Console.WriteLine("Type an instruction, or 'exit'.");
		Console.WriteLine();

		while (true)
		{
			Console.Write("> ");
			var input = Console.ReadLine();
			if (input is null) return 0;

			input = input.Trim();
			if (input.Equals("exit", StringComparison.OrdinalIgnoreCase))
				return 0;

			if (input.Length == 0)
				continue;

			try
			{
				await agent.RunAsync(input, maxSteps: 2000);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"ERROR: {ex.Message}");
			}
		}
	}
}