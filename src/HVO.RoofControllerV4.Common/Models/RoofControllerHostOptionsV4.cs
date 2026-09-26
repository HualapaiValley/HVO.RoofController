namespace HVO.RoofControllerV4.Common.Models;

public record class RoofControllerHostOptionsV4
{
    public int RestartOnFailureWaitTime { get; set; } = 10;

    /// <summary>
    /// Operator-facing controller name reported in status snapshots so clients can show which controller they operate.
    /// </summary>
    public string ControllerName { get; set; } = "HVO Roof Controller";
}
