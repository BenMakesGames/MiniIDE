using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniIde.Services;

namespace MiniIde.ViewModels;

/// <summary>A tab that renders a streamed output buffer (a project run or a NuGet restore) in the main
/// file area. It has no backing file — <see cref="TabViewModelBase.FilePath"/> is null, its identity is a
/// <c>run:</c>/<c>nuget:</c> key, and its <see cref="Header"/> is fixed at construction.
/// <see cref="Append"/> is called from the background stdout/stderr readers, so both mutators marshal to
/// the UI thread.
///
/// <para>A run tab additionally <em>owns</em> its run (see <see cref="AttachRun"/>): stopping it stops that
/// process and no other, and re-running it starts a fresh process for the same project and selection into
/// this same tab. Tabs constructed without a starter — NuGet restore output, per-package metadata — answer
/// <see cref="HasRun"/> false, which is how the view hides the run strip on them.</para></summary>
public partial class OutputTabViewModel : DocumentTabViewModel
{
    private const int MaxLines = 5000;

    private readonly string _header;
    public override string Header => _header;

    /// <summary>Starts one process for this tab's project and selection and hands the session back through
    /// <see cref="AttachRun"/>. Fixed at construction, so a tab is a run tab from birth and cannot become one
    /// (or stop being one) halfway through its life.</summary>
    private readonly Action<OutputTabViewModel>? _start;

    private RunSession? _run;

    /// <param name="buttonLabel">What this run's button on its project card reads. Only meaningful with a
    /// <paramref name="start"/>; defaults to the header.</param>
    public OutputTabViewModel(string tabId, string header, Action<OutputTabViewModel>? start = null, string? buttonLabel = null)
        : base(tabId, filePath: null, new TextDocument())
    {
        _header = header;
        _start = start;
        ButtonLabel = buttonLabel ?? header;
    }

    /// <summary>The short name a project card's run button shows for this tab — the selection and, from a
    /// project's second run on, its number. The full header is the button's tooltip.</summary>
    public string ButtonLabel { get; }

    /// <summary>Whether this tab is a run tab at all. Constant for the tab's life — deliberately not "a run is
    /// currently attached", so the strip neither waits for the first process nor vanishes (and reflows the
    /// editor) when it exits.</summary>
    public bool HasRun => _start is not null;

    /// <summary>Whether this tab's process is still going. Stays true across a stop request until the process
    /// actually exits, which is what keeps Stop — not Run — on screen while it is being killed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunState))]
    [NotifyCanExecuteChangedFor(nameof(StopRunCommand), nameof(RunAgainCommand))]
    private bool _isRunLive;

    /// <summary>Whether a stop has been asked for and the process has not exited yet. Killing a process tree is
    /// not instantaneous, and during that window neither button is a legal action: the run can't be stopped
    /// twice and can't be started again until this one is gone.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunState), nameof(StopTip))]
    [NotifyCanExecuteChangedFor(nameof(StopRunCommand))]
    private bool _isStopping;

    public string RunState => IsStopping ? "Stopping" : IsRunLive ? "Running" : "Exited";

    /// <summary>Why Stop is greyed out, when it is — a disabled button with no reason on it is just a dead
    /// control.</summary>
    public string StopTip => IsStopping ? "Already stopping — waiting for the process to exit" : "Stop this run";

    /// <summary>Whether the view is pinned to the tail of the output — a new line scrolls into view only while
    /// this is true. The attached editor flips it to false on any user interaction that implies "I'm reading,
    /// not following": scrolling the viewport away from the tail, or clicking into the editor to place a
    /// caret. Scrolling back to the bottom does <em>not</em> re-engage follow; only the overlay "Follow tail"
    /// button does, so the user never has the viewport yanked out from under them unexpectedly. The button is
    /// bound to <c>!StickToEnd</c>, so it appears exactly when the view has fallen behind the tail.</summary>
    [ObservableProperty]
    private bool _stickToEnd = true;

    /// <summary>Raised by <see cref="JumpToEnd"/> so the view attached to this tab (which owns the
    /// <c>TextEditor</c>, not the view model) can scroll to the final line. The binder subscribes on attach
    /// and unsubscribes on detach, so a tab with no realized editor silently drops the request rather than
    /// leaking a handler.</summary>
    public event Action? ScrollToEndRequested;

    /// <summary>Snaps the view back to the tail and re-engages follow mode. Called by the overlay button that
    /// appears when the user has scrolled away from the bottom.</summary>
    [RelayCommand]
    private void JumpToEnd()
    {
        StickToEnd = true;
        ScrollToEndRequested?.Invoke();
    }

    private bool CanStopRun => IsRunLive && !IsStopping;
    private bool CanRunAgain => HasRun && !IsRunLive;

    /// <summary>Hands this tab ownership of <paramref name="run"/>. Call once per launch, on the UI thread,
    /// right after starting it.</summary>
    public void AttachRun(RunSession run)
    {
        _run = run;
        IsStopping = false;
        IsRunLive = true;
        _ = MarkExitedAsync(run);
    }

    /// <summary>Stops this tab's run if it has one still going; a no-op otherwise. Closing the tab does not call
    /// this — a run outlives its tab and only this tab's Stop (or the IDE shutting down) ends it. Shutdown calls
    /// it on every live run, so it guards rather than relying on
    /// <see cref="CanStopRun"/>, which only gates the button.</summary>
    [RelayCommand(CanExecute = nameof(CanStopRun))]
    public void StopRun()
    {
        if (_run is null || !IsRunLive) return;
        // Flipped before the kill, not after it: the button has to grey out on the click that asked for the
        // stop, not on the exit that eventually answers it.
        IsStopping = true;
        _run.Stop();
    }

    /// <summary>Launches this tab's project and selection again, into this same tab. The buffer is cleared
    /// first: the tab shows one run at a time, and an exited run's output above a fresh one's reads as a
    /// single confusing log.</summary>
    [RelayCommand(CanExecute = nameof(CanRunAgain))]
    private void RunAgain()
    {
        if (_start is null || IsRunLive) return;
        Clear();
        _start(this);
    }

    // The completion arrives on a threadpool thread, so the flag flips are posted like Append's writes are —
    // they raise property-changed straight into bindings. Failure is not this tab's story to tell: the
    // process logged its own exit line, and the starter reports a launch failure to the status bar.
    private async Task MarkExitedAsync(RunSession run)
    {
        try { await run.Completion; } catch { }
        Dispatcher.UIThread.Post(() =>
        {
            // Guarded: a re-run may already have attached the next process by the time this lands, and the
            // exit of the run before it must not report that one as dead.
            if (!ReferenceEquals(_run, run)) return;
            IsStopping = false;
            IsRunLive = false;
        });
    }

    public void Clear() => Dispatcher.UIThread.Post(() =>
    {
        Document.Text = "";
        // A fresh run always wants to follow its own output: whatever the user had set the previous run to,
        // the next one starts from the tail. Otherwise re-running an exited process from a scrolled-up state
        // would silently drop every line into the void.
        StickToEnd = true;
    });

    public void Append(string line) => Dispatcher.UIThread.Post(() =>
    {
        Document.Insert(Document.TextLength, line + "\n");
        while (Document.LineCount > MaxLines)
        {
            var first = Document.GetLineByNumber(1);
            Document.Remove(first.Offset, first.TotalLength);
        }
    });
}
