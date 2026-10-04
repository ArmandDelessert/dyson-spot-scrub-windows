using System.Net.NetworkInformation;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using static Dyss.Core.Translation;

namespace Dyss.App.Views;

/// <summary>
/// Shown at start-up when Dyson cannot be reached — typically a machine started before its Wi-Fi.
/// Counts down to a new attempt, or starts one at once when Windows reports the network back;
/// "Quitter" is the only way it ends without one.
/// </summary>
public sealed partial class ConnectionWaitView : UserControl
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(10);
    private readonly TaskCompletionSource<bool> _answer = new();
    private readonly DispatcherQueueTimer _timer;
    private int _secondsLeft = (int)RetryAfter.TotalSeconds;

    public ConnectionWaitView(string reason)
    {
        InitializeComponent();
        Reason.Text = reason;
        UpdateCountdown();
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) =>
        {
            if (--_secondsLeft <= 0) Answer(true);
            else UpdateCountdown();
        };
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => Stop();
    }

    /// <summary>True for another attempt, false when the user chose to quit.</summary>
    public Task<bool> Decision => _answer.Task;

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        // Raised on a thread-pool thread.
        if (e.IsAvailable) DispatcherQueue.TryEnqueue(() => Answer(true));
    }

    private void UpdateCountdown() => Countdown.Text = T($"Nouvel essai dans {_secondsLeft} s…", $"Trying again in {_secondsLeft} s…");

    private void Retry_Click(object sender, RoutedEventArgs e) => Answer(true);
    private void Quit_Click(object sender, RoutedEventArgs e) => Answer(false);

    /// <summary>The timer and the network event can both land after the view has already answered: only the first counts.</summary>
    private void Answer(bool retry)
    {
        Stop();
        _answer.TrySetResult(retry);
    }

    private void Stop()
    {
        _timer.Stop();
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
    }
}
