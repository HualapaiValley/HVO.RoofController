using HVO.RoofControllerV4.RPi.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Tests.Security;

/// <summary>Options monitor whose value can be replaced to simulate a configuration reload.</summary>
internal sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
{
    private readonly List<Action<T, string?>> _listeners = new();

    public TestOptionsMonitor(T value) => CurrentValue = value;

    public T CurrentValue { get; private set; }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener)
    {
        _listeners.Add(listener);
        return new Registration(() => _listeners.Remove(listener));
    }

    public void Set(T value)
    {
        CurrentValue = value;
        foreach (var listener in _listeners.ToArray())
        {
            listener(value, Options.DefaultName);
        }
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

/// <summary>Time provider whose clock only moves when told to.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    public ManualTimeProvider(DateTimeOffset now) => Now = now;

    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;
}

internal static class KeyStoreFactory
{
    public static RoofApiKeyStore Create(TestOptionsMonitor<RoofControllerSecurityOptions> monitor)
        => new(monitor, NullLogger<RoofApiKeyStore>.Instance);

    public static TestOptionsMonitor<RoofControllerSecurityOptions> Monitor(params RoofApiKeyOptions[] keys)
        => new(new RoofControllerSecurityOptions { ApiKeys = keys.ToList() });

    public static RoofApiKeyOptions Key(string name, string role, string key) => new() { Name = name, Role = role, Key = key };
}
