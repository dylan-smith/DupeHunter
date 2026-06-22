namespace HarddriveDeduper;

/// <summary>A directory that could not be enumerated during a scan, and the reason why.</summary>
public sealed class SkipRecord
{
    public required string FullPath { get; init; }

    /// <summary>Exception type and message that caused the directory to be skipped.</summary>
    public required string Reason { get; init; }
}
