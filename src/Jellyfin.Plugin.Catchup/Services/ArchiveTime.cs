namespace Jellyfin.Plugin.Catchup.Services;

/// <summary>
/// Providers index their archive in local time. Some of them say so (and backends convert), some
/// report UTC and still use local time: then everything plays an hour or two too early. The setting
/// "archive time zone" names the zone the archive really uses.
/// </summary>
public static class ArchiveTime
{
    /// <summary>The configured zone, or null when none is set or this system does not know it.</summary>
    public static TimeZoneInfo? Zone
    {
        get
        {
            var id = Plugin.Instance?.Configuration.ArchiveTimeZone;
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// For a backend that passes the time through as UTC: the instant to ask for, so that the archive
    /// (indexed in <see cref="Zone"/> local time) returns what aired at <paramref name="utc"/>.
    /// </summary>
    public static DateTime AsIfUtc(DateTime utc)
    {
        var zone = Zone;
        return zone is null ? utc : utc + zone.GetUtcOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }
}
