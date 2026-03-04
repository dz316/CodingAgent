using System.Text;

namespace CodingAgent.Cli.Infrastructure;

public sealed class SessionLogger : IDisposable
{
	private readonly StreamWriter _writer;
	private readonly object _lock = new();

	public string FilePath { get; }

	public SessionLogger(string workspaceRoot)
	{
		var logsDir = Path.Combine(workspaceRoot, "logs");
		Directory.CreateDirectory(logsDir);

		var fileName = $"session_{DateTime.UtcNow:yyyyMMdd_HHmmss}.log";
		FilePath = Path.Combine(logsDir, fileName);

		_writer = new StreamWriter(
			new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
			Encoding.UTF8)
		{
			AutoFlush = true
		};

		LogLine("=== SESSION START ===");
	}

	public void LogBlock(string title, string content)
	{
		lock (_lock)
		{
			_writer.WriteLine();
			_writer.WriteLine($"[{DateTime.UtcNow:O}] {title}");
			_writer.WriteLine(new string('-', 80));
			_writer.WriteLine(content);
			_writer.WriteLine(new string('=', 80));
		}
	}

	public void LogLine(string line)
	{
		lock (_lock)
		{
			_writer.WriteLine($"[{DateTime.UtcNow:O}] {line}");
		}
	}

	public void Dispose()
	{
		lock (_lock)
		{
			_writer.WriteLine("=== SESSION END ===");
			_writer.Dispose();
		}
	}
}