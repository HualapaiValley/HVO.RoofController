using System.Globalization;

namespace HVO.RoofControllerV4.Simulation;

/// <summary>
/// Checks that an injected fault or wiring variant is one the plant models: a defined value, or for a
/// <see cref="FlagsAttribute"/> enum, a combination of defined flags only.
/// </summary>
internal static class FaultArguments
{
    public static void CheckDefined<T>(T value, string paramName)
        where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(paramName, $"{value} is not a {typeof(T).Name} value.");
        }
    }

    public static void CheckFlags<T>(T value, string paramName)
        where T : struct, Enum
    {
        long defined = 0;
        foreach (var flag in Enum.GetValues<T>())
        {
            defined |= Convert.ToInt64(flag, CultureInfo.InvariantCulture);
        }

        if ((Convert.ToInt64(value, CultureInfo.InvariantCulture) & ~defined) != 0)
        {
            throw new ArgumentOutOfRangeException(paramName, $"{value} is not a combination of {typeof(T).Name} flags.");
        }
    }
}
