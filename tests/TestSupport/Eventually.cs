namespace DyssCockpit.TestSupport;

/// <summary>
/// For what happens on its own a moment later — a timer firing on another thread, a reload started
/// and not awaited — and has no task to wait for: the condition is looked at again every few
/// milliseconds. The limit is generous, for a busy build machine; a passing test never reaches it.
/// </summary>
internal static class Eventually
{
    public static async Task TrueAsync(Func<bool> condition, string because = "the condition never became true")
    {
        var ct = TestContext.Current.CancellationToken;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10, ct);
        Assert.True(condition(), because);
    }
}
