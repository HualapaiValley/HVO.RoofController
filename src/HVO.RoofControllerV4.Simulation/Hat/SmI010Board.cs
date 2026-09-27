namespace HVO.RoofControllerV4.Simulation.Hat;

/// <summary>A fault on one relay's contacts.</summary>
public enum RelayContactFault
{
    None = 0,

    /// <summary>The contacts are welded closed and stay closed whatever the coil does.</summary>
    Welded,

    /// <summary>The contacts never close (burnt contacts, broken wire or open coil).</summary>
    Dead
}

/// <summary>Timing of the HAT relays. Sequent does not state operate or release times; these are typical small-relay values.</summary>
public sealed record SmI010Options
{
    /// <summary>Stack level set by the address jumpers; the I2C address is 0x0E + stack level.</summary>
    public int StackLevel { get; init; }

    public TimeSpan RelayOperateTime { get; init; } = TimeSpan.FromMilliseconds(10);

    public TimeSpan RelayReleaseTime { get; init; } = TimeSpan.FromMilliseconds(5);

    /// <summary>Values of registers 0x78-0x7B: hardware major, hardware minor, firmware major, firmware minor.</summary>
    public byte[] Revision { get; init; } = [1, 2, 1, 4];

    public int I2cAddress => 0x0E + StackLevel;
}

/// <summary>
/// Sequent Microsystems SM-I-010 (four relays, four isolated inputs) register model, from the vendor's register map
/// (<c>4rel4in.h</c>): 0 relay value, 1 relay set, 2 relay clear (value = relay 1-4), 3 digital inputs, 5 LED value,
/// 6 LED set, 7 LED clear, 8 LED mode (bit per LED, 1 = manual, 0 = auto where the LED follows its input), 0x78-0x7B
/// revision. Every other register is plain memory. Not thread-safe; <see cref="RoofPlant"/> serializes access.
/// </summary>
public sealed class SmI010Board
{
    public const byte RelayValueRegister = 0;
    public const byte RelaySetRegister = 1;
    public const byte RelayClearRegister = 2;
    public const byte DigitalInputRegister = 3;
    public const byte LedValueRegister = 5;
    public const byte LedSetRegister = 6;
    public const byte LedClearRegister = 7;
    public const byte LedModeRegister = 8;
    public const byte RevisionRegister = 0x78;

    private readonly byte[] _memory = new byte[256];
    private readonly Relay[] _relays = new Relay[4];

    public SmI010Board(SmI010Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Revision.Length != 4)
        {
            throw new ArgumentException("The revision is four bytes.", nameof(options));
        }

        Options = options;
        for (var i = 0; i < _relays.Length; i++)
        {
            _relays[i] = new Relay();
        }

