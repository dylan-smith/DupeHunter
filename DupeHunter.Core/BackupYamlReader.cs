using System.Globalization;
using System.Text;

namespace DupeHunter;

/// <summary>
/// Parses a YAML backup report back into a <see cref="BackupReport"/>. This is not a general YAML
/// parser — like <see cref="BackupYamlWriter"/> it carries no YAML dependency — it reads exactly the
/// dialect the writer emits (two-space indentation, double-quoted scalars, one <c>key: value</c> per
/// line) and throws <see cref="InvalidDataException"/> with a line number on anything else. Keys
/// within a list item may appear in any order, so a hand-edited file still loads as long as the shape
/// is preserved. <c>totalDeletableBytes</c>, <c>unverifiableFileCount</c> and
/// <c>unverifiableBytes</c> are accepted but ignored: they derive from the entry lists, which are the
/// source of truth.
/// </summary>
public static class BackupYamlReader
{
    public static async Task<BackupReport> LoadAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var lines = await File.ReadAllLinesAsync(path, ct);
        return new Parser(lines).Parse();
    }

    private sealed class Parser(string[] lines)
    {
        private int _i;

        /// <summary>
        /// The line <see cref="Error"/> blames: the one most recently handed to a value parser. The
        /// cursor itself (<see cref="_i"/>) has usually advanced past it by the time a bad value throws.
        /// </summary>
        private int _errorLine;

        public BackupReport Parse()
        {
            DateTime? generatedUtc = null;
            string? backupRoot = null;
            var excludes = new List<string>();
            var scans = new List<ScanRef>();
            var deletableFolders = new List<BackupReportEntry>();
            var deletableFiles = new List<BackupReportEntry>();
            var unmatchedFiles = new List<BackupReportEntry>();
            var unverifiableFiles = new List<UnverifiableEntry>();
            long unmatchedFileCount = 0;
            long unmatchedBytes = 0;

            while (TryCurrent(out var line))
            {
                var (key, value) = SplitKeyValue(line, indent: 0);
                _i++;
                switch (key)
                {
                    case "generatedUtc":
                        generatedUtc = ParseTimestamp(value);
                        break;
                    case "backupRoot":
                        backupRoot = ParseString(value);
                        break;
                    case "excludes":
                        if (value != "[]")
                        {
                            RequireEmpty(key, value);
                            ParseTopLevelStrings(excludes);
                        }
                        break;
                    case "scans":
                        if (value != "[]")
                        {
                            RequireEmpty(key, value);
                            ParseScans(scans);
                        }
                        break;
                    case "totalDeletableBytes":
                    case "unverifiableFileCount":
                    case "unverifiableBytes":
                        ParseLong(value); // derived from the lists on load; validated but ignored
                        break;
                    case "unmatchedFileCount":
                        unmatchedFileCount = ParseLong(value);
                        break;
                    case "unmatchedBytes":
                        unmatchedBytes = ParseLong(value);
                        break;
                    case "deletableFolders":
                        if (value != "[]")
                        {
                            RequireEmpty(key, value);
                            ParseEntries(deletableFolders, folderFields: true);
                        }
                        break;
                    case "deletableFiles":
                        if (value != "[]")
                        {
                            RequireEmpty(key, value);
                            ParseEntries(deletableFiles, folderFields: false);
                        }
                        break;
                    case "unmatchedFiles":
                        if (value != "[]")
                        {
                            RequireEmpty(key, value);
                            ParseEntries(unmatchedFiles, folderFields: false);
                        }
                        break;
                    case "unverifiableFiles":
                        if (value != "[]")
                        {
                            RequireEmpty(key, value);
                            ParseUnverifiable(unverifiableFiles);
                        }
                        break;
                    default:
                        throw Error($"unexpected top-level key '{key}'");
                }
            }

            var report = new BackupReport
            {
                GeneratedUtc = generatedUtc ?? throw Error("missing 'generatedUtc'"),
                BackupRoot = backupRoot ?? throw Error("missing 'backupRoot'"),
                UnmatchedFileCount = (int)Math.Min(unmatchedFileCount, int.MaxValue),
                UnmatchedBytes = unmatchedBytes,
            };
            CopyInto(excludes, report.Excludes);
            CopyInto(scans, report.Scans);
            CopyInto(deletableFolders, report.DeletableFolders);
            CopyInto(deletableFiles, report.DeletableFiles);
            CopyInto(unmatchedFiles, report.UnmatchedFiles);
            CopyInto(unverifiableFiles, report.UnverifiableFiles);
            return report;
        }

        private static void CopyInto<T>(List<T> source, ICollection<T> target)
        {
            foreach (var item in source)
            {
                target.Add(item);
            }
        }

        /// <summary>A top-level string list: <c>- "…"</c> entries at two-space indent (e.g. excludes).</summary>
        private void ParseTopLevelStrings(List<string> values)
        {
            while (TryCurrent(out var line) && line.StartsWith("  - \"", StringComparison.Ordinal))
            {
                _errorLine = _i;
                values.Add(ParseString(line["  - ".Length..]));
                _i++;
            }
        }

        private void ParseScans(List<ScanRef> scans)
        {
            while (TryStartItem(out var firstKey, out var firstValue))
            {
                var fields = ReadItemFields(firstKey, firstValue, locations: null);
                scans.Add(new ScanRef(
                    ParseString(Require(fields, "drive")),
                    ParseString(Require(fields, "scanRunId")),
                    ParseTimestamp(Require(fields, "completedUtc"))));
            }
        }

        /// <summary>
        /// A list of report entries. Folder entries carry <c>fileCount</c> and <c>matchKind</c>
        /// (exact/superset); file entries carry neither and are <see cref="BackupMatchKind.File"/>.
        /// Both may carry an <c>externalLocations</c> list.
        /// </summary>
        private void ParseEntries(List<BackupReportEntry> entries, bool folderFields)
        {
            while (TryStartItem(out var firstKey, out var firstValue))
            {
                var locations = new List<string>();
                var fields = ReadItemFields(firstKey, firstValue, locations);

                var entry = new BackupReportEntry
                {
                    Path = ParseString(Require(fields, "path")),
                    SizeBytes = ParseLong(Require(fields, "sizeBytes")),
                    MatchKind = folderFields ? ParseMatchKind(Require(fields, "matchKind")) : BackupMatchKind.File,
                    FileCount = folderFields ? ParseLong(Require(fields, "fileCount")) : 0,
                };
                foreach (var location in locations)
                {
                    entry.ExternalLocations.Add(location);
                }
                entries.Add(entry);
            }
        }

        private void ParseUnverifiable(List<UnverifiableEntry> entries)
        {
            while (TryStartItem(out var firstKey, out var firstValue))
            {
                var fields = ReadItemFields(firstKey, firstValue, locations: null);
                entries.Add(new UnverifiableEntry
                {
                    Path = ParseString(Require(fields, "path")),
                    SizeBytes = ParseLong(Require(fields, "sizeBytes")),
                    Reason = ParseString(Require(fields, "reason")),
                });
            }
        }

        /// <summary>
        /// If the current line opens a list item (<c>  - key: value</c>), consume it and return its
        /// first pair; otherwise leave the cursor for the caller (the list has ended).
        /// </summary>
        private bool TryStartItem(out string key, out string value)
        {
            key = value = "";
            if (!TryCurrent(out var line) || !line.StartsWith("  - ", StringComparison.Ordinal))
            {
                return false;
            }

            (key, value) = SplitKeyValue(line, indent: 4);
            _i++;
            return true;
        }

        /// <summary>
        /// The rest of one list item: <c>key: value</c> pairs at four-space indent, in any order. An
        /// <c>externalLocations:</c> key switches to reading its six-space <c>- "…"</c> entries into
        /// <paramref name="locations"/> (null when the item type has no such list, e.g. scans).
        /// </summary>
        private Dictionary<string, string> ReadItemFields(string firstKey, string firstValue, List<string>? locations)
        {
            var fields = new Dictionary<string, string> { [firstKey] = firstValue };
            while (TryCurrent(out var line)
                   && line.StartsWith("    ", StringComparison.Ordinal)
                   && !line.StartsWith("      ", StringComparison.Ordinal))
            {
                var (key, value) = SplitKeyValue(line, indent: 4);
                _i++;
                if (key == "externalLocations" && locations is not null)
                {
                    if (value == "[]")
                    {
                        continue;
                    }

                    RequireEmpty(key, value);
                    while (TryCurrent(out var loc) && loc.StartsWith("      - ", StringComparison.Ordinal))
                    {
                        _errorLine = _i;
                        locations.Add(ParseString(loc["      - ".Length..]));
                        _i++;
                    }
                }
                else
                {
                    fields[key] = value;
                }
            }
            return fields;
        }

        /// <summary>Advance past blank and comment lines to the next content line, if any.</summary>
        private bool TryCurrent(out string line)
        {
            while (_i < lines.Length)
            {
                line = lines[_i];
                var trimmed = line.TrimStart();
                if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
                {
                    return true;
                }
                _i++;
            }
            line = "";
            return false;
        }

        /// <summary>
        /// Split <c>key: value</c> at the given indent (a list item's leading <c>- </c> counts toward
        /// it). The colon search is safe because keys never contain one; values are quoted so theirs
        /// don't matter.
        /// </summary>
        private (string Key, string Value) SplitKeyValue(string line, int indent)
        {
            _errorLine = _i;
            var body = line.Length > indent ? line[indent..] : "";
            var colon = body.IndexOf(':');
            return colon <= 0 ? throw Error("expected 'key: value'") : ((string Key, string Value))(body[..colon], body[(colon + 1)..].Trim());
        }

        private string Require(Dictionary<string, string> fields, string key) =>
            fields.TryGetValue(key, out var value) ? value : throw Error($"missing '{key}'");

        private void RequireEmpty(string key, string value)
        {
            if (value.Length != 0)
            {
                throw Error($"expected a nested list under '{key}'");
            }
        }

        private BackupMatchKind ParseMatchKind(string value) => ParseString(value) switch
        {
            "exact" => BackupMatchKind.Exact,
            "superset" => BackupMatchKind.Superset,
            _ => throw Error("expected 'exact' or 'superset'"),
        };

        private string ParseString(string value)
        {
            if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
            {
                throw Error("expected a double-quoted string");
            }

            var sb = new StringBuilder(value.Length - 2);
            for (var i = 1; i < value.Length - 1; i++)
            {
                var c = value[i];
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (++i >= value.Length - 1)
                {
                    throw Error("dangling escape in string");
                }
                sb.Append(value[i] switch
                {
                    '\\' => '\\',
                    '"' => '"',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => throw Error($"unknown escape '\\{value[i]}'"),
                });
            }
            return sb.ToString();
        }

        private long ParseLong(string value) =>
            long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                ? n
                : throw Error("expected a non-negative integer");

        private DateTime ParseTimestamp(string value) =>
            DateTime.TryParseExact(ParseString(value), "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc)
                ? utc
                : throw Error("expected an ISO-8601 UTC timestamp");

        private InvalidDataException Error(string message) =>
            new($"Not a dupehunter backup report, or it has been corrupted: {message} at line {_errorLine + 1}.");
    }
}
