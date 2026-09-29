using System;
using System.Collections.Generic;
using System.Linq;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Controllers.Camera;
using HVO.RoofControllerV4.RPi.Middleware;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Security.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Settings;

/// <summary>
/// Checks settings groups the way the controller will use them: each setting's own rules, then the rules between
/// settings (the roof validator, the sign-in limits, the camera proxy, the allowed origins). Messages name settings,
/// never values.
/// </summary>
public sealed class RoofSettingsValidator
{
    private readonly IReadOnlyList<IValidateOptions<RoofControllerOptionsV4>> _roofValidators;
    private readonly IHostEnvironment _environment;

    public RoofSettingsValidator(IEnumerable<IValidateOptions<RoofControllerOptionsV4>> roofValidators, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(roofValidators);
        ArgumentNullException.ThrowIfNull(environment);
        _roofValidators = roofValidators.ToList();
        _environment = environment;
    }

    /// <summary>
    /// The problems with <paramref name="groups"/> as <paramref name="view"/> gives them. <paramref name="roof"/> gives
    /// the roof options to check (the roof group is checked as the service would apply it).
    /// </summary>
    internal List<RoofSettingProblem> Validate(IEnumerable<string> groups, RoofConfigurationView view, Func<RoofControllerOptionsV4> roof)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(roof);
        var problems = new List<RoofSettingProblem>();
        IConfiguration? candidate = null;
        foreach (var group in groups.Distinct(StringComparer.Ordinal))
        {
            var settingProblems = new List<RoofSettingProblem>();
            foreach (var definition in RoofSettingsCatalogue.InGroup(group))
            {
                if (!definition.TryRead(view, out _, out var problem))
                {
                    settingProblems.Add(new RoofSettingProblem(definition.Key, problem!));
                }
            }

            if (settingProblems.Count > 0)
            {
                // The rules between settings need every value readable.
                problems.AddRange(settingProblems);
                continue;
            }

            candidate ??= new ConfigurationBuilder().AddInMemoryCollection(view.Flatten()).Build();
            problems.AddRange(ValidateGroup(group, view, candidate, roof));
        }

