using System;
using System.Reflection;

namespace ScheduledCopyManager.App
{
    public static class BuildInfo
    {
        public static string Version => Domain.Models.BuildInfo.Version;
        public static string BuildId => Domain.Models.BuildInfo.BuildId;
        public static string InformationalVersion => Domain.Models.BuildInfo.InformationalVersion;
        public static string BuildIdentifier => Domain.Models.BuildInfo.BuildIdentifier;
    }
}
