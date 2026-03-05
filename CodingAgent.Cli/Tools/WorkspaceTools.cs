using System.Text;

namespace CodingAgent.Cli.Tools;

public sealed class WorkspaceTools
{
	private readonly string _workspaceRoot;

	public WorkspaceTools(string workspaceRoot)
	{
		_workspaceRoot = Path.GetFullPath(workspaceRoot);
		Directory.CreateDirectory(_workspaceRoot);
	}

	public string WorkspaceRoot => _workspaceRoot;

	public string List(string path = ".")
	{
		var abs = SafeResolve(path);

		if (!Directory.Exists(abs))
			throw new DirectoryNotFoundException($"Directory not found: {ToWorkspaceRelative(abs)}");

		var dirs = Directory.GetDirectories(abs).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
		var files = Directory.GetFiles(abs).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

		var sb = new StringBuilder();
		sb.AppendLine(ToWorkspaceRelative(abs));
		foreach (var d in dirs) sb.AppendLine($"[D] {Path.GetFileName(d)}");
		foreach (var f in files) sb.AppendLine($"[F] {Path.GetFileName(f)}");
		return sb.ToString();
	}

	public string Read(string path)
	{
		var abs = SafeResolve(path);
		if (!File.Exists(abs))
			throw new FileNotFoundException("File not found.", abs);

		return File.ReadAllText(abs, Encoding.UTF8);
	}

	public void Write(string path, string content, bool append)
	{
		var abs = SafeResolve(path);
		var dir = Path.GetDirectoryName(abs) ?? throw new InvalidOperationException("Invalid file path.");
		Directory.CreateDirectory(dir);

		if (append) File.AppendAllText(abs, content, Encoding.UTF8);
		else File.WriteAllText(abs, content, Encoding.UTF8);
	}

	public void Delete(string path)
	{
		var abs = SafeResolve(path);

		if (File.Exists(abs)) { File.Delete(abs); return; }
		if (Directory.Exists(abs)) { Directory.Delete(abs, recursive: true); return; }

		throw new FileNotFoundException("File or directory not found.", abs);
	}

	public void Move(string src, string dst)
	{
		var absSrc = SafeResolve(src);
		var absDst = SafeResolve(dst);

		if (File.Exists(absSrc))
		{
			Directory.CreateDirectory(Path.GetDirectoryName(absDst)!);
			File.Move(absSrc, absDst, overwrite: true);
			return;
		}

		if (Directory.Exists(absSrc))
		{
			if (Directory.Exists(absDst))
				Directory.Delete(absDst, recursive: true);

			Directory.CreateDirectory(Path.GetDirectoryName(absDst)!);
			Directory.Move(absSrc, absDst);
			return;
		}

		throw new FileNotFoundException("Source file or directory not found.", absSrc);
	}

	public void Copy(string src, string dst)
	{
		var absSrc = SafeResolve(src);
		var absDst = SafeResolve(dst);

		if (!File.Exists(absSrc))
			throw new FileNotFoundException("Source file not found.", absSrc);

		Directory.CreateDirectory(Path.GetDirectoryName(absDst)!);

		File.Copy(absSrc, absDst, overwrite: true);
	}

	/// <summary>
	/// Minimal patch format (safe + easy for LLM):
	/// PATCH v1
	/// ---OLD
	/// <exact old text>
	/// ---NEW
	/// <new text>
	/// If OLD is empty, patch means "insert NEW at end of file".
	/// </summary>
	public void Patch(string path, string patchText)
	{
		var abs = SafeResolve(path);
		if (!File.Exists(abs))
			throw new FileNotFoundException("File not found for patch.", abs);

		var fileText = File.ReadAllText(abs, Encoding.UTF8);

		ParsePatchV1(patchText, out var oldBlock, out var newBlock);

		string updated;
		if (string.IsNullOrEmpty(oldBlock))
		{
			updated = fileText + newBlock;
		}
		else
		{
			var idx = fileText.IndexOf(oldBlock, StringComparison.Ordinal);
			if (idx < 0)
				throw new InvalidOperationException("Patch OLD block not found in file (exact match required).");

			updated = fileText.Remove(idx, oldBlock.Length).Insert(idx, newBlock);
		}

		File.WriteAllText(abs, updated, Encoding.UTF8);
	}

	public void Mkdir(string path)
	{
		var abs = SafeResolve(path);
		Directory.CreateDirectory(abs);
	}

	private static void ParsePatchV1(string patchText, out string oldBlock, out string newBlock)
	{
		// Expect:
		// PATCH v1
		// ---OLD
		// ...
		// ---NEW
		// ...
		var normalized = patchText.Replace("\r\n", "\n");
		if (!normalized.StartsWith("PATCH v1\n", StringComparison.Ordinal))
			throw new InvalidOperationException("Unsupported patch format. Expected header: PATCH v1");

		var oldMarker = "\n---OLD\n";
		var newMarker = "\n---NEW\n";

		var oldStart = normalized.IndexOf(oldMarker, StringComparison.Ordinal);
		var newStart = normalized.IndexOf(newMarker, StringComparison.Ordinal);

		if (oldStart < 0 || newStart < 0 || newStart < oldStart)
			throw new InvalidOperationException("Invalid patch markers. Expected ---OLD and ---NEW.");

		var oldContentStart = oldStart + oldMarker.Length;
		var oldContent = normalized.Substring(oldContentStart, newStart - oldContentStart);

		var newContentStart = newStart + newMarker.Length;
		var newContent = normalized.Substring(newContentStart);

		// Preserve original line endings in output by keeping \n; file write will normalize to OS via content ???.
		oldBlock = oldContent;
		newBlock = newContent;
	}

	private string SafeResolve(string userPath)
	{
		userPath = (userPath ?? ".").Trim();
		userPath = userPath.Replace('\\', Path.DirectorySeparatorChar)
						   .Replace('/', Path.DirectorySeparatorChar)
						   .TrimStart(Path.DirectorySeparatorChar);

		var combined = Path.GetFullPath(Path.Combine(_workspaceRoot, userPath));

		// Ensure path is inside workspaceRoot
		var root = _workspaceRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
		if (!combined.StartsWith(root, StringComparison.OrdinalIgnoreCase)
			&& !string.Equals(combined, _workspaceRoot, StringComparison.OrdinalIgnoreCase))
		{
			throw new UnauthorizedAccessException("Path escapes workspace.");
		}

		return combined;
	}

	private string ToWorkspaceRelative(string absPath)
	{
		var rel = Path.GetRelativePath(_workspaceRoot, absPath);
		return rel == "." ? "./" : rel;
	}
}