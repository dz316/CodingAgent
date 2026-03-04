namespace CodingAgent.Cli.Agent;

public sealed class Policy
{
	// Track which files were read/searched recently
	private readonly HashSet<string> _touched = new(StringComparer.OrdinalIgnoreCase);

	public void MarkObserved(string path)
	{
		if (!string.IsNullOrWhiteSpace(path))
			_touched.Add(Norm(path));
	}

	public void RequireObservedBeforePatch(string path)
	{
		if (string.IsNullOrWhiteSpace(path)) return;
		var p = Norm(path);
		if (!_touched.Contains(p))
			throw new InvalidOperationException($"Policy: must read/search file before patch: {path}");
	}

	private static string Norm(string p) => p.Replace('\\', '/').Trim();
}