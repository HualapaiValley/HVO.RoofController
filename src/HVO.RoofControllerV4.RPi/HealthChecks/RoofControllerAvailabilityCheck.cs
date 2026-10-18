using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Services.HatEmulation;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.HealthChecks;

/// <summary>
/// Software availability for deployment and Docker. Shares operational health's I/O and lifecycle checks, but a
/// classified plant fault with verified all-off relays is Degraded. Healthy motion remains available; the deploy
/// script's authenticated Stop, separately, must verify all-off before a controller is replaced.
/// </summary>
public sealed class RoofControllerAvailabilityCheck(
    IRoofControllerServiceV4 roofController,
    ILogger<RoofControllerHealthCheck> logger,
    IOptions<RoofControllerOptionsV4> options,
    RoofHatConnection? hatConnection = null)
    : RoofControllerHealthCheck(roofController, logger, options, hatConnection, availabilityOnly: true);
