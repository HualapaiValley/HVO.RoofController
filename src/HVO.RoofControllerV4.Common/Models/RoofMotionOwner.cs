namespace HVO.RoofControllerV4.Common.Models;

/// <summary>Whose a motion is: what admitted it, and so what may later change it (#220).</summary>
public enum RoofMotionOwnerKind
{
    /// <summary>
    /// A command that came with no person behind it (an API key that names no one, a local or physical input). It counts
    /// as a person's: the Alpaca server never reverses it.
    /// </summary>
    Unleased = 0,

    /// <summary>A person's command: a password or PIN session, or a person's own API key.</summary>
    Person = 1,

    /// <summary>An Alpaca client's command, admitted under the External Alpaca account's permanent lease.</summary>
    Alpaca = 2
}

/// <summary>Who a motion belongs to: the kind, and the person's or account's name as it was shown when admitted.</summary>
/// <param name="Kind">What admitted the motion.</param>
/// <param name="Name">The person's or the API key's name; null when none is known.</param>
public sealed record RoofMotionOwner(RoofMotionOwnerKind Kind, string? Name = null)
{
    /// <summary>A motion with no person or Alpaca account behind it.</summary>
    public static RoofMotionOwner Unleased { get; } = new(RoofMotionOwnerKind.Unleased);

    /// <summary>True when the Alpaca server may reverse or join this motion: only its own.</summary>
    public bool IsAlpaca => Kind == RoofMotionOwnerKind.Alpaca;
}
