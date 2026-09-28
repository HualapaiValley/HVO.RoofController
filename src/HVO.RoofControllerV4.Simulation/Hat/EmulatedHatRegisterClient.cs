using System.Buffers.Binary;
using System.Device.I2c;
using HVO.Iot.Devices.Abstractions;

namespace HVO.RoofControllerV4.Simulation.Hat;

/// <summary>
/// <see cref="II2cRegisterClient"/> backed by a <see cref="RoofPlant"/>'s SM-I-010 model, for use with the real
/// <c>FourRelayFourInputHat</c>. Each access brings the plant up to the current time first, then reads or writes the
/// register under the plant lock. Bus failures can be injected; they surface as <see cref="IOException"/>, as a failed
/// I2C transfer does. With an <see cref="EmulatedBusTiming"/> each access also takes its bus time, as the library's
/// <c>I2cRegisterClient</c> does: the transfer, then (after a successful one) the post-transaction pause. The calling
/// thread spends that time through the wait it is given, outside the plant lock.
/// </summary>
/// <remarks>
/// The HAT library treats a client that is not a <c>MemoryI2cRegisterClient</c> as hardware backed, so the controller
/// takes its physical-hardware paths with this client.
/// </remarks>
public sealed class EmulatedHatRegisterClient : II2cRegisterClient
{
    private readonly RoofPlant _plant;
    private readonly EmulatedBusTiming _timing;
    private readonly Action<TimeSpan> _wait;
    private Func<HatBusAccess, bool>? _failWhen;
    private long _injectedFailures;
    private bool _failReads;
    private bool _failWrites;
    private bool _failInputReads;
    private int _failNextReads;
    private int _failNextWrites;
    private int _failNextInputReads;
    private long _reads;
    private long _writes;
    private bool _disposed;

    /// <param name="plant">The plant whose HAT answers.</param>
    /// <param name="busId">The I2C bus reported in <see cref="ConnectionSettings"/>.</param>
    /// <param name="timing">The bus time each access takes; <see cref="EmulatedBusTiming.Instant"/> when null.</param>
    /// <param name="wait">
    /// How the calling thread spends bus time: a manual clock advances, a real-time host sleeps. <see cref="Thread.Sleep(TimeSpan)"/>
    /// when null.
    /// </param>
    public EmulatedHatRegisterClient(RoofPlant plant, int busId = 1, EmulatedBusTiming? timing = null, Action<TimeSpan>? wait = null)
    {
        ArgumentNullException.ThrowIfNull(plant);
        _plant = plant;
        _timing = timing ?? EmulatedBusTiming.Instant;
        _timing.Validate();
        _wait = wait ?? Thread.Sleep;
        ConnectionSettings = new I2cConnectionSettings(busId, plant.Hat.Options.I2cAddress);
    }

    public I2cConnectionSettings ConnectionSettings { get; }

    public object SyncRoot { get; } = new();

    public RoofPlant Plant => _plant;

    public EmulatedBusTiming Timing => _timing;

    /// <summary>
    /// Fails each transaction this returns true for, in addition to the other settings. It runs under the plant lock,
    /// once per transaction that the other settings let through.
    /// </summary>
    public Func<HatBusAccess, bool>? FailWhen
    {
        get { lock (_plant.SyncRoot) { return _failWhen; } }
        set { lock (_plant.SyncRoot) { _failWhen = value; } }
    }

    /// <summary>The transactions that failed by injection so far.</summary>
    public long InjectedFailures
    {
        get { lock (_plant.SyncRoot) { return _injectedFailures; } }
    }

    /// <summary>Every read fails.</summary>
    public bool FailReads
    {
        get { lock (_plant.SyncRoot) { return _failReads; } }
        set { lock (_plant.SyncRoot) { _failReads = value; } }
    }

    /// <summary>Every write fails.</summary>
    public bool FailWrites
    {
        get { lock (_plant.SyncRoot) { return _failWrites; } }
        set { lock (_plant.SyncRoot) { _failWrites = value; } }
    }

    /// <summary>Every read of the input register (3) fails.</summary>
    public bool FailInputReads
    {
        get { lock (_plant.SyncRoot) { return _failInputReads; } }
        set { lock (_plant.SyncRoot) { _failInputReads = value; } }
    }

