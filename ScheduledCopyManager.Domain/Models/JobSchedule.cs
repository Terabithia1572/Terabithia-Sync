using System;
using ScheduledCopyManager.Domain.Enums;

namespace ScheduledCopyManager.Domain.Models
{
    public class JobSchedule
    {
        public ScheduleType ScheduleType { get; set; } = ScheduleType.Daily;
        public TimeSpan TimeOfDay { get; set; } = new TimeSpan(8, 0, 0);
        public bool[] DaysOfWeek { get; set; } = new bool[7] { true, true, true, true, true, false, false }; // Mon-Fri
        public int DayOfMonth { get; set; } = 1;
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public string? CronExpression { get; set; }
    }
}
