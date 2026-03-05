namespace CodingAgent.Cli.Agent;

public sealed class Policy
{
	private readonly HashSet<string> _observed = new(StringComparer.OrdinalIgnoreCase);

	public void MarkObserved(string path)
	{
		if (!string.IsNullOrWhiteSpace(path))
			_observed.Add(Norm(path));
	}

	public void RequireObservedBeforePatch(string path)
	{
		if (string.IsNullOrWhiteSpace(path)) return;
		var p = Norm(path);
		if (!_observed.Contains(p))
			throw new InvalidOperationException($"Policy: must read file before patch: {path}");
	}

	private static string Norm(string p) => p.Replace('\\', '/').Trim();
}