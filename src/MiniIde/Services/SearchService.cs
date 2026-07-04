using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiniIde.Models;

namespace MiniIde.Services;

public class SearchService
{
    public async IAsyncEnumerable<FindHit> SearchAsync(
        string root, string query, bool regex,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("rg")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--json");
        psi.ArgumentList.Add("--line-number");
        psi.ArgumentList.Add("--column");
        if (!regex) psi.ArgumentList.Add("--fixed-strings");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(query);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start rg");
        await using var _ = ct.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch { } });

        string? line;
        while ((line = await proc.StandardOutput.ReadLineAsync(ct)) is not null)
        {
            FindHit? hit = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("type", out var typeEl)) continue;
                if (typeEl.GetString() != "match") continue;
                var data = doc.RootElement.GetProperty("data");
                var path = data.GetProperty("path").GetProperty("text").GetString() ?? "";
                var lineNum = data.GetProperty("line_number").GetInt32();
                var text = data.GetProperty("lines").GetProperty("text").GetString() ?? "";
                int col = 1;
                if (data.TryGetProperty("submatches", out var subs) && subs.GetArrayLength() > 0)
                    col = subs[0].GetProperty("start").GetInt32() + 1;
                hit = new FindHit(Path.Combine(root, path), lineNum, col, text.TrimEnd('\n', '\r'));
            }
            catch { }
            if (hit is not null) yield return hit;
        }
        await proc.WaitForExitAsync(ct);
    }
}
