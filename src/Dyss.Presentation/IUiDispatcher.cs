namespace Dyss.Presentation;

/// <summary>
/// The UI thread, as the view models see it: whatever pushes data from elsewhere (the MQTT thread,
/// a network event) goes through here before touching anything bound to the screen. The UI provides
/// it: WinUI's DispatcherQueue, or an inline one in the tests.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Whether the caller is already on the UI thread.</summary>
    bool CheckAccess();

    /// <summary>Queues <paramref name="action"/> to run on the UI thread, and returns at once.</summary>
    void Post(Action action);
}
