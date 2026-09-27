using System.Buffers.Binary;
using System.Device.I2c;
using HVO.Iot.Devices.Abstractions;

namespace HVO.RoofControllerV4.Simulation.Hat;

/// <summary>
/// <see cref="II2cRegisterClient"/> backed by a <see cref="RoofPlant"/>'s SM-I-010 model, for use with the real
/// <c>FourRelayFourInputHat</c>. Each access brings the plant up to the current time first, then reads or writes the
/// register under the plant lock. Bus failures can be injected; they surface as <see cref="IOException"/>, as a failed
/// I2C transfer does.
/// </summary>
/// <remarks>
/// The HAT library treats a client that is not a <c>MemoryI2cRegisterClient</c> as hardware backed, so the controller
/// takes its physical-hardware paths with this client.
/// </remarks>
public sealed class EmulatedHatRegisterClient : II2cRegisterClient
{
    private readonly RoofPlant _plant;
    private bool _failReads;
    private bool _failWrites;
    private bool _failInputReads;
    private int _failNextReads;
    private int _failNextWrites;
    private int _failNextInputReads;
    private long _reads;
    private long _writes;
    private bool _disposed;

    public EmulatedHatRegisterClient(RoofPlant plant, int busId = 1)
    {
        ArgumentNullException.ThrowIfNull(plant);
        _plant = plant;
        ConnectionSettings = new I2cConnectionSettings(busId, plant.Hat.Options.I2cAddress);
    }

    public I2cConnectionSettings ConnectionSettings { get; }

    public object SyncRoot { get; } = new();

    public RoofPlant Plant => _plant;

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
        lock (_plant.SyncRoot)
        {
            BeginAccess(register, destination.Length, read: true);
            for (var i = 0; i < destination.Length; i++)
            {
                destination[i] = _plant.Hat.ReadRegister((byte)(register + i));
            }
        }
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
        lock (_plant.SyncRoot)
        {
            BeginAccess(register, data.Length, read: false);
            for (var i = 0; i < data.Length; i++)
            {
                _plant.Hat.WriteRegister((byte)(register + i), data[i]);
            }
        }
    }

    public void Dispose() => _disposed = true;

    private void BeginAccess(byte register, int length, bool read)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (register + length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The access runs past register 0xFF.");
        }

        _plant.Sync();
        if (read)
        {
            _reads++;
            var input = register <= SmI010Board.DigitalInputRegister && register + length > SmI010Board.DigitalInputRegister;
            if (_failReads || Consume(ref _failNextReads) || (input && (_failInputReads || Consume(ref _failNextInputReads))))
            {
                throw new IOException($"Injected I2C read failure at register {register}.");
            }
        }
        else
        {
            _writes++;
            if (_failWrites || Consume(ref _failNextWrites))
            {
                throw new IOException($"Injected I2C write failure at register {register}.");
            }
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
