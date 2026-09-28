using HVO.RoofControllerV4.Simulation.Camera;
using Microsoft.AspNetCore.Http.Features;

namespace HVO.RoofControllerV4.Emulator;

/// <summary>
/// Serves the <see cref="EmulatedCamera"/> on the Blue Iris MJPEG path, so the controller's camera proxy
/// (<c>BlueIris:BaseUrl</c>) can point at the emulator. Like the control API it has no authentication, and it ignores
/// the proxy's Basic credentials.
/// </summary>
public static class EmulatedCameraEndpoint
{
    public static IEndpointConventionBuilder Map(IEndpointRouteBuilder endpoints)
        => endpoints.MapGet(EmulatedCamera.Route, async (int camera, HttpContext context, EmulatedCamera source) =>
        {
            var response = context.Response;
            response.StatusCode = source.AnswerStatusCode;
            if (response.StatusCode == StatusCodes.Status401Unauthorized)
            {
                response.Headers.WWWAuthenticate = "Basic realm=\"emulated camera\"";
                return;
            }

            if (response.StatusCode != StatusCodes.Status200OK)
            {
                return;
            }

            response.ContentType = EmulatedCamera.ContentType;
            response.Headers.CacheControl = "no-cache, no-store";
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await response.StartAsync(context.RequestAborted).ConfigureAwait(false);
            await source.StreamAsync(camera, response.Body, context.RequestAborted).ConfigureAwait(false);
        });
}
