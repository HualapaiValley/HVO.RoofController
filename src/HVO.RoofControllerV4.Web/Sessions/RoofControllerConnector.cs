using HVO.RoofControllerV4.Client;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.Web.Sessions;

/// <summary>
/// Makes the web UI's clients of the controller: the shared anonymous ones (readiness, sign-in) and one per signed-in
/// person. Tests replace it to reach a stub or an in-memory controller.
/// </summary>
public class RoofControllerConnector(IOptions<RoofWebOptions> options, ILoggerFactory loggerFactory, TimeProvider time)
{
    /// <summary>A client of the controller with <paramref name="credential"/> (none: anonymous).</summary>
    public virtual RoofControllerClient Create(RoofCredential? credential, TimeSpan requestTimeout) => new(new RoofConnectionOptions
    {
        BaseAddress = options.Value.ControllerUrl,
        Credential = credential,
        RequestTimeout = requestTimeout,
        TimeProvider = time,
        LoggerFactory = loggerFactory,
    });
}
