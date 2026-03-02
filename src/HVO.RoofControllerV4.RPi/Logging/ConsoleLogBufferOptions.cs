using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Logging;

public sealed class ConsoleLogBufferOptions
{
    public const int DefaultCapacity = 400;

    public int Capacity { get; set; } = DefaultCapacity;

    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
}