    /// <summary>The next this-many reads fail.</summary>
    public int FailNextReads
    {
        get { lock (_plant.SyncRoot) { return _failNextReads; } }
        set { lock (_plant.SyncRoot) { _failNextReads = value; } }
    }

    /// <summary>The next this-many writes fail.</summary>
    public int FailNextWrites
    {
        get { lock (_plant.SyncRoot) { return _failNextWrites; } }
        set { lock (_plant.SyncRoot) { _failNextWrites = value; } }
    }

    /// <summary>The next this-many reads of the input register (3) fail.</summary>
    public int FailNextInputReads
    {
        get { lock (_plant.SyncRoot) { return _failNextInputReads; } }
        set { lock (_plant.SyncRoot) { _failNextInputReads = value; } }
    }

    public long ReadCount
    {
        get { lock (_plant.SyncRoot) { return _reads; } }
    }

    public long WriteCount
    {
        get { lock (_plant.SyncRoot) { return _writes; } }
    }

    public byte ReadByte(byte register)
    {
        Span<byte> buffer = stackalloc byte[1];
        ReadBlock(register, buffer);
        return buffer[0];
    }

    public ushort ReadUInt16(byte register)
    {
        Span<byte> buffer = stackalloc byte[2];
        ReadBlock(register, buffer);
        return BinaryPrimitives.ReadUInt16LittleEndian(buffer);
    }

    public uint ReadUInt32(byte register)
    {
        Span<byte> buffer = stackalloc byte[4];
        ReadBlock(register, buffer);
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    public void ReadBlock(byte register, Span<byte> destination)
    {
        Transfer(register, destination.Length, read: true);
        lock (_plant.SyncRoot)
        {
            BeginAccess(new HatBusAccess(register, destination.Length, IsRead: true, Value: null));
            for (var i = 0; i < destination.Length; i++)
            {
                destination[i] = _plant.Hat.ReadRegister((byte)(register + i));
            }
        }

        PostTransactionDelay();
    }

    public void WriteByte(byte register, byte value)
    {
        ReadOnlySpan<byte> buffer = [value];
        WriteBlock(register, buffer);
    }

    public void WriteUInt16(byte register, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        WriteBlock(register, buffer);
    }

    public void WriteBlock(byte register, ReadOnlySpan<byte> data)
    {
        Transfer(register, data.Length, read: false);
        lock (_plant.SyncRoot)
        {
            BeginAccess(new HatBusAccess(register, data.Length, IsRead: false, data.IsEmpty ? null : data[0]));
            for (var i = 0; i < data.Length; i++)
            {
                _plant.Hat.WriteRegister((byte)(register + i), data[i]);
            }
        }

        PostTransactionDelay();
    }

    public void Dispose() => _disposed = true;

    /// <summary>The transfer's bus time, spent before the register is read or written (the device acts at its end).</summary>
    private void Transfer(byte register, int length, bool read)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (register + length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The access runs past register 0xFF.");
        }

        Wait(_timing.TransferTime(length, read));
    }

    /// <summary>The library pauses only after a transaction that succeeded.</summary>
    private void PostTransactionDelay() => Wait(_timing.PostTransactionDelay);

    private void Wait(TimeSpan duration)
    {
        if (duration > TimeSpan.Zero)
        {
            _wait(duration);
        }
    }

    private void BeginAccess(HatBusAccess access)
    {
        _plant.Sync();
        bool fail;
        if (access.IsRead)
        {
            _reads++;
            var input = access.Covers(SmI010Board.DigitalInputRegister);
            fail = _failReads || Consume(ref _failNextReads) || (input && (_failInputReads || Consume(ref _failNextInputReads)));
        }
        else
        {
            _writes++;
            fail = _failWrites || Consume(ref _failNextWrites);
        }

        if (!fail && _failWhen is { } failWhen)
        {
            fail = failWhen(access);
        }

        if (fail)
        {
            _injectedFailures++;
            throw new IOException($"Injected I2C {(access.IsRead ? "read" : "write")} failure at register {access.Register}.");
        }
    }

    private static bool Consume(ref int counter)
    {
        if (counter <= 0)
        {
            return false;
        }

        counter--;
        return true;
    }
}
