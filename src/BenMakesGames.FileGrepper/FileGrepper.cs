using System.Buffers;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace BenMakesGames.FileGrepper;

/// <summary>
/// In-process, managed grep engine. Pure text search — no ignore semantics; the caller
/// supplies all exclusions via <see cref="GrepOptions.SkipDirectory"/> / <see cref="GrepOptions.SkipFile"/>.
/// Stateless; safe to reuse across calls.
/// </summary>
public sealed class FileGrepper
{
    // Binary sniff: most binary files fail on byte 0–4, so a small prefix is plenty.
    private const int BinarySniffBytes = 8192;

    // Stateless and reusable — one shared instance instead of one per scanned file.
    private static readonly UTF8Encoding Utf8Strict =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public async IAsyncEnumerable<GrepHit> GrepAsync(
        string rootPath, string pattern, GrepOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Built up front so a bad pattern throws to the caller before any I/O starts.
        var matcher = Matcher.Create(pattern, options);
        var files = EnumerateFiles(rootPath, options);
        var channel = Channel.CreateUnbounded<GrepHit>();

        // Disposal / external cancellation both wind the producer down promptly.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var producer = PumpAsync(files, matcher, channel.Writer, linkedCts.Token);

        try
        {
            await foreach (var hit in channel.Reader.ReadAllAsync(ct))
                yield return hit;
        }
        finally
        {
            linkedCts.Cancel();
            try { await producer; } catch { /* producer surfaces via the channel */ }
        }
    }

    private static async Task PumpAsync(
        IEnumerable<string> files, Matcher matcher, ChannelWriter<GrepHit> writer, CancellationToken ct)
    {
        try
        {
            await Parallel.ForEachAsync(
                files,
                new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
                (path, token) => ScanFileAsync(path, matcher, writer, token));
            writer.Complete();
        }
        catch (Exception ex)
        {
            // Surface cancellation and any hard failure (e.g. root not found) to the reader.
            writer.Complete(ex);
        }
    }

    private static IEnumerable<string> EnumerateFiles(string rootPath, GrepOptions options)
    {
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };

        return new FileSystemEnumerable<string>(
            rootPath,
            static (ref FileSystemEntry entry) => entry.ToFullPath(),
            enumerationOptions)
        {
            ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                options.SkipDirectory is null || !options.SkipDirectory(entry.ToFullPath()),
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                !entry.IsDirectory && (options.SkipFile is null || !options.SkipFile(entry.ToFullPath())),
        };
    }

    private static async ValueTask ScanFileAsync(
        string path, Matcher matcher, ChannelWriter<GrepHit> writer, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);

            if (await IsBinaryAsync(stream, ct))
                return;
            stream.Seek(0, SeekOrigin.Begin);

            // Throwing decoder → non-UTF-8 files fault mid-read and get skipped silently.
            using var reader = new StreamReader(stream, Utf8Strict, detectEncodingFromByteOrderMarks: true);

            var lineNumber = 0;
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                lineNumber++;
                var column = matcher.MatchColumn(line);
                if (column > 0)
                    await writer.WriteAsync(new GrepHit(path, lineNumber, column, line.TrimEnd('\r', '\n')), ct);
            }
        }
        catch (IOException) { /* one bad file must not kill the search */ }
        catch (UnauthorizedAccessException) { }
        catch (DecoderFallbackException) { /* not valid UTF-8 → skip */ }
    }

    private static async ValueTask<bool> IsBinaryAsync(Stream stream, CancellationToken ct)
    {
        // Pooled: a solution-wide grep sniffs thousands of files; a fresh 8 KB array each
        // would be tens of MB of throwaway churn across the parallel workers.
        var buffer = ArrayPool<byte>.Shared.Rent(BinarySniffBytes);
        try
        {
            var window = buffer.AsMemory(0, BinarySniffBytes);
            var read = await stream.ReadAtLeastAsync(window, BinarySniffBytes, throwOnEndOfStream: false, ct);
            for (var i = 0; i < read; i++)
                if (buffer[i] == 0)
                    return true;
            return false;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private abstract class Matcher
    {
        /// <summary>1-based column of the first match, or 0 for no match.</summary>
        public abstract int MatchColumn(string line);

        public static Matcher Create(string pattern, GrepOptions options) =>
            options.Regex
                ? new RegexMatcher(pattern, options.CaseSensitive)
                : new LiteralMatcher(pattern, options.CaseSensitive);

        private sealed class LiteralMatcher(string pattern, bool caseSensitive) : Matcher
        {
            private readonly StringComparison _comparison =
                caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            public override int MatchColumn(string line)
            {
                var index = line.AsSpan().IndexOf(pattern.AsSpan(), _comparison);
                return index >= 0 ? index + 1 : 0;
            }
        }

        private sealed class RegexMatcher : Matcher
        {
            private readonly Regex _regex;

            // NonBacktracking guards against ReDoS on user-typed patterns; it cannot be
            // combined with Compiled, so we forgo Compiled.
            public RegexMatcher(string pattern, bool caseSensitive)
            {
                var regexOptions = RegexOptions.NonBacktracking;
                if (!caseSensitive)
                    regexOptions |= RegexOptions.IgnoreCase;
                _regex = new Regex(pattern, regexOptions);
            }

            public override int MatchColumn(string line)
            {
                var match = _regex.Match(line);
                return match.Success ? match.Index + 1 : 0;
            }
        }
    }
}
