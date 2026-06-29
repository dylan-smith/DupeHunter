using System.Text;

namespace DupeHunter;

/// <summary>
/// A live, multi-line progress display for several drives scanning at once. Each drive owns a fixed
/// block of lines — a header plus one line per pass — and a single background timer repaints the whole
/// block in place once a second. Repainting from one timer (rather than one per pass/drive) keeps the
/// cursor moves serialized, so concurrent drives never corrupt each other's lines.
/// </summary>
/// <remarks>
/// In-place repainting uses ANSI cursor movement and only runs on an interactive console. When output
/// is redirected (a file or pipe), the block is written once on disposal with its final values instead,
/// so logs stay readable rather than filling with escape codes.
/// </remarks>
internal sealed class MultiProgress : IAsyncDisposable
{
    /// <summary>One drive's block: a header line and a live line per pass, fed by the scanning task.</summary>
    public sealed class Slot
    {
        private const string Pending = "pending…";

        private readonly string _header;
        private readonly bool _hasHashPass;
        private readonly bool _hasFolderPass;

        // Set by the scanning task when each pass begins; read by the render timer. Reference writes
        // are atomic, and a slightly stale read just shows last second's numbers — both fine here.
        private volatile Func<string>? _enumerate;
        private volatile Func<string>? _hash;
        private volatile Func<string>? _folders;

        internal Slot(string header, bool hasHashPass, bool hasFolderPass)
        {
            _header = header;
            _hasHashPass = hasHashPass;
            _hasFolderPass = hasFolderPass;
        }

        /// <summary>Begin (or update) the pass-one line; <paramref name="snapshot"/> supplies its live text.</summary>
        public void StartEnumerate(Func<string> snapshot) => _enumerate = snapshot;

        /// <summary>Begin (or update) the pass-two line; <paramref name="snapshot"/> supplies its live text.</summary>
        public void StartHash(Func<string> snapshot) => _hash = snapshot;

        /// <summary>Begin (or update) the pass-three (folder fingerprint) line.</summary>
        public void StartFolders(Func<string> snapshot) => _folders = snapshot;

        /// <summary>How many console lines this slot occupies — fixed for the slot's lifetime.</summary>
        internal int LineCount => 1 + (_hasHashPass ? 1 : 0) + (_hasFolderPass ? 1 : 0) + 1;

        internal IEnumerable<string> Lines()
        {
            yield return _header;
            yield return "    pass 1 enumerate: " + (_enumerate is { } e ? e() : Pending);
            if (_hasHashPass)
            {
                yield return "    pass 2 hash: " + (_hash is { } h ? h() : Pending);
            }

            if (_hasFolderPass)
            {
                yield return "    pass 3 folders: " + (_folders is { } f ? f() : Pending);
            }
        }
    }

    private readonly int _lineCount;
    private readonly bool _live;
    private readonly object _lock = new();
    private readonly Timer? _timer;
    private bool _painted;

    internal MultiProgress(IReadOnlyList<Slot> slots)
    {
        Slots = slots;
        _lineCount = slots.Sum(s => s.LineCount);
        // In-place repaint needs a real console that honors ANSI cursor moves. If output is redirected,
        // or VT processing can't be enabled (old console host), fall back to a single final render.
        _live = !Console.IsOutputRedirected && ConsoleVt.TryEnable();

        if (_live)
        {
            Render();
            _timer = new Timer(_ => Render(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>The per-drive slots, in the same order as the drives passed to the reporter.</summary>
    public IReadOnlyList<Slot> Slots { get; }

    private void Render()
    {
        lock (_lock)
        {
            var sb = new StringBuilder();
            if (_live)
            {
                // Jump back to the top of the block, then rewrite each line clearing any leftover text.
                if (_painted)
                {
                    sb.Append($"[{_lineCount}A");
                }

                foreach (var line in Slots.SelectMany(s => s.Lines()))
                {
                    sb.Append("\r[K").Append(line).Append('\n');
                }
            }
            else
            {
                // Redirected output: no cursor tricks — just emit the block once (on disposal).
                foreach (var line in Slots.SelectMany(s => s.Lines()))
                {
                    sb.Append(line).Append('\n');
                }
            }

            Console.Write(sb.ToString());
            _painted = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Awaiting the timer's disposal guarantees no repaint is in flight before the final render.
        if (_timer is not null)
        {
            await _timer.DisposeAsync();
        }

        Render();
    }
}
