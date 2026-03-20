using System;

namespace PlanViewer.Core.Services;

public enum TimeDisplayMode
{
    Local,
    Utc,
    Server
}

public static class TimeDisplayHelper
{
    public static TimeDisplayMode Current { get; set; } = TimeDisplayMode.Local;

    /// <summary>
    /// Offset in minutes from UTC to the connected SQL Server's local time.
    /// Set after connecting to a server.
    /// </summary>
    public static int ServerUtcOffsetMinutes { get; set; }

    public static DateTime ConvertForDisplay(DateTime utcTime)
    {
        return Current switch
        {
            TimeDisplayMode.Local => utcTime.ToLocalTime(),
            TimeDisplayMode.Utc => DateTime.SpecifyKind(utcTime, DateTimeKind.Utc),
            TimeDisplayMode.Server => utcTime.AddMinutes(ServerUtcOffsetMinutes),
            _ => utcTime.ToLocalTime()
        };
    }

    public static string FormatForDisplay(DateTime utcTime, string format = "yyyy-MM-dd HH:mm")
    {
        return ConvertForDisplay(utcTime).ToString(format);
    }

    public static string Suffix => Current switch
    {
        TimeDisplayMode.Local => "",
        TimeDisplayMode.Utc => " (UTC)",
        TimeDisplayMode.Server => " (Server)",
        _ => ""
    };
}
