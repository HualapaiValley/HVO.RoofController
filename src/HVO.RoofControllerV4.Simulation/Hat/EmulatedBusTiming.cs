namespace HVO.RoofControllerV4.Simulation.Hat;

/// <summary>
/// How long an I2C transaction on the emulated bus takes. The HAT library's <c>I2cRegisterClient</c> pauses
/// <c>PostTransactionDelayMs</c> (default 15, which the controller keeps) after each successful transaction; a failed
/// transfer throws before that pause. The transfer itself takes its bits at the bus clock: a write of <c>n</c> bytes is
/// START, the address, the register, the data and STOP (20 + 9n bits); a read adds a repeated START and the address
/// again (30 + 9n bits).
/// </summary>
/// <remarks>
/// Assumption: the Raspberry Pi's default I2C clock (100 kHz); the SM-I-010 documentation gives none. Transactions are
/// serialized on one thread in the emulation, so a poll read and a controller read never contend for the bus as they
/// can on the Pi.
/// </remarks>
public sealed record EmulatedBusTiming
{
    /// <summary>Every access completes at the instant it is made.</summary>
    public static EmulatedBusTiming Instant { get; } = new() { PostTransactionDelay = TimeSpan.Zero, BusClockHz = 0 };

    /// <summary>The HAT library defaults: 15 ms after each transaction at 100 kHz.</summary>
    public static EmulatedBusTiming LibraryDefault { get; } = new();

    /// <summary>The pause after each successful transaction (<c>FourRelayFourInputHatOptions.PostTransactionDelayMs</c>).</summary>
    public TimeSpan PostTransactionDelay { get; init; } = TimeSpan.FromMilliseconds(15);

    /// <summary>The I2C clock; 0 means transfers take no time.</summary>
    public int BusClockHz { get; init; } = 100_000;

    /// <summary>The time the transfer of <paramref name="length"/> data bytes takes on the bus.</summary>
    public TimeSpan TransferTime(int length, bool read)
    {
        if (BusClockHz <= 0)
        {
            return TimeSpan.Zero;
        }

        var bits = (read ? 30 : 20) + 9 * length;
        return TimeSpan.FromTicks(bits * TimeSpan.TicksPerSecond / BusClockHz);
    }

    public void Validate()
    {
        if (PostTransactionDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PostTransactionDelay), "The post-transaction delay cannot be negative.");
        }

        if (BusClockHz < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(BusClockHz), "The bus clock cannot be negative.");
        }
    }
}

/// <summary>One transaction on the emulated bus: the first register, the byte count, the direction and, for a write, the first byte.</summary>
public readonly record struct HatBusAccess(byte Register, int Length, bool IsRead, byte? Value)
{
    /// <summary>True when the transaction covers <paramref name="register"/>.</summary>
    public bool Covers(byte register) => register >= Register && register < Register + Length;
}
