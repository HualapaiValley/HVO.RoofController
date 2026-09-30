using System.Security.Cryptography.X509Certificates;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Certificates;
using HVO.RoofControllerV4.Installer.Machine;

namespace HVO.RoofControllerV4.Installer.Plan;

/// <summary>
/// The installer's certificate authority: made when missing, and made again when its key is gone, it expires within
/// 400 days, it may not issue for one of the controller's names, or a person asks (<c>--new-ca</c>, which names it by
/// fingerprint so the new one is then kept). Its key is written only to a file only root reads, and never shown.
/// </summary>
public sealed class CertificateAuthorityStep(ControllerLayout layout, CertificateNames names, string? replacing = null) : PlanStep
{
    public override StepKind Kind => StepKind.File;

    public override string Target => layout.CaCertificate;

    public override string Purpose => "the certificate authority clients trust (its key stays in " + layout.Ca + ")";

    public override Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        using var assessment = Assess(context);
        return Task.FromResult(assessment.Verdict switch
        {
            AuthorityVerdict.Missing => new StepCheck(StepChange.Create, assessment.Detail),
            AuthorityVerdict.Remake => new StepCheck(StepChange.Change, assessment.Detail),
            AuthorityVerdict.Unreadable => new StepCheck(StepChange.Info, assessment.Detail),
            _ => ModesDiffer(context.Machine) is { } modes ? new StepCheck(StepChange.Change, modes) : StepCheck.Unchanged(assessment.Detail)
        });
    }

    public override Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        var machine = context.Machine;
        using var assessment = Assess(context);
        if (assessment.Verdict == AuthorityVerdict.Keep)
        {
            machine.SetMode(layout.CaKey, Modes.PrivateFile);
            machine.SetMode(layout.CaCertificate, Modes.File);
            context.Log.Write($"Set {layout.CaKey} to {Modes.Octal(Modes.PrivateFile)} and {layout.CaCertificate} to {Modes.Octal(Modes.File)}.");
            return Task.CompletedTask;
        }

        using var authority = ControllerCertificates.CreateAuthority(names, context.Time.GetUtcNow());
        using var key = authority.GetECDsaPrivateKey()!;
        if (!machine.DirectoryExists(layout.Ca))
        {
            machine.CreateDirectory(layout.Ca, Modes.PrivateFolder);
        }

        machine.WriteAtomically(layout.CaKey, key.ExportPkcs8PrivateKeyPem(), Modes.PrivateFile);
        machine.WriteAtomically(layout.CaCertificate, authority.ExportCertificatePem() + "\n", Modes.File);
        context.Log.Write(
            $"Made the certificate authority {authority.GetNameInfo(X509NameType.SimpleName, false)}, until {AuthorityAssessment.Date(authority.NotAfter)}, "
            + $"SHA-256 {ControllerCertificates.Fingerprint(authority)}: clients trust {layout.CaCertificate}.");
        return Task.CompletedTask;
    }

    private AuthorityAssessment Assess(InstallContext context)
        => AuthorityAssessment.Assess(context.Machine, layout, names, replacing, context.Time.GetUtcNow());

    private string? ModesDiffer(InstallerMachine machine)
    {
        var key = machine.GetMode(layout.CaKey);
        var certificate = machine.GetMode(layout.CaCertificate);
        return key != Modes.PrivateFile ? $"its key: {Modes.Octal(key!.Value)} → {Modes.Octal(Modes.PrivateFile)}"
            : certificate != Modes.File ? $"{Modes.Octal(certificate!.Value)} → {Modes.Octal(Modes.File)}"
            : null;
    }
}

/// <summary>
/// The password of the controller's PKCS#12 file: 32 random bytes, made once, written straight to a file only root reads
/// (the controller reads it from its secrets folder), and never shown or logged.
/// </summary>
public sealed class CertificatePasswordStep(ControllerLayout layout) : PlanStep
{
    public override StepKind Kind => StepKind.File;

    public override string Target => layout.PfxPassword;

    public override string Purpose => "the certificate file's password (random, never shown)";

    public override Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        var machine = context.Machine;
        string? password;
        try
        {
            password = machine.ReadText(layout.PfxPassword);
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(new StepCheck(StepChange.Info, "only root can read it: run with sudo to check it"));
        }

