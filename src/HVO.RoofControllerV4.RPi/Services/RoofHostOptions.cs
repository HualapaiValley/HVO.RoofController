namespace HVO.RoofControllerV4.RPi.Services;

/// <summary>
/// Configuration section <c>RoofHost</c>: where people reach the web UI, as the deployment knows it (#209). A controller
/// in a container sees only its own bridge address, so the installer and the deploy script write these at each deploy
/// (<c>RoofHost__Addresses</c>, <c>RoofHost__WebUiUrls</c>, <c>RoofHost__WebUiAccess</c>); they only describe, and change
/// nothing the controller does. Empty means not known.
/// </summary>
public sealed class RoofHostOptions
{
    public const string SectionName = "RoofHost";

    /// <summary>The host machine's LAN addresses, separated by semicolons, commas or spaces (IPv6 with or without brackets).</summary>
    public string? Addresses { get; set; }

    /// <summary>Absolute <c>http</c> or <c>https</c> URLs of the web UI, separated by semicolons or spaces.</summary>
    public string? WebUiUrls { get; set; }

    /// <summary><c>Network</c> (published on the host's addresses) or <c>LocalOnly</c> (on the host alone); anything else is unknown.</summary>
    public string? WebUiAccess { get; set; }
}