        return problems;
    }

    /// <summary>The effective RequireHttps as <paramref name="view"/> gives it (unset: on outside Development).</summary>
    internal bool IsHttpsRequired(RoofConfigurationView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var definition = RoofSettingsCatalogue.Find(RequireHttpsKey)!;
        definition.TryRead(view, out var value, out _);
        return value as bool? ?? !_environment.IsDevelopment();
    }

    /// <summary>
    /// The refusal when <paramref name="after"/> turns RequireHttps on without an HTTPS listener, which would lock out
    /// every remote client; otherwise null.
    /// </summary>
    internal string? CheckHttpsLockout(RoofConfigurationView before, RoofConfigurationView after, IConfiguration listeners)
        => !IsHttpsRequired(before) && IsHttpsRequired(after) && !RoofSecurityStartup.IsHttpsConfigured(listeners)
            ? $"{RequireHttpsKey} cannot be turned on: the controller has no HTTPS listener, so every client on another " +
              "host would be refused. Configure HTTPS first (docs/security.md)."
            : null;

    /// <summary>
    /// Refuses to start with a settings file or managed secrets file that sets a setting the controller cannot use.
    /// Only the groups the files set are checked; the rest come from the shipped files and the deployment.
    /// </summary>
    public void ValidateStartup(IConfigurationRoot configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var files = configuration.Providers.OfType<RoofSettingsFileProvider>().ToList();
        var groups = RoofSettingsCatalogue.All
            .Where(definition => files.Any(file => RoofConfigurationView.Sets(file, definition.Key, includeChildren: true)))
            .Select(definition => definition.Group)
            .ToList();
        if (groups.Count == 0)
        {
            return;
        }

        var problems = Validate(groups, new RoofConfigurationView(configuration.Providers), () => BindRoof(configuration));
        if (problems.Count > 0)
        {
            var paths = string.Join(", ", files.Where(file => file.Path is not null).Select(file => $"'{file.Path}'"));
            throw new RoofSettingsFileException(
                $"The controller's settings ({paths}) cannot be used: {string.Join(" ", problems.Select(problem => problem.Message))} " +
                "Fix the file, or move it aside to start from the shipped defaults.");
        }
    }

    internal const string RequireHttpsKey = RoofControllerSecurityOptions.SectionName + ":" + nameof(RoofControllerSecurityOptions.RequireHttps);

    private IEnumerable<RoofSettingProblem> ValidateGroup(string group, RoofConfigurationView view, IConfiguration candidate, Func<RoofControllerOptionsV4> roof)
    {
        switch (group)
        {
            case RoofSettingsContract.RoofGroup:
                RoofControllerOptionsV4 options;
                try
                {
                    options = roof();
                }
                catch (InvalidOperationException)
                {
                    return [new RoofSettingProblem(nameof(RoofControllerOptionsV4),
                        $"A value under {nameof(RoofControllerOptionsV4)} cannot be converted to its setting's type.")];
                }

                return _roofValidators
                    .Select(validator => validator.Validate(Options.DefaultName, options))
                    .Where(result => result.Failed)
                    .SelectMany(result => result.Failures ?? [])
                    .Distinct(StringComparer.Ordinal)
                    .Select(failure => new RoofSettingProblem(nameof(RoofControllerOptionsV4), failure))
                    .ToList();
            case RoofSettingsContract.IdentityGroup:
                return Bind<RoofIdentityOptions>(candidate, RoofIdentityOptions.SectionName, out var identity) is { } identityProblem
                    ? [identityProblem]
                    : identity.Validate().Select(problem => new RoofSettingProblem(RoofIdentityOptions.SectionName, problem)).ToList();
            case RoofSettingsContract.CameraGroup:
                if (Bind<BlueIrisOptions>(candidate, BlueIrisOptions.SectionName, out var camera) is { } cameraProblem)
                {
                    return [cameraProblem];
                }

                return !string.IsNullOrWhiteSpace(camera.BaseUrl) && camera.GetConfigurationProblem() is { } problem
                    ? [new RoofSettingProblem(BlueIrisOptions.SectionName, problem)]
                    : [];
            case RoofSettingsContract.SecurityGroup:
                var originsKey = RoofControllerSecurityOptions.SectionName + ":" + nameof(RoofControllerSecurityOptions.AllowedOrigins);
                var origins = view.GetListItems(originsKey) ?? [];
                return origins
                    .Select((origin, index) => (origin, index))
                    .Where(entry => !OriginCheckMiddleware.IsValidAllowedOrigin(entry.origin))
                    .Select(entry => new RoofSettingProblem(
                        originsKey,
                        $"{originsKey} entry {entry.index + 1} is not an origin: write scheme://host[:port], for example " +
                        "https://roof.example.org, with nothing after it."))
                    .ToList();
            default:
                // Controller, logging and client settings have no rules between them.
                return [];
        }
    }

    private static RoofControllerOptionsV4 BindRoof(IConfiguration configuration)
    {
        var options = new RoofControllerOptionsV4();
        configuration.GetSection(nameof(RoofControllerOptionsV4)).Bind(options);
        return options;
    }

    /// <summary>Binds a section; the problem when a value the catalogue does not cover cannot be converted.</summary>
    private static RoofSettingProblem? Bind<T>(IConfiguration configuration, string section, out T options)
        where T : new()
    {
        options = new T();
        try
        {
            configuration.GetSection(section).Bind(options);
            return null;
        }
        catch (InvalidOperationException)
        {
            // The binder's message can quote the value.
            return new RoofSettingProblem(section, $"A value under {section} cannot be converted to its setting's type.");
        }
    }
}
