using Repl.Mcp;

namespace PlanViewer.Cli.ReplSurface;

internal static class McpPlanPathPolicy
{
    public static async ValueTask<McpAuthorizedPlanFile> OpenAsync(
        string path,
        IMcpClientRoots clientRoots,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(clientRoots);

        var roots = await GetEffectiveRootsAsync(clientRoots, cancellationToken).ConfigureAwait(false);
        var candidates = Path.IsPathFullyQualified(path)
            ? [path]
            : roots.Select(root => Path.Combine(root, path));

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string lexicalPath;
            try
            {
                lexicalPath = Path.GetFullPath(candidate);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException)
            {
                continue;
            }

            // Never probe an absolute pathname until lexical containment has been established.
            if (!roots.Any(root => IsWithinRoot(lexicalPath, root)) ||
                (OperatingSystem.IsWindows() && ContainsWindowsAlternateDataStream(lexicalPath)) ||
                !Path.GetExtension(lexicalPath).Equals(".sqlplan", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            FileStream? stream = null;
            try
            {
                stream = new FileStream(
                    lexicalPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var openedPath = OpenedFilePathResolver.GetFinalPath(stream);
                if ((OperatingSystem.IsWindows() && ContainsWindowsAlternateDataStream(openedPath)) ||
                    !Path.GetExtension(openedPath).Equals(".sqlplan", StringComparison.OrdinalIgnoreCase) ||
                    !roots.Any(root => IsWithinRoot(openedPath, root)))
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                return new McpAuthorizedPlanFile(stream, Path.GetFileName(openedPath));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (stream is not null)
                    await stream.DisposeAsync().ConfigureAwait(false);
            }
        }

        throw new UnauthorizedAccessException("Plan path is outside the allowed roots or does not exist.");
    }

    private static async ValueTask<IReadOnlyList<string>> GetEffectiveRootsAsync(
        IMcpClientRoots clientRoots,
        CancellationToken cancellationToken)
    {
        if (!clientRoots.IsSupported && !clientRoots.HasSoftRoots)
            return [ResolveExistingDirectory(Directory.GetCurrentDirectory())];

        IReadOnlyList<McpClientRoot> advertisedRoots;
        try
        {
            advertisedRoots = await clientRoots.GetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new UnauthorizedAccessException("Client roots could not be obtained.", exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var roots = advertisedRoots
            .Where(root => root.Uri.IsAbsoluteUri && root.Uri.IsFile)
            .Select(root => TryResolveExistingDirectory(root.Uri.LocalPath))
            .OfType<string>()
            .Distinct(PathComparison)
            .ToList();
        if (roots.Count == 0)
            throw new UnauthorizedAccessException("The client did not advertise a usable file root.");
        return roots;
    }

    private static string? TryResolveExistingDirectory(string path)
    {
        try
        {
            return ResolveExistingDirectory(path);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ResolveExistingDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var volumeRoot = Path.GetPathRoot(fullPath)
            ?? throw new IOException("Root path has no filesystem root.");
        var current = volumeRoot;
        var relative = Path.GetRelativePath(volumeRoot, fullPath);
        if (!relative.Equals(".", StringComparison.Ordinal))
        {
            foreach (var segment in relative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var directory = new DirectoryInfo(ResolveDirectoryEntry(current, segment));
                if (!directory.Exists)
                    throw new DirectoryNotFoundException("Root directory does not exist.");
                current = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory.FullName;
            }
        }

        return Path.GetFullPath(current);
    }

    private static string ResolveDirectoryEntry(string parent, string segment)
    {
        if (!OperatingSystem.IsWindows())
            return Path.Combine(parent, segment);

        foreach (var entry in Directory.EnumerateDirectories(parent))
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(entry));
            if (name.Equals(segment, StringComparison.Ordinal))
                return entry;
        }

        throw new DirectoryNotFoundException("Root directory does not exist with the advertised casing.");
    }

    private static bool IsWithinRoot(string candidate, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        var rootPrefix = Path.EndsInDirectorySeparator(normalizedRoot)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootPrefix, PathComparisonKind);
    }

    internal static bool ContainsWindowsAlternateDataStream(string path)
    {
        var start = path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' ? 2 : 0;
        return path.AsSpan(start).Contains(':');
    }

    private static StringComparer PathComparison => StringComparer.Ordinal;

    private static StringComparison PathComparisonKind => StringComparison.Ordinal;
}

internal sealed class McpAuthorizedPlanFile(FileStream stream, string label) : IAsyncDisposable
{
    public FileStream Stream { get; } = stream;
    public string Label { get; } = label;

    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}
