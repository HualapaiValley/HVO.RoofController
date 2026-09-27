namespace HVO.RoofControllerV4.Common.Models;

/// <summary>
/// Result of the most recent relay transition as observed by reading the HAT relay register back.
/// A verified register does not prove the physical contacts moved; it proves only the HAT accepted the write.
/// </summary>
public enum RoofRelayRegisterState
{
    /// <summary>No relay transition has been attempted or read back yet.</summary>
    Unknown = 0,

    /// <summary>The last transition was written and the register read-back matched the expected mask.</summary>
    Verified = 1,

    /// <summary>The last transition failed, or the read-back failed or did not match. Treat relay state as unknown.</summary>
    Unverified = 2
}
