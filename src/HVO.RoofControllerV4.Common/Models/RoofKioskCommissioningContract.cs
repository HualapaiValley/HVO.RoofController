using System.Text;
namespace HVO.RoofControllerV4.Common.Models;

/// <summary>Stable-principal kiosk sign-in. Names are display data only.</summary>
public sealed record RoofKioskPinRequest(string PrincipalId, string Pin)
{
    private bool PrintMembers(StringBuilder builder) { builder.Append("Kiosk PIN request (redacted)"); return true; }
}
/// <summary>Either a setup-only grant or a personal PIN session, never both.</summary>
public sealed record RoofKioskPinResponse(RoofSessionResponse? Session, string? SetupToken, DateTimeOffset? SetupExpiresUtc)
{
    private bool PrintMembers(StringBuilder builder) { builder.Append("Kiosk PIN response (redacted)"); return true; }
}
/// <summary>Mandatory confirmed personal PIN; never log its fields.</summary>
public sealed record RoofKioskSetupRequest(string SetupToken, string NewPin, string ConfirmPin)
{
    private bool PrintMembers(StringBuilder builder) { builder.Append("Kiosk setup request (redacted)"); return true; }
}
