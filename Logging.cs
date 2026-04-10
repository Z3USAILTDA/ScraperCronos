// Logging.cs
#nullable enable
using System.Text;
using System.Text.RegularExpressions;

public static class Logging
{
    private static readonly SemaphoreSlim _logLock = new(1, 1);

    public static string LogsDir
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("LOG_DIR");
            if (!string.IsNullOrWhiteSpace(custom))
                return custom;

            return Path.Combine(Path.GetTempPath(), "dachser-firecrawl", "logs");
        }
    }

    public static string GetAwbLogPath(string awb)
    {
        Directory.CreateDirectory(LogsDir);
        var safe = Regex.Replace(awb ?? "awb", @"[^\w\-]+", "_");
        return Path.Combine(LogsDir, $"awb_{safe}.log");
    }

    public static async Task LogAwbAsync(string awb, string message, Exception? ex = null)
    {
        var line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z] {message}";
        if (ex != null) line += $" | {ex.GetType().Name}: {ex.Message}";
        line += Environment.NewLine;

        var path = GetAwbLogPath(awb);

        await _logLock.WaitAsync();
        try
        {
            await using var fs = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);

            await using var sw = new StreamWriter(fs, Encoding.UTF8);
            await sw.WriteAsync(line);
            await sw.FlushAsync();
        }
        catch (Exception logEx)
        {
            Console.Error.WriteLine($"[LOG_FAIL] awb={awb} path={path} err={logEx.GetType().Name}: {logEx.Message}");
            Console.Error.WriteLine(line.TrimEnd());
        }
        finally
        {
            _logLock.Release();
        }
    }
}