using System;
using System.Collections.Generic;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.RPi.Logic;

/// <summary>
/// Failure returned by <see cref="IRoofControllerServiceV4"/> commands. <see cref="Code"/> tells callers whether the
/// command was refused by an interlock (409), could not run (503) or left relay state unverified (503).
/// </summary>
public sealed class RoofControllerException : InvalidOperationException
{
    public RoofControllerException(RoofControllerErrorCode code, string message, RoofStatusResponse? snapshot = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Snapshot = snapshot;
    }

    /// <summary>Machine-readable failure category.</summary>
    public RoofControllerErrorCode Code { get; }

    /// <summary>Coherent controller snapshot taken when the failure was produced, when available.</summary>
    public RoofStatusResponse? Snapshot { get; }

    /// <summary>
    /// For <see cref="RoofControllerErrorCode.AlpacaCommandRefused"/> and <see cref="RoofControllerErrorCode.Superseded"/>
    /// of an Alpaca command: the rule that refused it (a stable name such as <c>CommandsOff</c> or <c>NotSafe</c>, the
    /// problem's <c>alpacaRefusal</c> extension); null otherwise.
    /// </summary>
    public string? AlpacaRefusal { get; init; }

    /// <summary>The safety reasons' stable keys (kind and source) behind a policy refusal; empty otherwise.</summary>
    public IReadOnlyList<string> ReasonKeys { get; init; } = [];

    /// <summary>This failure naming <paramref name="rule"/> as its <see cref="AlpacaRefusal"/>.</summary>
    public RoofControllerException WithRule(string rule)
        => new(Code, Message, Snapshot, InnerException) { AlpacaRefusal = rule, ReasonKeys = ReasonKeys };

    /// <summary>This failure with <paramref name="snapshot"/> as its snapshot.</summary>
    public RoofControllerException WithSnapshot(RoofStatusResponse? snapshot)
        => new(Code, Message, snapshot, InnerException) { AlpacaRefusal = AlpacaRefusal, ReasonKeys = ReasonKeys };
}
