namespace Dyss.Presentation;

/// <summary>
/// The UI thread, as the view models see it: whatever pushes data from elsewhere (the MQTT thread,
/// a network event) goes through here before touching anything bound to the screen. Each UI
/// framework provides its own: WPF's Dispatcher, WinUI's DispatcherQueue.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Whether the caller is already on the UI thread.</summary>
    bool CheckAccess();

    /// <summary>Queues <paramref name="action"/> to run on the UI thread, and returns at once.</summary>
    void Post(Action action);
}
