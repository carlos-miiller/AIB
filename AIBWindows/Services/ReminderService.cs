using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;

namespace AIB.Services;

public class Reminder : System.ComponentModel.INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Message { get; set; } = "";
    public DateTime TriggerTime { get; set; }
    public bool IsActive { get; set; } = true;
    public string DisplayTime => TriggerTime.ToString("HH:mm");

    private bool _isApproaching;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsApproaching
    {
        get => _isApproaching;
        set
        {
            if (_isApproaching != value)
            {
                _isApproaching = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsApproaching)));
            }
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public static class ReminderService
{
    private static readonly string FilePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIB", "reminders.json");

    public static ObservableCollection<Reminder> ActiveReminders { get; } = new();

    static ReminderService()
    {
        LoadReminders();
    }

    private static void LoadReminders()
    {
        if (!System.IO.File.Exists(FilePath)) return;

        try
        {
            var json = System.IO.File.ReadAllText(FilePath);
            var loaded = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<Reminder>>(json);
            if (loaded != null)
            {
                foreach (var r in loaded)
                {
                    if (r.IsActive)
                    {
                        ActiveReminders.Add(r);
                        ScheduleReminderTask(r);
                    }
                }
            }
        }
        catch { }
    }

    private static void SaveReminders()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(FilePath);
            if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir!);

            var list = new System.Collections.Generic.List<Reminder>(ActiveReminders);
            var json = System.Text.Json.JsonSerializer.Serialize(list, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(FilePath, json);
        }
        catch { }
    }

    public static string AddReminder(string message, int delayMinutes)
    {
        var triggerTime = DateTime.Now.AddMinutes(delayMinutes);
        var reminder = new Reminder
        {
            Message = message,
            TriggerTime = triggerTime
        };
        
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            ActiveReminders.Add(reminder);
            SaveReminders();
        });

        ScheduleReminderTask(reminder);

        return $"Lembrete agendado para as {triggerTime:HH:mm}.";
    }

    private static void ScheduleReminderTask(Reminder reminder)
    {
        Task.Run(async () =>
        {
            var delayMs = (int)(reminder.TriggerTime - DateTime.Now).TotalMilliseconds;
            if (delayMs > 0)
            {
                await Task.Delay(delayMs);
            }
            
            if (reminder.IsActive && ActiveReminders.Contains(reminder))
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    if (System.Windows.Application.Current is App app)
                    {
                        app.ShowNotification("Lembrete do AIB", reminder.Message);
                    }
                    ActiveReminders.Remove(reminder);
                    SaveReminders();
                });
            }
        });
    }

    public static void CancelReminder(Reminder reminder)
    {
        reminder.IsActive = false;
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            ActiveReminders.Remove(reminder);
            SaveReminders();
        });
    }
}
