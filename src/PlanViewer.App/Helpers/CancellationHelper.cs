using System;
using System.Threading;

namespace PlanViewer.App.Helpers;

/// <summary>
/// Whether an exception caught around a cancellable call is the run being cancelled (#628).
///
/// <para><b>Why the token gets a say.</b> Microsoft.Data.SqlClient does not report a cancel the same
/// way twice. A token cancelled while rows stream through <c>reader.ReadAsync</c> comes back as a
/// <see cref="TaskCanceledException"/>, which is what every handler was written for. A token
/// cancelled while the server is still running the query — the usual case, because that is what a
/// slow query that someone stops looks like, and what a newer fetch replacing an older one looks
/// like — comes back as a <c>SqlException</c>: "A severe error occurred on the current command. The
/// results, if any, should be discarded. Operation cancelled by user." A handler that decides by
/// exception type alone shows that text as a failure. #626 fixed the three plan-capture paths with
/// this same test; this is its one home, so the Query Store paths and the session's status strip
/// cannot each grow a slightly different copy.</para>
///
/// <para>Both halves are needed. The type test keeps an <see cref="OperationCanceledException"/>
/// a cancel even when the handler's own token is not the one that was cancelled — a source that
/// something else owns. The token test is what catches the <c>SqlException</c>. A failure under a
/// token that is not cancelled is still a failure, and the caller shows it as it always did.</para>
/// </summary>
internal static class CancellationHelper
{
    /// <summary>
    /// True for any <see cref="OperationCanceledException"/>, and for anything else thrown while
    /// <paramref name="ct"/> — the token of the run the handler belongs to — has been cancelled.
    /// </summary>
    internal static bool IsCancellation(Exception ex, CancellationToken ct) =>
        ex is OperationCanceledException || ct.IsCancellationRequested;
}
