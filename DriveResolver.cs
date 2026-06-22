namespace HarddriveDeduper;

/// <summary>Decides which drive roots a scan should walk.</summary>
public static class DriveResolver
{
    /// <summary>
    /// The drives named on the command line, or — when none were given — every ready fixed drive.
    /// </summary>
    public static List<string> ResolveRoots(Options options)
    {
        if (options.Drives.Count > 0)
            return options.Drives;

        return DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.FullName)
            .ToList();
    }
}