        LoadPowerUpState();
    }

    public SmI010Options Options { get; }

    /// <summary>The HAT has power (from the Pi's 5 V). Without it every I2C access fails and every relay drops.</summary>
    public bool Powered { get; private set; } = true;

    /// <summary>Relay bits that read and act as set whatever is written (a firmware or driver fault).</summary>
    public byte StuckOnRelayBits { get; set; }

    /// <summary>Relay bits that stay clear whatever is written.</summary>
    public byte StuckOffRelayBits { get; set; }

    /// <summary>When true, writes to registers 0, 1 and 2 are accepted on the bus but change nothing.</summary>
    public bool IgnoreRelayWrites { get; set; }

    /// <summary>The relay coils the firmware drives: register 0, low four bits.</summary>
    public byte RelayRegister => (byte)(_memory[RelayValueRegister] & 0x0F);

    /// <summary>Opto-isolated input levels, bit 0 = IN1. Set by the plant's circuit each step.</summary>
    public byte InputBits { get; internal set; }

    /// <summary>LEDs as lit: manual-mode LEDs show register 5, auto-mode LEDs show their input.</summary>
    public byte DisplayedLeds
    {
        get
        {
            var manual = _memory[LedModeRegister] & 0x0F;
            return (byte)(((_memory[LedValueRegister] & manual) | (InputBits & ~manual)) & 0x0F);
        }
    }

    /// <summary>Register 8, low four bits: 1 = the LED is under software control.</summary>
    public byte LedModeBits => (byte)(_memory[LedModeRegister] & 0x0F);

    internal bool IsSettled
    {
        get
        {
            for (var i = 0; i < _relays.Length; i++)
            {
                if (!_relays[i].IsSettledFor(Powered && ((RelayRegister >> i) & 1) != 0))
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal event Action<string>? Changed;

    public RelayContactFault GetRelayFault(int relay) => _relays[RelayIndex(relay)].Fault;

    public void SetRelayFault(int relay, RelayContactFault fault) => _relays[RelayIndex(relay)].Fault = fault;

    /// <summary>The contacts of relay 1-4 are closed (COM to NO).</summary>
    public bool IsContactClosed(int relay) => _relays[RelayIndex(relay)].ContactClosed;

    /// <summary>The coil of relay 1-4 is energized.</summary>
    public bool IsCoilEnergized(int relay) => Powered && ((RelayRegister >> RelayIndex(relay)) & 1) != 0;

    /// <summary>Removes or restores HAT power. Power loss drops every relay; power-up loads all-zero registers.</summary>
    public void SetPower(bool powered)
    {
        if (powered == Powered)
        {
            return;
        }

        Powered = powered;
        LoadPowerUpState();
        Changed?.Invoke(powered ? "HAT powered up; registers reset" : "HAT power lost");
    }

    public byte ReadRegister(byte register)
    {
        EnsurePowered();
        return register switch
        {
            RelayValueRegister => RelayRegister,
            DigitalInputRegister => InputBits,
            _ => _memory[register]
        };
    }

    public void WriteRegister(byte register, byte value)
    {
        EnsurePowered();
        switch (register)
        {
            case RelayValueRegister:
                SetRelayRegister(value);
                break;
            case RelaySetRegister:
            case RelayClearRegister:
                if (value is >= 1 and <= 4)
                {
                    var bit = (byte)(1 << (value - 1));
                    SetRelayRegister(register == RelaySetRegister ? (byte)(RelayRegister | bit) : (byte)(RelayRegister & ~bit));
                }

                break;
            case DigitalInputRegister:
                // Read-only.
                break;
            case LedValueRegister:
                _memory[LedValueRegister] = (byte)(value & 0x0F);
                break;
            case LedSetRegister:
            case LedClearRegister:
                if (value is >= 1 and <= 4)
                {
                    var bit = (byte)(1 << (value - 1));
                    _memory[LedValueRegister] = register == LedSetRegister
                        ? (byte)(_memory[LedValueRegister] | bit)
                        : (byte)(_memory[LedValueRegister] & ~bit);
                }

                break;
            case LedModeRegister:
                _memory[LedModeRegister] = (byte)(value & 0x0F);
                break;
            case >= RevisionRegister and < RevisionRegister + 4:
                // Read-only.
                break;
            default:
                _memory[register] = value;
                break;
        }
    }

    internal void StepRelays(TimeSpan dt)
    {
        for (var i = 0; i < _relays.Length; i++)
        {
            var before = _relays[i].ContactClosed;
            _relays[i].Step(dt, Powered && ((RelayRegister >> i) & 1) != 0, Options);
            if (_relays[i].ContactClosed != before)
            {
                Changed?.Invoke($"RLY{i + 1} contact {(_relays[i].ContactClosed ? "closed" : "open")}");
            }
        }
    }

    private void SetRelayRegister(byte value)
    {
        if (IgnoreRelayWrites)
        {
            return;
        }

        var masked = (byte)(((value & ~StuckOffRelayBits) | StuckOnRelayBits) & 0x0F);
        if (masked != RelayRegister)
        {
            Changed?.Invoke($"relay register 0x{RelayRegister:X1} -> 0x{masked:X1}");
        }

        _memory[RelayValueRegister] = masked;
    }

    private void LoadPowerUpState()
    {
        Array.Clear(_memory);
        Options.Revision.CopyTo(_memory, RevisionRegister);
    }

    private void EnsurePowered()
    {
        if (!Powered)
        {
            throw new IOException($"SM-I-010 at 0x{Options.I2cAddress:X2} does not respond (HAT unpowered).");
        }
    }

    private static int RelayIndex(int relay)
        => relay is >= 1 and <= 4 ? relay - 1 : throw new ArgumentOutOfRangeException(nameof(relay), "Relays are numbered 1-4.");

    private sealed class Relay
    {
        private bool _contact;
        private TimeSpan _pending;

        public RelayContactFault Fault { get; set; }

        public bool ContactClosed => Fault switch
        {
            RelayContactFault.Welded => true,
            RelayContactFault.Dead => false,
            _ => _contact
        };

        public bool IsSettledFor(bool coil) => coil == _contact;

        public void Step(TimeSpan dt, bool coil, SmI010Options options)
        {
            if (coil == _contact)
            {
                _pending = TimeSpan.Zero;
                return;
            }

            _pending += dt;
            if (_pending >= (coil ? options.RelayOperateTime : options.RelayReleaseTime))
            {
                _contact = coil;
                _pending = TimeSpan.Zero;
            }
        }
    }
}
