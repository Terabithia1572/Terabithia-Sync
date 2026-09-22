using System;
using System.Reflection;

namespace ScheduledCopyManager.Domain.Models
{
    public static class BuildInfo
    {
        public static string VersionNumber => "1.0.0.0";
        public static string Version => Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? VersionNumber;
        public static string BuildId => "1.0.0-phase3-unified-retry-execution-v14.2";

        public static string InformationalVersion
        {
            get
            {
                var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                var attr = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                return attr?.InformationalVersion ?? Version;
            }
        }

        public static string BuildIdentifier => $"v{VersionNumber} (BuildId: {BuildId})";
    }
}
