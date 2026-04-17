using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;

namespace Ripple.Tools;

/// <summary>
/// File operation tools — compatible with Claude Code's built-in tools.
/// Single-pass streaming for large files, binary detection, shared read access.
/// </summary>
[McpServerToolType]
public class FileTools
{
    private const int BinaryCheckBytes = 8192;
    private const int MaxLineLength = 10000;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".hg", ".svn", "__pycache__",
        "dist", "build", ".next", ".nuxt", "coverage",
        ".tox", ".venv", "venv", ".mypy_cache", ".pytest_cache",
        "target", "bin", "obj",
    };

    [McpServerTool]
    [Description("Read a file with line numbers. Supports offset/limit for large files.")]
    public static async Task<string> ReadFile(
        [Description("Absolute path to the file")] string path,
        [Description("Line number to start from (0-based)")] int offset = 0,
        [Description("Maximum number of lines to read")] int limit = 2000,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return $"Error: File not found: {path}";
        if (Directory.Exists(path)) return $"Error: Path is a directory: {path}";
        if (IsBinaryFile(path)) return $"Error: Binary file, cannot display: {path}";

        var lines = new List<string>();
        int lineNum = 0, totalLines = 0;

        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            new FileStreamOptions { Access = FileAccess.Read, Share = FileShare.ReadWrite });

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            totalLines++;
            if (lineNum >= offset && lines.Count < limit)
            {
                var display = line.Length > MaxLineLength ? line[..MaxLineLength] + "..." : line;
                lines.Add($"{lineNum + 1,4}: {display}");
            }
            lineNum++;
        }

        var output = string.Join('\n', lines);
        if (totalLines > offset + limit)
            output += $"\n\n[Showing lines {offset + 1}-{offset + lines.Count} of {totalLines}]";

        return output;
    }

    [McpServerTool]
    [Description("Write content to a file. Creates the file if it does not exist, overwrites if it does. Creates parent directories as needed.")]
    public static Task<string> WriteFile(
        [Description("Absolute path to the file")] string path,
        [Description("Content to write")] string content,
        CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(path, content, Utf8NoBom);
        var lines = content.Count(c => c == '\n') + 1;
        return Task.FromResult($"Written {lines} lines to {path}");
    }

    [McpServerTool]
    [Description("Edit a file by replacing an exact string with a new string. By default old_string must be unique. Use replace_all to replace all occurrences.")]
    public static Task<string> EditFile(
        [Description("Absolute path to the file")] string path,
        [Description("Exact string to find and replace")] string old_string,
        [Description("Replacement string")] string new_string,
        [Description("Replace all occurrences (default: false, requires unique match)")] bool replace_all = false,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return Task.FromResult($"Error: File not found: {path}");

        var content = File.ReadAllText(path, Encoding.UTF8);
        var firstIdx = content.IndexOf(old_string, StringComparison.Ordinal);
        if (firstIdx == -1) return Task.FromResult("Error: old_string not found in file.");

        if (replace_all)
        {
            // 1-pass replace via IndexOf loop
            var sb = new StringBuilder();
            int lastEnd = 0, idx = firstIdx, count = 0;
            while (idx != -1)
            {
                sb.Append(content, lastEnd, idx - lastEnd);
                sb.Append(new_string);
                lastEnd = idx + old_string.Length;
                count++;
                idx = content.IndexOf(old_string, lastEnd, StringComparison.Ordinal);
            }
            sb.Append(content, lastEnd, content.Length - lastEnd);
            File.WriteAllText(path, sb.ToString(), Utf8NoBom);
            return Task.FromResult($"Replaced {count} occurrence{(count > 1 ? "s" : "")} in {path}");
        }

        // Single replacement: must be unique
        var secondIdx = content.IndexOf(old_string, firstIdx + 1, StringComparison.Ordinal);
        if (secondIdx != -1)
        {
            int count = 0;
            int idx = -1;
            while ((idx = content.IndexOf(old_string, idx + 1, StringComparison.Ordinal)) != -1) count++;
            return Task.FromResult($"Error: old_string found {count} times. It must be unique. Add more context or use replace_all.");
        }

        var result = string.Concat(content.AsSpan(0, firstIdx), new_string, content.AsSpan(firstIdx + old_string.Length));
        File.WriteAllText(path, result, Utf8NoBom);
        return Task.FromResult($"Replaced 1 occurrence in {path}");
    }

    [McpServerTool]
    [Description("Search file contents using a regular expression. Returns matching lines with file paths and line numbers.")]
    public static async Task<string> SearchFiles(
        [Description("Regular expression pattern to search for")] string pattern,
        [Description("Directory or file to search in (default: current directory)")] string? path = null,
        [Description("Glob pattern to filter files (e.g., \"*.js\", \"*.ts\")")] string? glob = null,
        [Description("Maximum number of matching lines to return")] int max_results = 50,
        CancellationToken cancellationToken = default)
    {
        var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var basePath = path ?? Directory.GetCurrentDirectory();
        var results = new List<string>();

        if (File.Exists(basePath))
        {
            await SearchInFileAsync(basePath, regex, results, max_results, cancellationToken);
        }
        else if (Directory.Exists(basePath))
        {
            await WalkAndSearchAsync(basePath, regex, results, max_results, glob, cancellationToken);
        }
        else
        {
            return $"Error: Path not found: {basePath}";
        }

        if (results.Count == 0) return "No matches found.";
        var output = string.Join('\n', results);
        if (results.Count >= max_results) output += $"\n\n[Results limited to {max_results}]";
        return output;
    }

    [McpServerTool]
    [Description("Find files by glob pattern. Returns matching file paths.")]
    public static Task<string> FindFiles(
        [Description("Glob pattern (e.g., \"*.js\", \"src/**/*.ts\")")] string pattern,
        [Description("Base directory to search in (default: current directory)")] string? path = null,
        [Description("Maximum number of files to return")] int max_results = 200,
        CancellationToken cancellationToken = default)
    {
        var dir = path ?? Directory.GetCurrentDirectory();
        var results = new List<string>();
        FindFilesRecursive(dir, pattern, results, max_results);

        if (results.Count == 0) return Task.FromResult("No files found.");
        return Task.FromResult(string.Join('\n', results));
    }

    // --- Helpers ---

    private static async Task SearchInFileAsync(string filePath, Regex regex, List<string> results, int maxResults, CancellationToken ct)
    {
        if (IsBinaryFile(filePath)) return;

        using var reader = new StreamReader(filePath, Encoding.UTF8, true,
            new FileStreamOptions { Access = FileAccess.Read, Share = FileShare.ReadWrite });

        int lineNum = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            lineNum++;
            if (results.Count >= maxResults) return;
            if (regex.IsMatch(line))
            {
                var display = line.Length > MaxLineLength ? line[..MaxLineLength] + "..." : line;
                results.Add($"{filePath}:{lineNum}: {display}");
            }
        }
    }

    private static async Task WalkAndSearchAsync(string dir, Regex regex, List<string> results, int maxResults, string? globPattern, CancellationToken ct)
    {
        string[] entries;
        try { entries = Directory.GetFileSystemEntries(dir); }
        catch { return; }

        foreach (var entry in entries)
        {
            if (results.Count >= maxResults) return;
            ct.ThrowIfCancellationRequested();

            if (Directory.Exists(entry))
            {
                var name = Path.GetFileName(entry);
                if (SkipDirs.Contains(name)) continue;
                await WalkAndSearchAsync(entry, regex, results, maxResults, globPattern, ct);
            }
            else if (File.Exists(entry))
            {
                if (globPattern != null && !MatchGlob(Path.GetFileName(entry), globPattern)) continue;
                await SearchInFileAsync(entry, regex, results, maxResults, ct);
            }
        }
    }

    private static void FindFilesRecursive(string dir, string pattern, List<string> results, int maxResults)
    {
        string[] entries;
        try { entries = Directory.GetFileSystemEntries(dir); }
        catch { return; }

        foreach (var entry in entries)
        {
            if (results.Count >= maxResults) return;

            if (Directory.Exists(entry))
            {
                var name = Path.GetFileName(entry);
                if (SkipDirs.Contains(name)) continue;
                FindFilesRecursive(entry, pattern, results, maxResults);
            }
            else if (File.Exists(entry))
            {
                var name = Path.GetFileName(entry);
                if (MatchGlob(name, pattern) || MatchGlob(entry, pattern))
                    results.Add(entry);
            }
        }
    }

    private static bool IsBinaryFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[BinaryCheckBytes];
            int read = fs.Read(buf, 0, buf.Length);
            for (int i = 0; i < read; i++)
                if (buf[i] == 0) return true;
            return false;
        }
        catch { return false; }
    }

    private static bool MatchGlob(string str, string pattern)
    {
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", @"[^/\\]*")
            .Replace(@"\?", ".") + "$";
        return Regex.IsMatch(str, regexPattern, RegexOptions.IgnoreCase);
    }
}
