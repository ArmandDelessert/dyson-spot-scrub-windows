using System.Windows.Threading;
using Dyss.Presentation;

namespace Dyss.App.Services;

/// <summary>The view models' UI thread, as WPF runs it.</summary>
internal sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public bool CheckAccess() => dispatcher.CheckAccess();

    public void Post(Action action) => dispatcher.BeginInvoke(action);
}
