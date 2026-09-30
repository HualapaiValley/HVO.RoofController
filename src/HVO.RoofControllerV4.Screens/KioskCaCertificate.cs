using System.Security.Cryptography.X509Certificates;
using HVO.RoofControllerV4.Client;

namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// Reads the private CA that issued the controller's certificate, such as the installer's (the file the
/// <c>ServerCaCertificateFile</c> setting of the kiosk or the Mac app names), once, at startup. Only that CA is then
/// trusted for the controller (<see cref="RoofConnectionOptions.ServerCaCertificate"/>).
/// </summary>
public static class KioskCaCertificate
{
    /// <summary>The kiosk's setting, as messages name it.</summary>
    public const string KioskSetting = "Kiosk:ServerCaCertificateFile";

    /// <summary>Reads the CA certificate, PEM or DER, from <paramref name="path"/>.</summary>
    /// <param name="path">The CA certificate file.</param>
    /// <param name="setting">The setting that names the file, as messages name it.</param>
    /// <param name="reader">Who must be able to read the file, as messages say it.</param>
    /// <exception cref="KioskSettingsException">The file cannot be read, or does not hold a CA certificate.</exception>
    public static X509Certificate2 Load(string path, string setting = KioskSetting, string reader = "the kiosk's user")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            return RoofCertificateAuthority.Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new KioskSettingsException(
                $"{setting} names {path}, which could not be read ({ex.GetType().Name}). It must be readable by {reader}.");
        }
        catch (ArgumentException ex)
        {
            throw new KioskSettingsException($"{setting} cannot be used: {ex.Message}");
        }
    }
}
