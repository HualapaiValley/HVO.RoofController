using HVO.RoofControllerV4.Common.Models;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Safety;

/// <summary>Who may override the safety interlock in <see cref="RoofSafetyInterlockMode.Warn"/>.</summary>
public enum RoofSafetyOverrideRole
{
    /// <summary>Only a caller with the Admin role (and, for a scoped key, the configure scope).</summary>
    Admin = 0,

    /// <summary>Any caller who may open the roof (the Operator role).</summary>
    Operator = 1
}

/// <summary>
/// The safety service (<c>RoofSafety</c> section): how the sources are read and how Open treats the result
/// (docs/safety-service.md). Read from the controller's configuration; not in the settings catalogue yet.
/// </summary>
public sealed class RoofSafetyOptions
{
    public const string SectionName = "RoofSafety";

    public static readonly TimeSpan MinPollInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxPollInterval = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MinReadTimeout = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan MaxReadTimeout = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxMaxReadingAge = TimeSpan.FromHours(1);

    /// <summary>What Open does with the safety state. Off (the default) only reports it.</summary>
    public RoofSafetyInterlockMode InterlockMode { get; set; } = RoofSafetyInterlockMode.Off;

    /// <summary>Who may override in <see cref="RoofSafetyInterlockMode.Warn"/>. Admin by default.</summary>
    public RoofSafetyOverrideRole OverrideRole { get; set; } = RoofSafetyOverrideRole.Admin;

    /// <summary>How often each source is read (1 s to 10 min).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long one read may take before it counts as failed (0.1 s to 1 min).</summary>
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How old a reading may be and still count, for a source that does not set its own (more than
    /// <see cref="PollInterval"/> plus <see cref="ReadTimeout"/>, the longest gap between two reads; at most 1 h). An older
    /// reading makes its source Unknown.
    /// </summary>
    public TimeSpan MaxReadingAge { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>Refuses <see cref="RoofSafetyOptions"/> the service cannot use, so the controller does not start with them.</summary>
public sealed class RoofSafetyOptionsValidator : IValidateOptions<RoofSafetyOptions>
{
    public ValidateOptionsResult Validate(string? name, RoofSafetyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (!Enum.IsDefined(options.InterlockMode))
        {
            failures.Add($"{RoofSafetyOptions.SectionName}:{nameof(RoofSafetyOptions.InterlockMode)} must be Off, Warn or Block.");
        }

        if (!Enum.IsDefined(options.OverrideRole))
        {
            failures.Add($"{RoofSafetyOptions.SectionName}:{nameof(RoofSafetyOptions.OverrideRole)} must be Admin or Operator.");
        }

        if (options.PollInterval < RoofSafetyOptions.MinPollInterval || options.PollInterval > RoofSafetyOptions.MaxPollInterval)
        {
            failures.Add($"{RoofSafetyOptions.SectionName}:{nameof(RoofSafetyOptions.PollInterval)} must be between 1 s and 10 min.");
        }

        if (options.ReadTimeout < RoofSafetyOptions.MinReadTimeout || options.ReadTimeout > RoofSafetyOptions.MaxReadTimeout)
        {
            failures.Add($"{RoofSafetyOptions.SectionName}:{nameof(RoofSafetyOptions.ReadTimeout)} must be between 0.1 s and 1 min.");
        }

        if (options.MaxReadingAge <= options.PollInterval + options.ReadTimeout || options.MaxReadingAge > RoofSafetyOptions.MaxMaxReadingAge)
        {
            failures.Add($"{RoofSafetyOptions.SectionName}:{nameof(RoofSafetyOptions.MaxReadingAge)} must be longer than {nameof(RoofSafetyOptions.PollInterval)} plus {nameof(RoofSafetyOptions.ReadTimeout)} and at most 1 h, or a reading goes stale before the next one.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
