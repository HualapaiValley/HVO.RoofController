using System;
using System.Collections.Generic;

namespace HVO.RoofControllerV4.RPi.Security.Identity;

/// <summary>
/// Configuration section <c>RoofControllerSecurity:Identity</c>: where people, sessions and managed API keys are kept,
/// how long sessions last, and when repeated sign-in failures lock out.
/// </summary>
public sealed class RoofIdentityOptions
{
    /// <summary>Configuration section name, below <see cref="RoofControllerSecurityOptions.SectionName"/>.</summary>
    public const string SectionName = RoofControllerSecurityOptions.SectionName + ":Identity";

    /// <summary>
    /// The identity store file (for example <c>/var/lib/hvo-roof/identity/identity.json</c>). Its directory must exist
    /// and be writable by the controller. When empty, people, sessions and managed keys live only in memory and are
    /// lost when the controller restarts (a warning is logged; fine for development and tests).
    /// </summary>
    public string? StorePath { get; set; }

    /// <summary>How long a name-and-password session lasts. Default 12 hours.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(12);

    /// <summary>A PIN session ends after this long without a request. Default 10 minutes.</summary>
    public TimeSpan PinSessionIdleTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>A PIN session ends after this long even while in use. Default 12 hours.</summary>
    public TimeSpan PinSessionLifetime { get; set; } = TimeSpan.FromHours(12);

    /// <summary>Failed sign-ins for one name, or PIN attempts at one kiosk, before a lockout. Default 5.</summary>
    public int LockoutThreshold { get; set; } = 5;

    /// <summary>The first lockout's length; each further lockout doubles it, up to <see cref="MaximumLockoutDuration"/>. Default 5 minutes.</summary>
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The longest lockout. Default 4 hours.</summary>
    public TimeSpan MaximumLockoutDuration { get; set; } = TimeSpan.FromHours(4);

    /// <summary>
    /// After this long with no failure, a name or kiosk starts again from no failures and no lockouts. Default 24 hours.
    /// </summary>
    public TimeSpan FailureMemory { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Returns the problems with these settings (empty when valid).</summary>
    internal IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        Positive(nameof(SessionLifetime), SessionLifetime);
        Positive(nameof(PinSessionIdleTimeout), PinSessionIdleTimeout);
        Positive(nameof(PinSessionLifetime), PinSessionLifetime);
        Positive(nameof(LockoutDuration), LockoutDuration);
        Positive(nameof(FailureMemory), FailureMemory);
        if (LockoutThreshold < 1)
        {
            problems.Add($"{SectionName}:{nameof(LockoutThreshold)} must be at least 1.");
        }

        if (MaximumLockoutDuration < LockoutDuration)
        {
            problems.Add($"{SectionName}:{nameof(MaximumLockoutDuration)} must be at least {nameof(LockoutDuration)}.");
        }

        if (FailureMemory < MaximumLockoutDuration)
        {
            // Otherwise a lockout could outlast the memory of the failures that caused it.
            problems.Add($"{SectionName}:{nameof(FailureMemory)} must be at least {nameof(MaximumLockoutDuration)}.");
        }

        return problems;

        void Positive(string name, TimeSpan value)
        {
            if (value <= TimeSpan.Zero)
            {
                problems.Add($"{SectionName}:{name} must be positive.");
            }
        }
    }
}
