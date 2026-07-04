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
        proj = proj.AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
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
