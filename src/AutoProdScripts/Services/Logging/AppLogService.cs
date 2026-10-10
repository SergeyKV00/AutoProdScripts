using System.Collections.ObjectModel;
using System.IO;

namespace AutoProdScripts.Services.Logging;

public sealed class AppLogService
{
    private readonly object _sync = new();
    private readonly string _logPath;

    public ObservableCollection<string> Entries { get; } = new();

    public AppLogService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AutoProdScripts",
            "logs");
        Directory.CreateDirectory(dir);
        _logPath = Path.Combine(dir, $"app-{DateTime.Now:yyyyMMdd}.log");
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} [{level}] {message}";
        lock (_sync)
        {
            try
            {
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
            catch
            {
                // ignore IO errors in logger
            }
        }

        // UI thread affinity handled by caller when needed
        try
        {
            if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == true)
            {
                Entries.Insert(0, line);
                Trim();
            }
            else
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    Entries.Insert(0, line);
                    Trim();
                });
            }
        }
        catch
        {
            // app may be shutting down
        }
    }

    private void Trim()
    {
        while (Entries.Count > 200)
            Entries.RemoveAt(Entries.Count - 1);
    }
}
