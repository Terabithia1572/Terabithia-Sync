using System;
using System.Collections.Generic;
using System.Linq;

namespace ScheduledCopyManager.Domain.Models
{
    public enum PreflightSeverity
    {
        Success = 0,
        Warning = 1,
        BlockingError = 2
    }

    public enum PreflightIssueCode
    {
        SOURCE_NOT_FOUND,
        DESTINATION_NOT_FOUND,
        DESTINATION_NOT_WRITABLE,
        INSUFFICIENT_SPACE,
        SOURCE_EQUALS_DESTINATION,
        DESTINATION_INSIDE_SOURCE,
        SOURCE_INSIDE_DESTINATION,
        SOURCE_ACCESS_DENIED,
        DESTINATION_UNAVAILABLE,
        GENERAL_ERROR
    }

    public class PreflightIssue
    {
        public PreflightIssueCode Code { get; set; }
        public PreflightSeverity Severity { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string? Path { get; set; }
        public string? SuggestedAction { get; set; }
    }

    public class PreflightResult
    {
        public List<PreflightIssue> Issues { get; } = new List<PreflightIssue>();

        public PreflightSeverity Severity
        {
            get
            {
                if (Issues.Any(i => i.Severity == PreflightSeverity.BlockingError))
                    return PreflightSeverity.BlockingError;
                if (Issues.Any(i => i.Severity == PreflightSeverity.Warning))
                    return PreflightSeverity.Warning;
                return PreflightSeverity.Success;
            }
        }

        public bool IsSuccess => Severity == PreflightSeverity.Success;
        public bool HasBlockingErrors => Severity == PreflightSeverity.BlockingError;
        public bool HasWarnings => Severity == PreflightSeverity.Warning;

        public void AddIssue(PreflightIssue issue)
        {
            Issues.Add(issue);
        }

        public static PreflightResult Success() => new PreflightResult();
    }
}
