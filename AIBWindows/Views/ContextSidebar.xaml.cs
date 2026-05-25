using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIB.Services;

namespace AIB.Views
{
    public partial class ContextSidebar : System.Windows.Controls.UserControl
    {
        public event Action<ChatSession> OnRecoverChat;
        private System.Windows.Threading.DispatcherTimer _pulseTimer;

        public ContextSidebar()
        {
            InitializeComponent();

            // Bindings do Side Panel
            RemindersList.ItemsSource = ReminderService.ActiveReminders;
            ContextFilesList.ItemsSource = ContextService.ActiveFiles;
            RecentAccessedFilesList.ItemsSource = ContextService.RecentFiles;
            
            // Force refresh of memories and history on load
            Refresh();

            _pulseTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10)
            };
            _pulseTimer.Tick += (s, e) => CheckApproachingReminders();
            _pulseTimer.Start();
        }

        private void CheckApproachingReminders()
        {
            bool anyApproaching = false;
            foreach (var r in ReminderService.ActiveReminders)
            {
                if (r.IsActive && (r.TriggerTime - DateTime.Now).TotalMinutes <= 10)
                {
                    r.IsApproaching = true;
                    anyApproaching = true;
                }
            }

            // Animate Tab Icon if not currently active
            if (anyApproaching && TabReminders.IsChecked != true)
            {
                StartTabPulse();
            }
            else
            {
                StopTabPulse();
            }
        }

        private System.Windows.Media.Animation.Storyboard _tabPulseStoryboard;

        private void StartTabPulse()
        {
            if (_tabPulseStoryboard != null) return;
            
            var anim = new System.Windows.Media.Animation.DoubleAnimation(0.2, 1.0, new Duration(TimeSpan.FromSeconds(0.8)))
            {
                AutoReverse = true,
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
            };
            _tabPulseStoryboard = new System.Windows.Media.Animation.Storyboard();
            _tabPulseStoryboard.Children.Add(anim);
            System.Windows.Media.Animation.Storyboard.SetTarget(anim, TabReminders);
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(anim, new PropertyPath("Opacity"));
            _tabPulseStoryboard.Begin();
        }

        private void StopTabPulse()
        {
            if (_tabPulseStoryboard != null)
            {
                _tabPulseStoryboard.Stop();
                _tabPulseStoryboard = null;
                TabReminders.Opacity = 1.0;
            }
        }

        public void Refresh()
        {
            RefreshMemories_Click(null, null);
            ChatHistoryList.ItemsSource = ChatHistoryService.LoadHistory();
        }

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (PanelHistory == null) return; // Control not yet initialized

            PanelHistory.Visibility = Visibility.Collapsed;
            PanelReminders.Visibility = Visibility.Collapsed;
            PanelMemories.Visibility = Visibility.Collapsed;
            PanelContextFiles.Visibility = Visibility.Collapsed;
            PanelRecentFiles.Visibility = Visibility.Collapsed;

            if (TabHistory.IsChecked == true) PanelHistory.Visibility = Visibility.Visible;
            else if (TabReminders.IsChecked == true)
            {
                PanelReminders.Visibility = Visibility.Visible;
                CheckApproachingReminders(); // Will stop animation if active
            }
            else if (TabMemories.IsChecked == true) PanelMemories.Visibility = Visibility.Visible;
            else if (TabContextFiles.IsChecked == true) PanelContextFiles.Visibility = Visibility.Visible;
            else if (TabRecentFiles.IsChecked == true) PanelRecentFiles.Visibility = Visibility.Visible;
        }

        private void Reminder_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is Reminder r)
            {
                if (r.IsApproaching)
                {
                    r.IsApproaching = false;
                    CheckApproachingReminders();
                }
            }
        }

        private void RecoverChat_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is ChatSession session)
            {
                OnRecoverChat?.Invoke(session);
            }
        }

        private void DeleteChat_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string sessionId)
            {
                ChatHistoryService.DeleteSession(sessionId);
                ChatHistoryList.ItemsSource = ChatHistoryService.LoadHistory();
            }
        }

        private void CancelReminder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is Reminder r)
            {
                ReminderService.CancelReminder(r);
            }
        }

        private void RefreshMemories_Click(object? sender, RoutedEventArgs? e)
        {
            RecentMemoriesList.ItemsSource = MemoryService.GetRecentMemories(5);
        }

        private void DeleteMemory_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is int id)
            {
                MemoryService.DeleteMemory(id);
                RefreshMemories_Click(null, null);
            }
        }

        private void ContextFiles_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
                foreach (var f in files)
                {
                    ContextService.AddFile(f);
                }
            }
        }

        private void RemoveContextFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is ContextFile cf)
            {
                ContextService.RemoveFile(cf);
            }
        }

        private void OpenFile_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ContextFile cf)
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = cf.FilePath,
                        UseShellExecute = true
                    };
                    System.Diagnostics.Process.Start(psi);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Erro ao abrir o arquivo: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }
}
