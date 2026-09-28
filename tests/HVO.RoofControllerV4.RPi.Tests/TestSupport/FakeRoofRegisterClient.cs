using System;
using System.Collections.Generic;
using System.Device.I2c;
using System.IO;
using HVO.Iot.Devices.Abstractions;
using HVO.Iot.Devices.Implementation;
using HVO.Iot.Devices.Iot.Devices.Sequent;

namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

/// <summary>
/// In-memory register client for the four-relay / four-input HAT with fault injection. Delegates register semantics to
/// the library's <see cref="FourRelayFourInputHatMemoryClient"/> and records the relay register after every relay write.
/// </summary>
/// <remarks>
/// Registers: 0 = relay mask (read/write), 1 = set relay (value = relay index), 2 = clear relay, 3 = digital inputs.
/// </remarks>
internal sealed class FakeRoofRegisterClient : MemoryI2cRegisterClient
{
    private const byte RelayMaskRegister = 0;
    private const byte RelaySetRegister = 1;
    private const byte RelayClearRegister = 2;
    private const byte InputRegister = 3;
    private const byte LedModeRegister = 8;

    private readonly FourRelayFourInputHatMemoryClient _inner = new();
    private readonly object _gate = new();
    private readonly List<byte> _maskHistory = new();
    private bool _everBothDirectionBits;
    private bool _failRelayWrites;
    private int _failNextRelayWrites;
    private bool _failRelayReads;
    private bool _failLedModeWrites;
    private int _ledModeWriteAttempts;
    private bool _failInputReads;
    private int _failNextInputReads;
    private byte _stuckRelayBits;
    private byte _ignoredSetRelayBits;
    private int _inputReadCount;

    public FakeRoofRegisterClient() : base(1, 14, 256)
    {
    }

    /// <summary>When true, every write to the relay registers (0, 1, 2) throws.</summary>
    public bool FailRelayWrites
    {
        get { lock (_gate) { return _failRelayWrites; } }
        set { lock (_gate) { _failRelayWrites = value; } }
    }

    /// <summary>Number of upcoming relay-register writes that throw.</summary>
    public int FailNextRelayWrites
    {
        get { lock (_gate) { return _failNextRelayWrites; } }
        set { lock (_gate) { _failNextRelayWrites = value; } }
    }

    /// <summary>When true, reading the relay register (read-back) throws.</summary>
    public bool FailRelayReads
    {
        get { lock (_gate) { return _failRelayReads; } }
        set { lock (_gate) { _failRelayReads = value; } }
    }

    /// <summary>When true, every write to the LED mode register (8) throws.</summary>
    public bool FailLedModeWrites
    {
        get { lock (_gate) { return _failLedModeWrites; } }
        set { lock (_gate) { _failLedModeWrites = value; } }
    }

    /// <summary>Number of writes to the LED mode register (8), including failed ones.</summary>
    public int LedModeWriteAttempts
    {
        get { lock (_gate) { return _ledModeWriteAttempts; } }
    }

    /// <summary>When true, reading the digital input register throws.</summary>
    public bool FailInputReads
    {
        get { lock (_gate) { return _failInputReads; } }
        set { lock (_gate) { _failInputReads = value; } }
    }

    /// <summary>Number of upcoming input-register reads that throw.</summary>
    public int FailNextInputReads
    {
        get { lock (_gate) { return _failNextInputReads; } }
        set { lock (_gate) { _failNextInputReads = value; } }
    }

    /// <summary>Bits OR'd into every relay register read (a relay stuck on in the register).</summary>
    public byte StuckRelayBits
    {
        get { lock (_gate) { return _stuckRelayBits; } }
        set { lock (_gate) { _stuckRelayBits = value; } }
    }

    /// <summary>Relay bits whose "set" commands are accepted but silently have no effect.</summary>
    public byte IgnoredSetRelayBits
    {
        get { lock (_gate) { return _ignoredSetRelayBits; } }
        set { lock (_gate) { _ignoredSetRelayBits = value; } }
    }

    /// <summary>Invoked (outside the client lock) after each relay-register write with the register, value and resulting mask.</summary>
    public Action<byte, byte, byte>? RelayWriteObserver { get; set; }

    /// <summary>Number of digital input register reads attempted.</summary>
    public int InputReadCount
    {
        get { lock (_gate) { return _inputReadCount; } }
    }

    public IReadOnlyList<(byte Register, byte Value)> RelayWriteLog
    {
        get { lock (_gate) { return _inner.RelayWriteLog.ToArray(); } }
    }

    public byte RelayMask
    {
        get { lock (_gate) { return _inner.RelayMask; } }
    }

    public byte LedMask
    {
        get { lock (_gate) { return _inner.LedMask; } }
    }

    /// <summary>LED mode register (8): a set bit puts that LED under manual control. Set it to emulate a HAT reset.</summary>
    public byte LedModeMask
    {
        get { lock (_gate) { return _inner.ReadByte(LedModeRegister); } }
        set { lock (_gate) { _inner.WriteByte(LedModeRegister, value); } }
    }

    /// <summary>Relay register value after each relay write, in order.</summary>
    public IReadOnlyList<byte> MaskHistory
    {
        get { lock (_gate) { return _maskHistory.ToArray(); } }
    }

    /// <summary>True if the relay register ever held both direction bits (relays 1 and 2) at once.</summary>
    public bool EverBothDirectionBits
    {
        get { lock (_gate) { return _everBothDirectionBits; } }
    }

    public void ClearRelayWriteLog()
    {
        lock (_gate)
        {
            _inner.ClearRelayWriteLog();
        }
    }

