using System;
using System.Collections.Generic;
namespace AIB.Services {
    public class Reminder {
        public bool IsApproaching { get; set; }
        public bool IsActive { get; set; }
        public DateTime TriggerTime { get; set; }
    }
    public static class ReminderService {
        public static List<Reminder> ActiveReminders => new List<Reminder>();
        public static void DeleteReminder(int id) { }
        public static void CancelReminder(Reminder r) { }
    }
}
