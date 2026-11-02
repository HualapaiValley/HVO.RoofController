using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Alpaca;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>
/// The UIs' tests of Alpaca Open grants (#220, phase 4) against the controller in process: its settings for Alpaca Open
/// and Close, Block mode, and a change of setting at run time, as a reload of its configuration makes one.
/// </summary>
internal static class AlpacaOpenUiSupport
{
    /// <summary>
    /// Alpaca Open and Close on (no telescope set to block the roof, no weather source); with <paramref name="block"/>, the
    /// safety interlock Block, in which no Open grant can be made (<see cref="RoofAlpacaCommands.OpenGrantsBlocked"/>).
    /// </summary>
    public static Dictionary<string, string?> OpenAndClose(bool block = false)
    {
        var settings = new Dictionary<string, string?> { [$"{RoofAlpacaCommandOptions.SectionName}:AllowCommands"] = nameof(RoofAlpacaCommandMode.OpenAndClose) };
        if (block)
        {
            settings[RoofApiTestHost.SafetyInterlockModeKey] = nameof(RoofSafetyInterlockMode.Block);
        }

        return settings;
    }

    /// <summary>Changes one of the controller's settings at run time, as a reload of its configuration does.</summary>
    public static void Reconfigure(this IServiceProvider services, string key, string value)
    {
        var configuration = (IConfigurationRoot)services.GetRequiredService<IConfiguration>();
        configuration[key] = value;
        configuration.Reload();
    }

    /// <summary>
    /// The safety interlock turns to Block just before the controller reads the first grant request: the status the UI
    /// read before says nothing of it, so the controller refuses the grant (403, <c>BlockMode</c>).
    /// </summary>
    public static void BlockBeforeTheGrant(IServiceProvider services, HttpRequestMessage request)
    {
        if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/" + RoofAlpacaContract.GrantsRoute, StringComparison.Ordinal))
        {
            services.Reconfigure(RoofApiTestHost.SafetyInterlockModeKey, nameof(RoofSafetyInterlockMode.Block));
        }
    }

    /// <summary>A handler that calls <paramref name="before"/> with each request before it is sent.</summary>
    public sealed class BeforeSending(HttpMessageHandler inner, Action<HttpRequestMessage> before) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            before(request);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
