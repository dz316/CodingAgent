using System.Text.RegularExpressions;

namespace CodingAgent.Cli.Tools;

public sealed class SearchTool
{
	private readonly WorkspaceTools _ws;

	public SearchTool(WorkspaceTools ws)
	{
		_ws = ws;
	}

	public string Search(string query, string path = ".", string[]? globs = null, int maxResults = 50)
	{
		if (string.IsNullOrWhiteSpace(query))
			throw new ArgumentException("Search query cannot be empty.");

		// Default: common code files
		globs ??= new[] { "*.cs", "*.csproj", "*.sln", "*.json", "*.md", "*.yml", "*.yaml", "*.config", "*.txt" };

		var absRoot = Path.Combine(_ws.WorkspaceRoot, path.Replace('/', Path.DirectorySeparatorChar));
		absRoot = Path.GetFullPath(absRoot);

		// Ensure within workspace (reuse list safety by resolving)
		_ = _ws.List(path);

		var results = new List<string>();
		foreach (var glob in globs)
		{
			foreach (var file in Directory.EnumerateFiles(absRoot, glob, SearchOption.AllDirectories))
			{
				var relFile = Path.GetRelativePath(_ws.WorkspaceRoot, file);
				int lineNo = 0;

				foreach (var line in File.ReadLines(file))
				{
					lineNo++;
					if (line.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
					{
						results.Add($"{relFile}:{lineNo}: {line}");
						if (results.Count >= maxResults)
							return string.Join(Environment.NewLine, results);
					}
				}
			}
		}

		return results.Count == 0 ? "(no matches)" : string.Join(Environment.NewLine, results);
	}
}