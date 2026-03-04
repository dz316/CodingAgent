using System.Diagnostics;

namespace CodingAgent.Cli.Tools;

public sealed class ExecTool
{
	private readonly WorkspaceTools _ws;
	private readonly HashSet<string> _allowedCommands;

	public ExecTool(WorkspaceTools ws, IEnumerable<string>? allowedCommands = null)
	{
		_ws = ws;
		_allowedCommands = new HashSet<string>(
			allowedCommands ?? new[] { "dotnet" },
			StringComparer.OrdinalIgnoreCase
		);
	}

	public async Task<string> ExecAsync(string command, string[]? args, string workingDir = ".", int timeoutSec = 30)
	{
		if (string.IsNullOrWhiteSpace(command))
			throw new ArgumentException("command is required.");

		command = command.Trim();

		if (!_allowedCommands.Contains(command))
			throw new UnauthorizedAccessException($"Command not allowed: {command}");

		// Resolve working dir inside workspace
		// SafeResolve is private, so ensure by listing it (will throw if escapes)
		_ = _ws.List(workingDir);

		var absWorking = Path.GetFullPath(Path.Combine(_ws.WorkspaceRoot, workingDir));
		var psi = new ProcessStartInfo
		{
			FileName = command,
			WorkingDirectory = absWorking,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};

		if (args is { Length: > 0 })
		{
			foreach (var a in args)
				psi.ArgumentList.Add(a);
		}

		using var proc = new Process { StartInfo = psi };

		proc.Start();

		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, timeoutSec)));

		var stdoutTask = proc.StandardOutput.ReadToEndAsync();
		var stderrTask = proc.StandardError.ReadToEndAsync();

		try
		{
			await proc.WaitForExitAsync(cts.Token);
		}
		catch (OperationCanceledException)
		{
			try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
			throw new TimeoutException($"Command timed out after {timeoutSec}s.");
		}

		var stdout = await stdoutTask;
		var stderr = await stderrTask;

		// Prevent huge output from flooding the LLM
		string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "\n...<truncated>...";
		stdout = Truncate(stdout, 20_000);
		stderr = Truncate(stderr, 20_000);

		return $"ExitCode: {proc.ExitCode}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}";
	}
}