namespace Squeue.ViewModels;

/// What a launch of Squeue.exe asks for.
/// <list type="bullet">
/// <item><c>Squeue.exe a b c</c>: copy a, b and c; ask where to.</item>
/// <item><c>Squeue.exe --to D:\Backup a b</c>: copy a and b into D:\Backup.</item>
/// <item><c>Squeue.exe --paste D:\Backup</c>: copy the files on the clipboard into D:\Backup.</item>
/// </list>
public sealed record LaunchRequest(IReadOnlyList<string> Sources, string? Destination, bool Paste)
{
    public static readonly LaunchRequest Nothing = new([], null, false);

    public bool IsEmpty => Sources.Count == 0 && !Paste;

    public static LaunchRequest Parse(IReadOnlyList<string> args)
    {
        var sources = new List<string>();
        string? destination = null;
        bool paste = false;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i].Trim();
            if (arg.Length == 0) continue;
            if (Is(arg, "--paste") || Is(arg, "--to"))
            {
                if (i + 1 >= args.Count || args[i + 1].Trim().Length == 0)
                    throw new ArgumentException($"{arg} needs a folder after it.");
                destination = Clean(args[++i]);
                paste |= Is(arg, "--paste");
                continue;
            }
            if (arg.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unknown option {arg}.");
            sources.Add(Clean(arg));
        }
        if (paste && sources.Count > 0) throw new ArgumentException("--paste copies the clipboard; don't list files with it.");
        return new LaunchRequest(sources, destination, paste);
    }

    /// Explorer starts Squeue once per selected item. Requests that arrive together are merged:
    /// sources going to the same place become one job; pastes into the same folder happen once.
    public static IReadOnlyList<LaunchRequest> Merge(IEnumerable<LaunchRequest> requests)
    {
        var merged = new List<LaunchRequest>();
        foreach (var request in requests)
        {
            if (request.IsEmpty) continue;
            int at = merged.FindIndex(m => m.Paste == request.Paste && SamePath(m.Destination, request.Destination));
            if (at < 0) { merged.Add(request with { Sources = [.. request.Sources] }); continue; }
            var sources = merged[at].Sources.ToList();
            foreach (string s in request.Sources)
                if (!sources.Contains(s, StringComparer.OrdinalIgnoreCase)) sources.Add(s);
            merged[at] = merged[at] with { Sources = sources };
        }
        return merged;
    }

    private static bool Is(string arg, string option) => string.Equals(arg, option, StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string? a, string? b) =>
        a is null || b is null ? a is null && b is null : string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // Explorer passes "D:\" for a drive and sometimes a stray quote after a trailing backslash ("D:\" → D:").
    private static string Clean(string path)
    {
        path = path.Trim().Trim('"');
        if (path.Length == 2 && path[1] == ':') path += Path.DirectorySeparatorChar;
        return path;
    }
}
