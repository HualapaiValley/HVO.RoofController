using System;
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
}