    /// <summary>Clears the mask history and the <see cref="EverBothDirectionBits"/> flag.</summary>
    public void ClearMaskHistory()
    {
        lock (_gate)
        {
            _maskHistory.Clear();
            _everBothDirectionBits = false;
        }
    }

    public void SetDigitalInputs(bool in1, bool in2, bool in3, bool in4)
    {
        lock (_gate)
        {
            _inner.SetDigitalInputs(in1, in2, in3, in4);
        }
    }

    public override byte ReadByte(byte register)
    {
        lock (_gate)
        {
            ThrowIfReadFails_NoLock(register);
            var value = _inner.ReadByte(register);
            return register == RelayMaskRegister ? (byte)(value | _stuckRelayBits) : value;
        }
    }

    public override ushort ReadUInt16(byte register)
    {
        lock (_gate)
        {
            ThrowIfReadFails_NoLock(register);
            return _inner.ReadUInt16(register);
        }
    }

    public override uint ReadUInt32(byte register)
    {
        lock (_gate)
        {
            ThrowIfReadFails_NoLock(register);
            return _inner.ReadUInt32(register);
        }
    }

    public override void ReadBlock(byte register, Span<byte> destination)
    {
        lock (_gate)
        {
            ThrowIfReadFails_NoLock(register);
            _inner.ReadBlock(register, destination);
            if (register == RelayMaskRegister && destination.Length > 0)
            {
                destination[0] |= _stuckRelayBits;
            }
        }
    }

    public override void WriteByte(byte register, byte value)
    {
        byte? mask = null;
        Action<byte, byte, byte>? observer;
        lock (_gate)
        {
            observer = RelayWriteObserver;
            if (IsRelayRegister(register))
            {
                ThrowIfRelayWriteFails_NoLock();
                if (register == RelaySetRegister && value is >= 1 and <= 4 && (_ignoredSetRelayBits & (1 << (value - 1))) != 0)
                {
                    RecordMask_NoLock();
                    mask = _inner.RelayMask;
                }
                else
                {
                    _inner.WriteByte(register, value);
                    RecordMask_NoLock();
                    mask = _inner.RelayMask;
                }
            }
            else
            {
                if (register == LedModeRegister)
                {
                    _ledModeWriteAttempts++;
                    if (_failLedModeWrites)
                    {
                        throw new IOException("Simulated LED mode register write failure");
                    }
                }

                _inner.WriteByte(register, value);
            }
        }

        if (mask is { } m)
        {
            observer?.Invoke(register, value, m);
        }
    }

    public override void WriteUInt16(byte register, ushort value)
    {
        lock (_gate)
        {
            if (IsRelayRegister(register))
            {
                ThrowIfRelayWriteFails_NoLock();
            }

            _inner.WriteUInt16(register, value);
            if (IsRelayRegister(register))
            {
                RecordMask_NoLock();
            }
        }
    }

    public override void WriteBlock(byte register, ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (IsRelayRegister(register))
            {
                ThrowIfRelayWriteFails_NoLock();
            }

            _inner.WriteBlock(register, data);
            if (IsRelayRegister(register))
            {
                RecordMask_NoLock();
            }
        }
    }

    private static bool IsRelayRegister(byte register) => register is RelayMaskRegister or RelaySetRegister or RelayClearRegister;

    private void ThrowIfReadFails_NoLock(byte register)
    {
        if (register == RelayMaskRegister && _failRelayReads)
        {
            throw new IOException("Simulated relay register read failure");
        }

        if (register == InputRegister)
        {
            _inputReadCount++;
            if (_failInputReads)
            {
                throw new IOException("Simulated input register read failure");
            }

            if (_failNextInputReads > 0)
            {
                _failNextInputReads--;
                throw new IOException("Simulated transient input register read failure");
            }
        }
    }

    private void ThrowIfRelayWriteFails_NoLock()
    {
        if (_failRelayWrites)
        {
            throw new IOException("Simulated relay register write failure");
        }

        if (_failNextRelayWrites > 0)
        {
            _failNextRelayWrites--;
            throw new IOException("Simulated transient relay register write failure");
        }
    }

    private void RecordMask_NoLock()
    {
        var mask = (byte)(_inner.RelayMask | _stuckRelayBits);
        _maskHistory.Add(mask);
        if ((mask & 0x03) == 0x03)
        {
            _everBothDirectionBits = true;
        }
    }
}

/// <summary>
/// Wraps a <see cref="FakeRoofRegisterClient"/> behind the plain register-client interface so the HAT reports
/// <c>IsHardwareBacked == true</c>. Used to test physical-hardware-only rules without hardware.
/// </summary>
internal sealed class HardwareLikeRegisterClient : II2cRegisterClient
{
    private readonly FakeRoofRegisterClient _inner;

    public HardwareLikeRegisterClient(FakeRoofRegisterClient inner)
    {
        _inner = inner;
    }

    public I2cConnectionSettings ConnectionSettings => _inner.ConnectionSettings;

    public object SyncRoot => _inner.SyncRoot;

    public byte ReadByte(byte register) => _inner.ReadByte(register);

    public ushort ReadUInt16(byte register) => _inner.ReadUInt16(register);

    public uint ReadUInt32(byte register) => _inner.ReadUInt32(register);

    public void ReadBlock(byte register, Span<byte> destination) => _inner.ReadBlock(register, destination);

    public void WriteByte(byte register, byte value) => _inner.WriteByte(register, value);

    public void WriteUInt16(byte register, ushort value) => _inner.WriteUInt16(register, value);

    public void WriteBlock(byte register, ReadOnlySpan<byte> data) => _inner.WriteBlock(register, data);

    public void Dispose() => _inner.Dispose();
}
