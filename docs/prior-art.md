# Prior art

## Read before coding

- **[RoslynPad](https://github.com/roslynpad/roslynpad)** — cross-plat C# editor on Roslyn + AvaloniaEdit. Reference impl for "embed Roslyn in text editor." Study `RoslynHost.cs`.
- **[razzmatazz/csharp-language-server](https://github.com/razzmatazz/csharp-language-server)** — small Roslyn-backed LSP. Good scoping reference for what a minimal C# language service looks like.

## Skip

- `Microsoft.CodeAnalysis.LanguageServer` — not published as public standalone package ([roslyn#71474](https://github.com/dotnet/roslyn/issues/71474)). Embed Roslyn directly instead of running it as LSP server.
