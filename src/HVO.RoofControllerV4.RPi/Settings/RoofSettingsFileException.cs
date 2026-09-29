using System;

namespace HVO.RoofControllerV4.RPi.Settings;

/// <summary>
/// The settings file or the managed secrets file cannot be used: unreadable, not valid JSON, or holding a value the
/// controller cannot start with. The message names the file, the line or the setting, never a value.
/// </summary>
public sealed class RoofSettingsFileException : Exception
{
    public RoofSettingsFileException(string message)
        : base(message)
    {
    }

    public RoofSettingsFileException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
