using System;

namespace PlanViewer.Core.Services;

public enum TimeDisplayMode
{
    Local,
    Utc,
    Server
}

/// <summary>
/// One connection's offset in minutes from UTC to its server's local time (E5).
///
/// <para>Each connect makes a new one, and every document opened on that connection keeps it. That
/// is the point of it being an object rather than a number: the offset is fetched a moment after
/// the connect lands, and the documents that read it must be reading the connection they got their
/// data from, not whichever connection the process touched last. It used to be a process-wide
/// static, so two sessions on servers in different time zones — or one session that reconnected —
/// shifted each other's Query Store times by the wrong server's offset.</para>
///
/// <para>Zero until the fetch lands, and zero if it fails, which reads as UTC in Server mode.
/// Readers take the value each time they format a time, so a document opened before the fetch
/// finished picks the real offset up the next time it redraws.</para>
/// </summary>
public sealed class ServerUtcOffset
{
    public int Minutes { get; set; }
}

public static class TimeDisplayHelper
{
    /// <summary>
    /// The user's display preference. Global on purpose: it is one setting, not something a
    /// connection owns. The offset Server mode needs is not global, and is passed in by whoever
    /// is formatting the time (see <see cref="ServerUtcOffset"/>).
    /// </summary>
    public static TimeDisplayMode Current { get; set; } = TimeDisplayMode.Local;

    public static DateTime ConvertForDisplay(DateTime utcTime, int serverUtcOffsetMinutes)
    {
        return ConvertForDisplay(utcTime, Current, serverUtcOffsetMinutes);
    }

    public static DateTime ConvertForDisplay(DateTime utcTime, TimeDisplayMode mode, int serverUtcOffsetMinutes)
    {
        return mode switch
        {
            TimeDisplayMode.Local => utcTime.ToLocalTime(),
            TimeDisplayMode.Utc => DateTime.SpecifyKind(utcTime, DateTimeKind.Utc),
            TimeDisplayMode.Server => utcTime.AddMinutes(serverUtcOffsetMinutes),
            _ => utcTime.ToLocalTime()
        };
    }

    public static string FormatForDisplay(DateTime utcTime, int serverUtcOffsetMinutes, string format = "yyyy-MM-dd HH:mm")
    {
        return ConvertForDisplay(utcTime, serverUtcOffsetMinutes).ToString(format);
    }

    public static string Suffix => Current switch
    {
        TimeDisplayMode.Local => "",
        TimeDisplayMode.Utc => " (UTC)",
        TimeDisplayMode.Server => " (Server)",
        _ => ""
    };
}