        var mode = machine.GetMode(layout.PfxPassword);
        return Task.FromResult(
            password is null ? new StepCheck(StepChange.Create, Modes.Octal(Modes.PrivateFile))
            : password.Length == 0 ? new StepCheck(StepChange.Change, "it is empty: a new password")
            : mode != Modes.PrivateFile ? new StepCheck(StepChange.Change, $"{Modes.Octal(mode!.Value)} → {Modes.Octal(Modes.PrivateFile)}")
            : StepCheck.Unchanged(Modes.Octal(Modes.PrivateFile)));
    }

    public override Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        var machine = context.Machine;
        if (string.IsNullOrEmpty(machine.ReadText(layout.PfxPassword)))
        {
            // No newline: the controller reads the whole file as the password.
            machine.WriteAtomically(layout.PfxPassword, ControllerCertificates.NewPassword(), Modes.PrivateFile);
            context.Log.Write($"Wrote a new random password to {layout.PfxPassword}.");
        }
        else
        {
            machine.SetMode(layout.PfxPassword, Modes.PrivateFile);
            context.Log.Write($"Set {layout.PfxPassword} to {Modes.Octal(Modes.PrivateFile)}.");
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// The controller's certificate (the PKCS#12 file the deploy script mounts). With <see cref="ConnectionMode.PrivateCa"/>
/// the installer's CA issues it, and with <see cref="ConnectionMode.SelfSigned"/> it signs itself; either way it is
/// issued again when the CA is new, its password is new or does not open it, its names differ from the controller's, or
/// it expires within 30 days, or a person asks (<c>hvo-roof-install cert --renew</c>). With
/// <see cref="ConnectionMode.OwnCertificate"/> the person's certificate is kept as it is: <c>hvo-roof-install cert
/// import</c> puts it in place.
/// </summary>
public sealed class CertificateStep(ControllerLayout layout, CertificateNames names, ConnectionMode mode, string? replacingAuthority = null, bool renew = false) : PlanStep
{
    public override StepKind Kind => StepKind.File;

    public override string Target => layout.Pfx;

    public override string Purpose => mode switch
    {
        ConnectionMode.PrivateCa => $"the controller's certificate, from its CA, for {names.Host}",
        ConnectionMode.SelfSigned => $"the controller's self-signed certificate, for {names.Host}",
        _ => "your certificate for the controller"
    };

    public ConnectionMode Mode => mode;

    public override Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
        => Task.FromResult(Evaluate(context).Check);

    /// <summary>True when the step would put a new certificate in the file (not only set its mode).</summary>
    public bool WillIssue(InstallContext context) => Evaluate(context).Issue;

    /// <summary>The certificate in the file (with its key), or null when there is none, or it cannot be read or opened.</summary>
    public X509Certificate2? Current(InstallerMachine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        try
        {
            return machine.ReadBytes(layout.Pfx) is { } pfx && machine.ReadText(layout.PfxPassword) is { Length: > 0 } password
                ? ControllerCertificates.LoadPfx(pfx, password)
                : null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public override Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        var machine = context.Machine;
        var (_, issue) = Evaluate(context);
        if (!issue)
        {
            machine.SetMode(layout.Pfx, Modes.PrivateFile);
            context.Log.Write($"Set {layout.Pfx} to {Modes.Octal(Modes.PrivateFile)}.");
            return Task.CompletedTask;
        }

        var password = machine.ReadText(layout.PfxPassword);
        if (string.IsNullOrEmpty(password))
        {
            throw new InstallerException($"{layout.PfxPassword} is missing or empty, so the certificate cannot be written.");
        }

        var now = context.Time.GetUtcNow();
        if (mode == ConnectionMode.SelfSigned)
        {
            using var selfSigned = ControllerCertificates.SelfSigned(names, now);
            Write(context, ControllerCertificates.ExportPfx(selfSigned, null, password), selfSigned, "signed itself");
            return Task.CompletedTask;
        }

        using var assessment = AuthorityAssessment.Assess(machine, layout, names, replacingAuthority, now);
        if (assessment.Authority is not { } authority)
        {
            throw new InstallerException($"The certificate authority in {layout.CaCertificate} cannot issue the certificate: {assessment.Detail}.");
        }

        using var issued = ControllerCertificates.Issue(authority, names, now);
        Write(context, ControllerCertificates.ExportPfx(issued, authority, password), issued, $"issued by {authority.GetNameInfo(X509NameType.SimpleName, false)}");
        return Task.CompletedTask;
    }

    /// <summary>What the step would do, and whether that is a new certificate (otherwise only the file's mode is set).</summary>
    private (StepCheck Check, bool Issue) Evaluate(InstallContext context)
    {
        var machine = context.Machine;
        var now = context.Time.GetUtcNow();
        byte[]? pfx;
        string? password;
        try
        {
            pfx = machine.ReadBytes(layout.Pfx);
            password = machine.ReadText(layout.PfxPassword);
        }
        catch (UnauthorizedAccessException)
        {
            return (new StepCheck(StepChange.Info, "only root can read it: run with sudo to check it"), false);
        }

        if (mode == ConnectionMode.OwnCertificate)
        {
            using var own = pfx is null || string.IsNullOrEmpty(password) ? null : ControllerCertificates.LoadPfx(pfx, password);
            return own is null
                ? (new StepCheck(StepChange.Blocked, pfx is null
                    ? "there is none yet: give yours with hvo-roof-install cert import FILE, or choose private-ca"
                    : $"the password in {layout.PfxPassword} does not open it: give it again with hvo-roof-install cert import FILE"), false)
                : ModeCheck(machine) ?? (StepCheck.Unchanged($"yours, kept: {Describe(own)}"), false);
        }

        using var assessment = mode == ConnectionMode.PrivateCa ? AuthorityAssessment.Assess(machine, layout, names, replacingAuthority, now) : null;
        var issueAs = pfx is null ? StepChange.Create : StepChange.Change;
        if (assessment is { Verdict: AuthorityVerdict.Unreadable })
        {
            return (new StepCheck(StepChange.Info, assessment.Detail), false);
        }

        if (pfx is null)
        {
            return (new StepCheck(StepChange.Create, $"for {names.DnsNames.Count} names and {names.Addresses.Count} addresses, until {AuthorityAssessment.Date((now + Lifetime).UtcDateTime)}"), true);
        }

        if (assessment is { Authority: null })
        {
            return (new StepCheck(issueAs, "issued again by the new CA"), true);
        }

        if (string.IsNullOrEmpty(password))
        {
            return (new StepCheck(issueAs, "issued again, with a new password"), true);
        }

        using var current = ControllerCertificates.LoadPfx(pfx, password);
        if (current is null)
        {
            return (new StepCheck(issueAs, "its password does not open it: issued again"), true);
        }

        var (missing, extra) = ControllerCertificates.CompareNames(current, names);
        var reason = assessment?.Authority is { } authority && !ControllerCertificates.IsIssuedBy(current, authority)
                ? "another CA issued it: issued again by this machine's CA"
            : mode == ConnectionMode.SelfSigned && !ControllerCertificates.IsSelfSigned(current)
                ? "it is not self-signed: a self-signed one in its place"
            : missing.Count > 0 || extra.Count > 0
                ? $"the names changed ({Changes(missing, extra)}): issued again"
            : new DateTimeOffset(current.NotAfter) - now < ControllerCertificates.RenewWithin
                ? $"it expires on {AuthorityAssessment.Date(current.NotAfter)}: issued again"
            : renew
                ? $"issued again, as asked (--renew): it was valid until {AuthorityAssessment.Date(current.NotAfter)}"
            : null;
        return reason is not null
            ? (new StepCheck(StepChange.Change, reason), true)
            : ModeCheck(machine) ?? (StepCheck.Unchanged($"kept: {Describe(current)}"), false);
    }

    private TimeSpan Lifetime => mode == ConnectionMode.SelfSigned ? ControllerCertificates.SelfSignedLifetime : ControllerCertificates.IssuedLifetime;

    private (StepCheck, bool)? ModeCheck(InstallerMachine machine)
        => machine.GetMode(layout.Pfx) is { } current && current != Modes.PrivateFile
            ? (new StepCheck(StepChange.Change, $"{Modes.Octal(current)} → {Modes.Octal(Modes.PrivateFile)}"), false)
            : null;

    private void Write(InstallContext context, byte[] pfx, X509Certificate2 certificate, string issuer)
    {
        context.Machine.WriteAtomically(layout.Pfx, pfx, Modes.PrivateFile);
        context.Log.Write($"Wrote {layout.Pfx}: {Describe(certificate)}, {issuer}, SHA-256 {ControllerCertificates.Fingerprint(certificate)}.");
    }

    private static string Describe(X509Certificate2 certificate)
        => $"{certificate.GetNameInfo(X509NameType.SimpleName, false)}, until {AuthorityAssessment.Date(certificate.NotAfter)}";

    private static string Changes(IReadOnlyList<string> missing, IReadOnlyList<string> extra)
        => string.Join("; ", new[]
        {
            missing.Count > 0 ? $"adds {string.Join(", ", missing)}" : null,
            extra.Count > 0 ? $"drops {string.Join(", ", extra)}" : null
        }.OfType<string>());
}

/// <summary>
/// A certificate a person gives (<c>hvo-roof-install cert import</c>), put in the controller's PKCS#12 file with the
/// certificates that chain it, under the password the installer made; the file it came from is left as it is. Unchanged
/// when that certificate is already in place.
/// </summary>
public sealed class ImportCertificateStep(ControllerLayout layout, ImportedCertificate imported, string source) : PlanStep
{
    public override StepKind Kind => StepKind.File;

    public override string Target => layout.Pfx;

    public override string Purpose => $"your certificate for the controller, from {source}";

    public override Task<StepCheck> CheckAsync(InstallContext context, CancellationToken cancellationToken)
    {
        var machine = context.Machine;
        byte[]? pfx;
        string? password;
        try
        {
            pfx = machine.ReadBytes(layout.Pfx);
            password = machine.ReadText(layout.PfxPassword);
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(new StepCheck(StepChange.Info, "only root can read it: run with sudo to check it"));
        }

        var detail = Describe(imported);
        if (pfx is null)
        {
            return Task.FromResult(new StepCheck(StepChange.Create, detail));
        }

        return Task.FromResult(
            !InPlace(pfx, password) ? new StepCheck(StepChange.Change, detail)
            : machine.GetMode(layout.Pfx) is { } mode && mode != Modes.PrivateFile ? new StepCheck(StepChange.Change, $"{Modes.Octal(mode)} → {Modes.Octal(Modes.PrivateFile)}")
            : StepCheck.Unchanged($"already in place: {detail}"));
    }

    public override Task ApplyAsync(InstallContext context, StepCheck check, CancellationToken cancellationToken)
    {
        var machine = context.Machine;
        var password = machine.ReadText(layout.PfxPassword);
        if (string.IsNullOrEmpty(password))
        {
            throw new InstallerException($"{layout.PfxPassword} is missing or empty, so the certificate cannot be written.");
        }

        if (machine.ReadBytes(layout.Pfx) is { } pfx && InPlace(pfx, password))
        {
            machine.SetMode(layout.Pfx, Modes.PrivateFile);
            context.Log.Write($"Set {layout.Pfx} to {Modes.Octal(Modes.PrivateFile)}.");
            return Task.CompletedTask;
        }

        machine.WriteAtomically(layout.Pfx, ControllerCertificates.ExportPfxWithChain(imported.Certificate, imported.Chain, password), Modes.PrivateFile);
        context.Log.Write($"Wrote {layout.Pfx} from {source}: {Describe(imported)}, SHA-256 {ControllerCertificates.Fingerprint(imported.Certificate)}.");
        return Task.CompletedTask;
    }

    /// <summary>"{subject}, until {date}, issued by {issuer}".</summary>
    public static string Describe(ImportedCertificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return $"{certificate.Subject}, until {AuthorityAssessment.Date(certificate.Certificate.NotAfter)}, issued by {certificate.Issuer}";
    }

    // The same certificate with the same chain after it: a file of the certificate alone is written again with its chain.
    private bool InPlace(byte[] pfx, string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        using var current = ControllerCertificates.LoadPfx(pfx, password);
        return current is not null
            && current.RawData.AsSpan().SequenceEqual(imported.Certificate.RawData)
            && ControllerCertificates.PfxChain(pfx, password) is { } chain
            && chain.Count == imported.Chain.Count
            && imported.Chain.All(link => chain.Any(held => held.AsSpan().SequenceEqual(link.RawData)));
    }
}
