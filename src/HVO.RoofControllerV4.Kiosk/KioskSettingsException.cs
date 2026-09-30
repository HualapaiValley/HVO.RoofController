namespace HVO.RoofControllerV4.Kiosk;

/// <summary>The kiosk's settings are wrong: it says why and does not start.</summary>
public sealed class KioskSettingsException(string message) : Exception(message);
