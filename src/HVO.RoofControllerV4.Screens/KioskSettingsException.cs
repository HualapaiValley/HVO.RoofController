namespace HVO.RoofControllerV4.Screens;

/// <summary>The settings of the kiosk or the Mac app are wrong: it says why and does not start.</summary>
public sealed class KioskSettingsException(string message) : Exception(message);
