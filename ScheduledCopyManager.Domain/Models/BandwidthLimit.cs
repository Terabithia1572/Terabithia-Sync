namespace ScheduledCopyManager.Domain.Models
{
    public class BandwidthLimit
    {
        public bool Enabled { get; set; } = false;
        public double MegabytesPerSecond { get; set; } = 0;
    }
}
