using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Text;

namespace MiniIde.Services;

public class SyntaxHighlightService
{
    private readonly AdhocWorkspace _workspace = new();
    private readonly ProjectId _projectId;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SyntaxHighlightService()
    {
        var proj = _workspace.AddProject("Adhoc", LanguageNames.CSharp);
        // IL3000: false positive. This is a framework-dependent single-file app (SelfContained=false), so
        // CoreLib is not embedded — it loads from the shared framework and Assembly.Location is a valid path.
        // AppContext.BaseDirectory (the analyzer's suggestion) would be wrong: CoreLib isn't in the app dir.
#pragma warning disable IL3000
        proj = proj.AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
#pragma warning restore IL3000
        _workspace.TryApplyChanges(proj.Solution);
        _projectId = proj.Id;
    }

    public async Task<IReadOnlyList<ClassifiedSpan>> ClassifyAsync(string source, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var docId = DocumentId.CreateNewId(_projectId);
            var sol = _workspace.CurrentSolution.AddDocument(docId, "Snippet.cs", SourceText.From(source));
            _workspace.TryApplyChanges(sol);
            var doc = _workspace.CurrentSolution.GetDocument(docId)!;
            var spans = await Classifier.GetClassifiedSpansAsync(doc, TextSpan.FromBounds(0, source.Length), ct);
            var result = new List<ClassifiedSpan>(spans);
            _workspace.TryApplyChanges(_workspace.CurrentSolution.RemoveDocument(docId));
            return result;
        }
        finally { _lock.Release(); }
    }
}
