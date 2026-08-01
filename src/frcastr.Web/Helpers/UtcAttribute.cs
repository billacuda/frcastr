namespace frcastr.Web.Helpers;

/// <summary>
/// Timestamps are stored in UTC but read back from the database with an unspecified
/// <see cref="DateTimeKind"/>, and the server has no idea what zone a viewer is in — formatting
/// one here shows UTC to everybody, putting an evening event on tomorrow's date. Emit the instant
/// into a <c>data-utc</c> attribute instead and let <c>local-time.js</c> render it in the browser's
/// own local time.
/// </summary>
public static class UtcAttribute
{
    /// <summary>Round-trip UTC string for a <c>data-utc</c> attribute.</summary>
    public static string ToUtcAttr(this DateTime value)
        => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("o");

    /// <summary>Null renders no attribute at all, which leaves the server's text in place.</summary>
    public static string? ToUtcAttr(this DateTime? value)
        => value?.ToUtcAttr();
}
