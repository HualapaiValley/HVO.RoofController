using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Client;

/// <summary>Sign-in and the caller's own session (<c>api/v4.0/Auth</c>).</summary>
public sealed class RoofAuthApi
{
    private readonly RoofControllerClient _owner;
    private readonly RoofHttp _http;

    internal RoofAuthApi(RoofControllerClient owner, RoofHttp http)
    {
        _owner = owner;
        _http = http;
    }

    /// <summary>
    /// Signs in with a name and password and returns the session. The client's credential is not changed; set
    /// <see cref="RoofControllerClient.Credential"/> to the result to use it.
    /// </summary>
    public async Task<RoofSessionCredential> SignInAsync(string name, string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(password);
        var session = await _http.SendAsync<RoofSessionResponse>(
            HttpMethod.Post,
            RoofApiRoutes.Session,
            new RoofSignInRequest { Name = name, Password = password },
            cancellationToken,
            use: null).ConfigureAwait(false);
        return new RoofSessionCredential(session);
    }

    /// <summary>
    /// Unlocks a kiosk with a person's PIN. The client's credential must be a <see cref="RoofKioskCredential"/>; the
    /// request is made with the device key alone, and on success the kiosk uses the new PIN session.
    /// </summary>
    public async Task<RoofSessionResponse> SignInWithPinAsync(string name, string pin, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(pin);
        var kiosk = RequireKiosk();
        var session = await _http.SendAsync<RoofSessionResponse>(
            HttpMethod.Post,
            RoofApiRoutes.Pin,
            new RoofPinSignInRequest { Name = name, Pin = pin },
            cancellationToken,
            RoofCredentialUse.Device).ConfigureAwait(false);
        kiosk.UsePinSession(session);
        return session;
    }

    /// <summary>
    /// Signs a person in with their name and password on a device that has its own key (a <see cref="RoofKioskCredential"/>:
    /// the Mac app). The request carries no credential, as <see cref="SignInAsync"/>'s does not; on success the device
    /// sends the new session with its key, as it sends a PIN session, and drops it when the controller refuses it.
    /// </summary>
    public async Task<RoofSessionResponse> SignInOnDeviceAsync(string name, string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(password);
        var device = _owner.Credential as RoofKioskCredential
            ?? throw new InvalidOperationException("Signing in on a device needs a device credential (RoofKioskCredential).");
        var session = await _http.SendAsync<RoofSessionResponse>(
            HttpMethod.Post,
            RoofApiRoutes.Session,
            new RoofSignInRequest { Name = name, Password = password },
            cancellationToken,
            use: null).ConfigureAwait(false);
        device.UsePinSession(session);
        return session;
    }

    /// <summary>The people who may unlock this kiosk with a PIN. Device key only.</summary>
    public Task<RoofPinUserResponse[]> GetPinUsersAsync(CancellationToken cancellationToken = default)
    {
        RequireKiosk();
        return _http.GetAsync<RoofPinUserResponse[]>(RoofApiRoutes.PinUsers, cancellationToken, RoofCredentialUse.Device);
    }

    /// <summary>Who the controller takes the caller to be.</summary>
    public Task<RoofCallerResponse> GetCallerAsync(CancellationToken cancellationToken = default)
        => _http.GetAsync<RoofCallerResponse>(RoofApiRoutes.Me, cancellationToken);

    /// <summary>
    /// Confirms the session is still open and refreshes its expiry from the controller. The controller has no token
    /// refresh: a session that ended needs a new sign-in.
    /// </summary>
    public async Task<RoofCallerResponse> RefreshAsync(CancellationToken cancellationToken = default)
    {
        // The answer describes the credential the request was sent with; one set meanwhile (a new sign-in) is left alone.
        var sent = _owner.Credential;
        var caller = await GetCallerAsync(cancellationToken).ConfigureAwait(false);
        if (sent is RoofSessionCredential session && ReferenceEquals(_owner.Credential, sent))
        {
            session.Update(caller);
        }

        return caller;
    }

    /// <summary>
    /// Ends the current session (or a kiosk's PIN session) at the controller and marks it ended here. An API key has no
    /// session; the controller refuses that with 400.
    /// </summary>
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await _http.SendAsync(HttpMethod.Delete, RoofApiRoutes.Session, null, cancellationToken).ConfigureAwait(false);
        switch (_owner.Credential)
        {
            case RoofSessionCredential session:
                session.End();
                break;
            case RoofKioskCredential kiosk:
                kiosk.EndPinSession();
                break;
        }
    }

    /// <summary>Changes the signed-in person's password. The controller ends their other sessions.</summary>
    public Task ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(currentPassword);
        ArgumentException.ThrowIfNullOrEmpty(newPassword);
        return _http.SendAsync(
            HttpMethod.Post,
            RoofApiRoutes.Password,
            new RoofPasswordChangeRequest { CurrentPassword = currentPassword, NewPassword = newPassword },
            cancellationToken);
    }

    private RoofKioskCredential RequireKiosk()
        => _owner.Credential as RoofKioskCredential
            ?? throw new InvalidOperationException("PIN sign-in needs a kiosk credential (RoofKioskCredential).");
}
