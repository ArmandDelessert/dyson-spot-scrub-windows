using DyssCockpit.Presentation;
using Microsoft.UI.Dispatching;

namespace DyssCockpit.App.Services;

/// <summary>The view models' UI thread, as WinUI runs it.</summary>
internal sealed class WinUiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    public bool CheckAccess() => queue.HasThreadAccess;

    public void Post(Action action) => queue.TryEnqueue(() => action());
}
