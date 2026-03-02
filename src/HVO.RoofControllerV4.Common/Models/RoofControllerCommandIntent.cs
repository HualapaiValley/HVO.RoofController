namespace HVO.RoofControllerV4.Common.Models;

public enum RoofControllerCommandIntent
{
    None = 0,
    Initialize = 1,
    Open = 2,
    Close = 3,
    Stop = 4,
    LimitStop = 5,
    FaultStop = 6,
    SafetyStop = 7
}
