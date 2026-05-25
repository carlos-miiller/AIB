using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;

namespace AIB.Services;

public class ContextFile
{
    public string FilePath { get; set; } = "";
    public string FileName => Path.GetFileName(FilePath);
    
    // Determine icon text/emoji based on extension
    public string Icon 
    {
        get
        {
            string ext = Path.GetExtension(FilePath).ToLower();
            return ext switch
            {
                ".pdf" => "📕",
                ".doc" or ".docx" => "📘",
                ".xls" or ".xlsx" or ".csv" => "📗",
                ".txt" or ".md" => "📄",
                ".png" or ".jpg" or ".jpeg" => "🖼️",
                ".cs" or ".js" or ".py" => "💻",
                _ => "📁"
            };
        }
    }
}

public static class ContextService
{
    public static ObservableCollection<ContextFile> ActiveFiles { get; } = new();
    public static ObservableCollection<ContextFile> RecentFiles { get; } = new();

    public static void AddRecentFile(string filePath)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var existing = RecentFiles.FirstOrDefault(f => f.FilePath.Equals(filePath, System.StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                RecentFiles.Remove(existing);
            }
            RecentFiles.Insert(0, new ContextFile { FilePath = filePath });
            
            while (RecentFiles.Count > 5)
            {
                RecentFiles.RemoveAt(RecentFiles.Count - 1);
            }
        });
    }

    public static void AddFile(string filePath)
    {
        if (ActiveFiles.Any(f => f.FilePath.Equals(filePath, System.StringComparison.OrdinalIgnoreCase)))
            return;

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            ActiveFiles.Add(new ContextFile { FilePath = filePath });
        });
    }

    public static void RemoveFile(ContextFile file)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            ActiveFiles.Remove(file);
        });
    }
}
