using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Threading;

namespace Dyss.App.Views;

/// <summary>
/// Shown at start-up when Dyson cannot be reached — typically a machine started before its Wi-Fi.
/// Counts down to a new attempt, or starts one at once when Windows reports the network back;
/// "Quitter" is the only way it ends without one.
/// </summary>
public partial class ConnectionWaitWindow : Window
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(10);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _secondsLeft = (int)RetryAfter.TotalSeconds;

    private ConnectionWaitWindow(string reason)
    {
        InitializeComponent();
        Reason.Text = reason;
        UpdateCountdown();
        _timer.Tick += (_, _) =>
        {
            if (--_secondsLeft <= 0) Retry();
            else UpdateCountdown();
        };
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        };
    }

    /// <summary>True for another attempt, false when the user chose to quit.</summary>
    public static bool Ask(string reason) => new ConnectionWaitWindow(reason).ShowDialog() == true;

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        // Raised on a thread-pool thread.
        if (e.IsAvailable) Dispatcher.BeginInvoke(Retry);
    }

    private void UpdateCountdown() => Countdown.Text = $"Nouvel essai dans {_secondsLeft} s…";

    private void Retry_Click(object sender, RoutedEventArgs e) => Retry();

    private void Retry()
    {
        // The timer and the network event can both land after the dialog has already answered.
        if (!IsVisible) return;
        DialogResult = true;
    }
}
