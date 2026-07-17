using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.CodeAnalysis.CSharp;

namespace MiniIde.Views;

/// <summary>The app's first modal dialog: prompts for a symbol's new name. Shown over <c>MainWindow</c> via
/// <c>ShowDialog&lt;string?&gt;</c> — it returns the entered name, or null when the user cancels. "Rename" stays
/// disabled until the text is a non-empty, changed, valid C# identifier (<see cref="SyntaxFacts.IsValidIdentifier"/>),
/// so a blank / unchanged / malformed name simply can't be committed. Deliberately self-contained (no view
/// model) — the whole of it is one text field and two buttons.</summary>
public partial class RenameDialog : Window
{
    private readonly string _currentName;

    // Parameterless ctor for the XAML designer / loader; the real entry point pre-fills the current name.
    public RenameDialog() : this("") { }

    public RenameDialog(string currentName)
    {
        InitializeComponent();
        _currentName = currentName;
        NameBox.Text = currentName;
        NameBox.SelectAll();
        NameBox.Focus();
        UpdateOkState();
    }

    private void OnNameChanged(object? sender, TextChangedEventArgs e) => UpdateOkState();

    private void UpdateOkState() => OkButton.IsEnabled = IsAcceptable(NameBox.Text);

    // The same gate the OK button uses defensively — non-empty, actually changed, and a legal C# identifier.
    private bool IsAcceptable(string? name) =>
        !string.IsNullOrEmpty(name)
        && !string.Equals(name, _currentName, StringComparison.Ordinal)
        && SyntaxFacts.IsValidIdentifier(name);

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text;
        if (!IsAcceptable(name)) return; // Enter can reach here while disabled on some platforms — re-check.
        Close(name);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
