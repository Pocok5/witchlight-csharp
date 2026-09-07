using System;
using Newtonsoft.Json;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// Carries what the world's clock says, in the game's own words.
///
/// This is sent over the API rather than written to disk. The value is stale
/// before a write would finish, and the page polls the running server for it
/// every two seconds.
/// </summary>
public class LiveWorld
{
    /// <summary>The day and the month, as the game words them, such as `12. May`.</summary>
    public string Date { get; set; } = "";

    /// <summary>The year, worded as `Year 3`.</summary>
    public string Year { get; set; } = "";

    /// <summary>The time on the world's own clock, worded as `14:30`.</summary>
    public string Time { get; set; } = "";

    /// <summary>The season at spawn. A season belongs to a place, and the two
    /// hemispheres are in opposite ones.</summary>
    public string Season { get; set; } = "";

}

/// <summary>
/// Reads the world's clock and words it the way the game would.
/// </summary>
public static class WorldClock
{
    /// <summary>Serialises the world's clock as the JSON the service expects.</summary>
    public static string Json(ICoreServerAPI api)
    {
        return JsonConvert.SerializeObject(Now(api));
    }

    /// <summary>
    /// Returns the date, the time and the season, each in the words the game
    /// would use.
    ///
    /// The wording happens here because the game holds the month names and the
    /// operator's chosen language. A page wording them itself would word them in
    /// English on a server configured otherwise.
    /// </summary>
    public static LiveWorld Now(ICoreServerAPI api)
    {
        var calendar = api.World?.Calendar;
        if (calendar is null)
        {
            return new LiveWorld();
        }

        // `DayOfYear` counts from zero, matching the game's own `DayOfMonth`.
        // Day 0 of a month is worded as its 1st.
        var perMonth = Math.Max(1, calendar.DaysPerMonth);
        var dayOfYear = Math.Max(0, calendar.DayOfYear);
        var month = dayOfYear / perMonth;
        var day = dayOfYear % perMonth + 1;
        var hour = (int)calendar.HourOfDay;
        var minute = (int)((calendar.HourOfDay - hour) * 60);

        return new LiveWorld
        {
            Date = $"{day}. {MonthName(month)}",
            Year = Lang.Get("Year {0}", calendar.Year),
            Time = $"{hour:00}:{minute:00}",
            Season = SeasonAtSpawn(api),
        };
    }

    /// <summary>
    /// Returns the name of a month, counting from zero.
    ///
    /// The game names twelve months and a world may be configured with more. A
    /// month past the twelfth is worded by its number. Wrapping round to January
    /// would name the wrong month.
    /// </summary>
    private static string MonthName(int month)
    {
        string[] names =
        {
            "January", "February", "March", "April", "May", "June",
            "July", "August", "September", "October", "November", "December",
        };
        return month >= 0 && month < names.Length
            ? Lang.Get("month-" + names[month])
            : $"{month + 1}";
    }

    private static string SeasonAtSpawn(ICoreServerAPI api)
    {
        try
        {
            var spawn = WorldFacts.Spawn(api);
            if (spawn is not { } at)
            {
                return "";
            }

            return api.World.Calendar?.GetSeason(new BlockPos(at.X, at.Y, at.Z)).ToString() ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }
}
