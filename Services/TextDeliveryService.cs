using System.Collections.Concurrent;

namespace whispershortkey.Services;

public enum DeliveryOutcome
{
    /// <summary>Typed or pasted straight into the target window.</summary>
    Injected,

    /// <summary>On the clipboard, waiting for the user to press Ctrl+V.</summary>
    ClipboardOnly,

    /// <summary>Nowhere. The text is still on the job so it can be delivered again.</summary>
    Failed
}

/// <summary>
/// Decides where a transcript goes, and does the work on a dedicated STA thread.
///
/// Off the UI thread on purpose: delivery sleeps for a few hundred milliseconds waiting for
/// focus and for the paste to land, which would otherwise freeze the overlay and the tray.
/// STA on purpose too: the clipboard requires it.
/// </summary>
public sealed class TextDeliveryService : IDisposable
{
    private const int SettleBeforePasteMs = 80;
    private const int SettleAfterPasteMs = 80;

    private readonly KeyboardInjectionService _injector;
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;

    public TextDeliveryService(KeyboardInjectionService injector)
    {
        _injector = injector;

        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "VoiceTray delivery"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>
    /// First delivery of a fresh recording: into the window the user was in when they
    /// started dictating, falling back to the clipboard if it cannot be focused.
    /// </summary>
    public Task<DeliveryOutcome> DeliverToWindowAsync(string text, WindowTarget target, bool useClipboard) =>
        Post(() => DeliverToWindow(text, target, useClipboard));

    /// <summary>
    /// The retry path. A retry can happen minutes later from a tray menu, by which point the
    /// original window may be gone and the user is somewhere else entirely - so the text is
    /// only ever offered, never typed.
    /// </summary>
    public Task<DeliveryOutcome> DeliverToClipboardAsync(string text) =>
        Post(() => _injector.TrySetClipboard(text) ? DeliveryOutcome.ClipboardOnly : DeliveryOutcome.Failed);

    private DeliveryOutcome DeliverToWindow(string text, WindowTarget target, bool useClipboard)
    {
        var focused = _injector.TryFocus(target);

        KeyboardInjectionService.WaitForModifiersReleased();

        if (!focused)
        {
            // Better on the clipboard than typed into whatever happens to be in front.
            return _injector.TrySetClipboard(text) ? DeliveryOutcome.ClipboardOnly : DeliveryOutcome.Failed;
        }

        if (useClipboard)
        {
            if (!_injector.TrySetClipboard(text))
                return DeliveryOutcome.Failed;

            Thread.Sleep(SettleBeforePasteMs);

            if (!_injector.TryPaste())
                return DeliveryOutcome.ClipboardOnly;

            Thread.Sleep(SettleAfterPasteMs);
            return DeliveryOutcome.Injected;
        }

        if (_injector.TryType(text))
            return DeliveryOutcome.Injected;

        return _injector.TrySetClipboard(text) ? DeliveryOutcome.ClipboardOnly : DeliveryOutcome.Failed;
    }

    private Task<DeliveryOutcome> Post(Func<DeliveryOutcome> work)
    {
        var completion = new TaskCompletionSource<DeliveryOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            _work.Add(() =>
            {
                try
                {
                    completion.TrySetResult(work());
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Shutting down.
            completion.TrySetResult(DeliveryOutcome.Failed);
        }

        return completion.Task;
    }

    private void Pump()
    {
        foreach (var work in _work.GetConsumingEnumerable())
            work();
    }

    public void Dispose()
    {
        _work.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
        _work.Dispose();
    }
}
