namespace PlanViewer.App;

/// <summary>
/// Process-wide answer to "is this the real app or the test host?"
///
/// <para>The headless test harness (#451) boots the REAL <see cref="App"/> — deliberately,
/// because MainWindow resolves styles from the application XAML — which means the real
/// startup side effects run inside the test runner unless something says otherwise. That
/// was not hypothetical: a local <c>dotnet test</c> rewrote HKCU's .sqlplan association to
/// point at the test host executable, polluted the developer's Recent Plans with fixture
/// paths, and destroyed the saved open-tab list, all confirmed live.</para>
///
/// <para>This is the one seam those side effects consult, rather than a scattering of
/// environment sniffs. The harness sets it before the first App boots; nothing in the
/// product ever sets it, so in the real app every check reads a constant false and
/// behavior is unchanged.</para>
/// </summary>
internal static class AppRuntimeMode
{
    /// <summary>
    /// True only inside the test host. Set once by the test harness's module initializer,
    /// never by the app itself.
    /// </summary>
    internal static bool IsTestHost;
}
