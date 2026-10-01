using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Installer.Certificates;
using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Settings;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.RPi.Tests.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>
/// The controller on a Pi with the HAT (#74), from docs/install.md's answers file for it: the plan the installer prints,
/// every file and folder it leaves with its mode and owner, and the controller's own deployment check
/// (--validate-deployment) passing with the settings the installer gives the deploy script, run as the script's pre-flight
/// runs it, and failing with a setting the installer never gives. Keys, passwords and certificates are new on each run:
/// they are checked for their shape, and never shown. What only the Pi can prove stays a documented assumption: that
/// /dev/i2c-1 is the HAT's I2C bus with the HAT on it, that /dev/gpiomem and the thermal sensor are the Pi's, that Docker
/// maps the devices and mounts the script names, and that the image's environment is its Dockerfile's on the .NET base
/// image's (ASPNETCORE_HTTP_PORTS=8080). Here the devices are empty files, Docker is the script's arguments read as Docker
/// reads them, and the container's paths are the fake Pi's through the script's mounts.
/// </summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed partial class InstallerRealHatTests
{
    private const string Secrets = "/etc/hvo-roof/secrets";
    private const string CertificatePassword = $"{Secrets}/Kestrel__Certificates__Default__Password";
    private const string Password = "a long test password";
    private const string Pin = "9081726354";
    private const string CameraPassword = "camera test password";
    private const string PreFlightFailed = "[deploy] ERROR: Pre-flight failed (see above). The running controller was not touched.";

    private static readonly string Root = ProductVersionTests.RepositoryRoot();

    // The certificate's last day, from the tests' clock.
    private static readonly string Until = (FakeMachine.Today + ControllerCertificates.IssuedLifetime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // What --plan prints for the docs' answers file on the Pi: the real HAT, so no emulator.
    private static readonly string[] Plan =
    [
        "The plan for the controller on roofpi, 4.0.0:",
        "",
        "Folders",
        "  create     /etc/hvo-roof                                 the controller's configuration (0755)",
        "  create     /etc/hvo-roof/secrets                         secrets the controller reads, one file per setting (0700)",
        "  create     /etc/hvo-roof/https                           the controller's HTTPS certificate (0700)",
        "  create     /etc/hvo-roof/ca                              the certificate authority's key (0700)",
        "  create     /etc/hvo-roof/config                          settings files the controller reads (0755)",
        "  create     /var/lib/hvo-roof                             the controller's data (0755)",
        "  create     /var/lib/hvo-roof/identity                    people, sessions and API keys (0700)",
        "  create     /var/lib/hvo-roof/settings-secrets            secrets set through the API (0700)",
        "Files",
        "  create     /etc/hvo-roof/ca.crt                          the certificate authority clients trust (its key stays in /etc/hvo-roof/ca) (a new CA, which may issue only for names in local, localhost, roofpi, roof, observatory.example, and private addresses)",
        "  create     /etc/hvo-roof/secrets/Kestrel__Certificates__Default__Password  the certificate file's password (random, never shown) (0600)",
        $"  create     /etc/hvo-roof/https/roof-controller.pfx       the controller's certificate, from its CA, for roofpi (for 7 names and 3 addresses, until {Until})",
        "  create     /etc/hvo-roof/secrets/RoofControllerSecurity__ApiKeys__0__Key  the installer's operator key: the deploy script's Status and verified Stop (never shown) (installer-operator (RoofOperator): a new random key, never shown)",
        "  create     /etc/hvo-roof/secrets/RoofControllerSecurity__ApiKeys__1__Key  the installer's admin key: adds the first admin (never shown) (installer-admin (RoofAdmin): a new random key, never shown)",
        "  create     /etc/hvo-roof/secrets/RoofControllerSecurity__ApiKeys__2__Key  the web UI's own key for Stop (never shown) (web-ui-stop (RoofViewer): a new random key, never shown)",
        "  create     /etc/hvo-roof/secrets/BlueIris__BaseUrl       the Blue Iris server the controller shows the camera from (http://192.168.0.4:81)",
        "  create     /etc/hvo-roof/secrets/BlueIris__Password      the password of the camera's user, roof-viewer (never shown) (typed when the install runs, or given with --camera-password-file)",
        "  create     /etc/hvo-roof/secrets/BlueIris__UserName      the Blue Iris user the controller views the camera as (roof-viewer)",
        "  create     /usr/local/sbin/hvo-roof-install              hvo-roof-install: upgrade, rollback, backup and uninstall (release 4.0.0's, 0755)",
        "  create     /etc/hvo-roof/install.json                    the install record: the roles, choices and versions (no secrets) (0644)",
        "Users",
        "  create     observer                                      the first admin: signs in to the web UI, and at the kiosk with a PIN, and adds everyone else (added through the controller's API as RoofAdmin, with a password and a PIN)",
        "Packages",
        "  info       bash, curl, setsid or perl, jq or python3     what the deploy script runs (found bash, curl, setsid, jq)",
        "Containers",
        "  create     roof-controller                               the controller, driving the real HAT (release 4.0.0 by digest, through the deploy script)",
        "Ports",
        "  info       8443                                          the controller's API (HTTPS) (free; roof-controller will listen on it)",
        "  info       8088                                          the web UI (free; roof-controller will listen on it)",
        "",
        "21 to create, 0 to change, 0 unchanged.",
        "",
        "The install asks for the first admin's password (or give it with --admin-password-file FILE).",
        "The install asks for the first admin's PIN (or give it with --admin-pin-file FILE).",
        "The install asks for the camera's password (or give it with --camera-password-file FILE).",
        ""
    ];

    // Each file and folder the install leaves that was not there, with its mode and owner.
    private static readonly string[] Installed =
    [
        "/etc/hvo-roof folder 0755 root:root",
        "/etc/hvo-roof/ca folder 0700 root:root",
        "/etc/hvo-roof/ca.crt file 0644 root:root",
        "/etc/hvo-roof/ca/ca.key file 0600 root:root",
        "/etc/hvo-roof/config folder 0755 root:root",
        "/etc/hvo-roof/https folder 0700 root:root",
        "/etc/hvo-roof/https/roof-controller.pfx file 0600 root:root",
        "/etc/hvo-roof/install.json file 0644 root:root",
        "/etc/hvo-roof/secrets folder 0700 root:root",
        "/etc/hvo-roof/secrets/BlueIris__BaseUrl file 0600 root:root",
        "/etc/hvo-roof/secrets/BlueIris__Password file 0600 root:root",
        "/etc/hvo-roof/secrets/BlueIris__UserName file 0600 root:root",
        "/etc/hvo-roof/secrets/Kestrel__Certificates__Default__Password file 0600 root:root",
        "/etc/hvo-roof/secrets/RoofControllerSecurity__ApiKeys__0__Key file 0600 root:root",
        "/etc/hvo-roof/secrets/RoofControllerSecurity__ApiKeys__0__Name file 0600 root:root",
        "/etc/hvo-roof/secrets/RoofControllerSecurity__ApiKeys__0__Role file 0600 root:root",
        "/etc/hvo-roof/secrets/RoofControllerSecurity__ApiKeys__1__Key file 0600 root:root",
        "/etc/hvo-roof/secrets/RoofControllerSecurity__ApiKeys__1__Name file 0600 root:root",
        "/etc/hvo-roof/secrets/RoofControllerSecurity__ApiKeys__1__Role file 0600 root:root",
        "/etc/hvo-roof/secrets/RoofControllerSecurity__ApiKeys__2__Key file 0600 root:root",
        "/etc/hvo-roof/secrets/RoofControllerSecurity__ApiKeys__2__Name file 0600 root:root",
        "/etc/hvo-roof/secrets/RoofControllerSecurity__ApiKeys__2__Role file 0600 root:root",
        "/usr/local/sbin/hvo-roof-install file 0755 root:root",
        "/var/lib/hvo-roof folder 0755 root:root",
        "/var/lib/hvo-roof/identity folder 0700 root:root",
        "/var/lib/hvo-roof/identity/identity.json file 0600 root:root",
        "/var/lib/hvo-roof/settings-secrets folder 0700 root:root",
        "/var/log/hvo-roof-install.log file 0640 root:root"
    ];

    // The deploy script's defaults for the settings it gives the container: NAME=${NAME:-default} when unset or empty,
    // NAME=${NAME-default} when unset.
    private static readonly (string Name, string Default, bool OrEmpty)[] ScriptDefaults =
    [
        ("EXTRA_DOCKER_ARGS", "", true),
        ("HVO_FORCE_RASPBERRY_PI", "true", true),
        ("IGNORE_PHYSICAL_LIMIT_SWITCHES", "false", true),
        ("OTEL_SERVICE_NAME", "hvo-roof-controller", true),
        ("OTEL_SERVICE_INSTANCE_ID", "roof-controller-rpi", true),
        ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://192.168.1.238:4318", false),
        ("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf", true),
        ("OTEL_METRIC_EXPORT_INTERVAL", "10000", true),
        ("HAT_EMULATOR_ENDPOINT", "", true),
        ("SECRETS_DIR", "/etc/hvo-roof/secrets", true),
        ("IDENTITY_DIR", "/var/lib/hvo-roof/identity", false),
        ("CONFIG_DIR", "/etc/hvo-roof/config", false),
        ("MANAGED_SECRETS_DIR", "/var/lib/hvo-roof/settings-secrets", false),
        ("HTTPS_CERT_DIR", "", true),
        ("HTTPS_CERT_FILE", "roof-controller.pfx", true),
        ("ALLOWED_HOSTS", "", true)
    ];

    // The deploy script's container_args, line for line and in its order, each group with the setting it needs: set, or
    // (after a !) empty.
    private static readonly (string? When, string[] Lines)[] ContainerArgs =
    [
        (null,
        [
            "--env \"HVO_FORCE_RASPBERRY_PI=${HVO_FORCE_RASPBERRY_PI}\"",
            "--env \"RoofControllerOptionsV4__IgnorePhysicalLimitSwitches=${IGNORE_PHYSICAL_LIMIT_SWITCHES}\"",
            "--env \"OTEL_SERVICE_NAME=${OTEL_SERVICE_NAME}\"",
            "--env \"OTEL_RESOURCE_ATTRIBUTES=service.instance.id=${OTEL_SERVICE_INSTANCE_ID}\"",
            "--env \"OTEL_EXPORTER_OTLP_ENDPOINT=${OTEL_EXPORTER_OTLP_ENDPOINT}\"",
            "--env \"OTEL_EXPORTER_OTLP_PROTOCOL=${OTEL_EXPORTER_OTLP_PROTOCOL}\"",
            "--env \"OTEL_METRIC_EXPORT_INTERVAL=${OTEL_METRIC_EXPORT_INTERVAL}\"",
            "--mount \"type=bind,src=${SECRETS_DIR},dst=/run/secrets,readonly\""
        ]),
        ("IDENTITY_DIR",
        [
            "--mount \"type=bind,src=${IDENTITY_DIR},dst=/var/lib/hvo-roof/identity\"",
            "--env \"RoofControllerSecurity__Identity__StorePath=/var/lib/hvo-roof/identity/identity.json\""
        ]),
        ("CONFIG_DIR",
        [
            "--mount \"type=bind,src=${CONFIG_DIR},dst=/etc/hvo-roof/config\"",
            "--env \"RoofControllerSettings__FilePath=/etc/hvo-roof/config/appsettings.Local.json\""
        ]),
        ("MANAGED_SECRETS_DIR",
        [
            "--mount \"type=bind,src=${MANAGED_SECRETS_DIR},dst=/var/lib/hvo-roof/settings-secrets\"",
            "--env \"RoofControllerSettings__SecretsFilePath=/var/lib/hvo-roof/settings-secrets/secrets.json\""
        ]),
        ("HAT_EMULATOR_ENDPOINT",
        [
            "--env \"HatEmulator__Enabled=true\"",
            "--env \"HatEmulator__Host=${HAT_EMULATOR_HOST}\"",
            "--env \"HatEmulator__Port=${HAT_EMULATOR_PORT}\"",
            "--env \"HatEmulator__AllowOutsideDevelopment=true\""
        ]),
        ("!HAT_EMULATOR_ENDPOINT",
        [
            "--device /dev/gpiomem:/dev/gpiomem",
            "--mount type=bind,src=/sys/class/thermal/thermal_zone0/temp,dst=/sys/class/thermal/thermal_zone0/temp,readonly",
            "--device /dev/i2c-1:/dev/i2c-1",
            "--env \"HatEmulator__Enabled=false\"",
            "--env \"HatEmulator__AllowOutsideDevelopment=false\""
        ]),
        ("HTTPS_CERT_DIR",
        [
            "--mount \"type=bind,src=${HTTPS_CERT_DIR},dst=/https,readonly\"",
            "--env \"ASPNETCORE_URLS=http://localhost:8080;https://+:8443\"",
            "--env \"Kestrel__Certificates__Default__Path=/https/${HTTPS_CERT_FILE}\"",
            "--env \"RoofControllerSecurity__RequireHttps=true\"",
            "--env \"RoofWeb__Urls=https://+:8088\""
        ]),
        ("!HTTPS_CERT_DIR",
        [
            "--env \"ASPNETCORE_URLS=http://+:8080\"",
            "--env \"RoofControllerSecurity__RequireHttps=false\"",
            "--env \"RoofWeb__Urls=http://+:8088\""
        ]),
        ("ALLOWED_HOSTS", ["--env \"AllowedHosts=${ALLOWED_HOSTS}\""])
    ];

    // What the pre-flight adds after container_args and EXTRA_DOCKER_ARGS.
    private const string DeployKeyArgument = "--env \"DeploymentCheck__DeployKeySha256=${deploy_key_sha256}\"";

    // The image's environment: its Dockerfile's, and the .NET base image's port.
    private static readonly Dictionary<string, string> ImageEnvironment = new(StringComparer.Ordinal)
    {
        ["ASPNETCORE_HTTP_PORTS"] = "8080",
        ["ASPNETCORE_URLS"] = "http://+:8080",
        ["ASPNETCORE_ENVIRONMENT"] = "Production",
        ["TZ"] = "UTC",
        ["RoofWeb__Urls"] = "http://+:8088"
    };

    [TestMethod]
    public async Task ThePi_PlansTheControllerForTheRealHat_FromTheDocsAnswers_AndChangesNothing()
    {
        using var pi = Pi();
        var answers = DocsAnswers(pi);
        var listed = pi.Listing();
        var before = pi.Snapshot();

        var run = await pi.RunAsync("--plan", "--answers", answers);

        run.ExitCode.Should().Be(0, run.ToString());
        run.Error.Should().BeEmpty();
        run.Output.Split('\n').Should().Equal(Plan);
        pi.Listing().Should().Equal(listed, "--plan makes nothing");
        pi.Snapshot().SequenceEqual(before).Should().BeTrue("--plan changes and touches nothing");
        pi.Asked.Should().BeEmpty("--plan asks for no secret");
        pi.Deploys.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ThePi_IsLeftWithTheFilesThePlanNamed_EachWithItsModeAndOwner_AndNoSecretShown()
    {
        using var pi = Pi();
        var answers = DocsAnswers(pi);
        var before = pi.Listing();

        var run = await pi.RunAsync("--answers", answers);

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Asked.Should().Equal(
            "the first admin's password", "the first admin's password again",
            "the first admin's PIN", "the first admin's PIN again",
            "the camera's password", "the camera's password again");
        var after = pi.Listing();
        before.Except(after).Should().BeEmpty("the install changes the mode or owner of nothing that was there");
        after.Except(before).Should().Equal(Installed);

        (string Name, string Role)[] keys =
        [
            (ApiKeyFiles.OperatorName, RoofControllerApiContract.OperatorRole),
            (ApiKeyFiles.AdminName, RoofControllerApiContract.AdminRole),
            (ApiKeyFiles.WebStopName, RoofControllerApiContract.ViewerRole)
        ];
        for (var index = 0; index < keys.Length; index++)
        {
            pi.Read($"{Secrets}/{ApiKeyFiles.FileName(index, "Name")}").Should().Be(keys[index].Name);
            pi.Read($"{Secrets}/{ApiKeyFiles.FileName(index, "Role")}").Should().Be(keys[index].Role);
            IsRandom(pi.Read(ApiKeyFiles.KeyFile(Secrets, index))).Should().BeTrue($"{keys[index].Name}'s key is 32 random bytes, as base64url");
        }

        pi.ApiKeyValues().Distinct().Count().Should().Be(3, "each key is its own");
        IsRandom(pi.Read(CertificatePassword)).Should().BeTrue("the certificate file's password is 32 random bytes, as base64url");
        pi.Read($"{Secrets}/{CameraSteps.BaseUrlSetting}").Should().Be("http://192.168.0.4:81");
        pi.Read($"{Secrets}/{CameraSteps.UserNameSetting}").Should().Be("roof-viewer");
        (pi.Read($"{Secrets}/{CameraSteps.PasswordSetting}") == CameraPassword).Should().BeTrue("the camera's password is the one typed");

        using var authority = X509Certificate2.CreateFromPem(pi.Read("/etc/hvo-roof/ca.crt"), pi.Read("/etc/hvo-roof/ca/ca.key"));
        authority.HasPrivateKey.Should().BeTrue("the CA's key is the one for its certificate");
        authority.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority.Should().BeTrue();
        using var certificate = ControllerCertificates.LoadPfx(File.ReadAllBytes(pi.OnDisk("/etc/hvo-roof/https/roof-controller.pfx")), pi.Read(CertificatePassword));
        (certificate is { HasPrivateKey: true }).Should().BeTrue("the certificate file opens, with its key, with the password beside it");
        ControllerCertificates.IsIssuedBy(certificate!, authority).Should().BeTrue("the controller's certificate is from its CA");
        var (dnsNames, addresses) = ControllerCertificates.NamesIn(certificate!);
        dnsNames.Should().Equal("roofpi", "roofpi.local", "roofpi.observatory.example", "roof", "roof.local", "roof.observatory.example", "localhost");
        addresses.Select(address => address.ToString()).Should().Equal("192.168.1.50", "127.0.0.1", "::1");
        (new DateTimeOffset(certificate!.NotBefore) <= FakeMachine.Today && FakeMachine.Today < new DateTimeOffset(certificate.NotAfter))
            .Should().BeTrue("the certificate is good from the day it is made");

        pi.People["observer"].Should().Be(RoofControllerApiContract.AdminRole);
        pi.UserRequests.Count.Should().Be(1);
        pi.UserRequests[0].Name.Should().Be("observer");
        pi.UserRequests[0].Role.Should().Be(RoofControllerApiContract.AdminRole);
        (pi.UserRequests[0].Password == Password && pi.UserRequests[0].Pin == Pin).Should().BeTrue("the first admin has the password and PIN typed");
        (pi.UserRequests[0].Key == pi.Read(ApiKeyFiles.KeyFile(Secrets, 1))).Should().BeTrue("the first admin is added with the installer's admin key");

        string[] secrets = [Password, Pin, CameraPassword, pi.Read(CertificatePassword), .. pi.ApiKeyValues()];
        Shows(run.Output + run.Error, secrets).Should().BeFalse("the run shows no secret");
        Shows(pi.Read(InstallPaths.SystemLog), secrets).Should().BeFalse("the log holds no secret");
        Shows(pi.Read(InstallPaths.SystemRecord), secrets).Should().BeFalse("the record holds no secret");
        pi.Ran.Any(command => Shows(string.Join('\n', command.Arguments.Concat(command.Environment?.Values ?? [])), secrets))
            .Should().BeFalse("no secret is on a command line or in a command's environment");
    }

    [TestMethod]
    public async Task ThePi_PassesTheControllersDeploymentCheck_WithTheSettingsTheInstallerGave()
    {
        using var pi = Pi();
        var checks = PreFlight(pi);

        var run = await pi.RunAsync("--answers", DocsAnswers(pi));

        run.ExitCode.Should().Be(0, run.ToString());
        var deploy = pi.Deploys.Single();
        string[] secrets = [Password, Pin, CameraPassword, pi.Read(CertificatePassword), .. pi.ApiKeyValues()];
        Shows(string.Join('\n', deploy.Values), secrets).Should().BeFalse("the deploy script is given a key only as a file's path");
        deploy.Should().Equal(new Dictionary<string, string>
        {
            ["ALLOWED_HOSTS"] = "roofpi;roofpi.local;roofpi.observatory.example;roof;roof.local;roof.observatory.example;localhost;192.168.1.50;127.0.0.1;[::1]",
            ["ALLOW_EMULATED_HAT"] = "false",
            ["ALLOW_INSECURE_HTTP"] = "false",
            ["BUILD_PLATFORM"] = "linux/arm64",
            ["CONFIG_DIR"] = "/etc/hvo-roof/config",
            ["CONTAINER_NAME"] = "roof-controller",
            ["DOCKER_CONTEXT"] = "default",
            ["EXTRA_DOCKER_ARGS"] = "--env RoofWeb__StopKeyFile=/run/secrets/RoofControllerSecurity__ApiKeys__2__Key",
            ["HAT_EMULATOR_ENDPOINT"] = "",
            ["HOST_PORT"] = "8080",
            ["HTTPS_CERT_DIR"] = "/etc/hvo-roof/https",
            ["HTTPS_CERT_FILE"] = "roof-controller.pfx",
            ["HTTPS_HOST_PORT"] = "8443",
            ["HVO_FORCE_RASPBERRY_PI"] = "true",
            ["IDENTITY_DIR"] = "/var/lib/hvo-roof/identity",
            ["IGNORE_PHYSICAL_LIMIT_SWITCHES"] = "false",
            ["IMAGE_REF"] = $"ghcr.io/hualapaivalley/roof-controller:4.0.0@{FakeMachine.ControllerDigest}",
            ["MANAGED_SECRETS_DIR"] = "/var/lib/hvo-roof/settings-secrets",
            ["OPERATOR_KEY_FILE"] = ApiKeyFiles.KeyFile(Secrets, 0),
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://collector:4318",
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
            ["PI_HOST"] = "roofpi",
            ["PUBLISH_ADDRESS"] = "",
            ["REMOTE_CA_CERT"] = "/etc/hvo-roof/ca.crt",
            ["REMOTE_CONNECT_TO"] = "::127.0.0.1:",
            ["REQUIRE_IDLE_ROOF"] = "true",
            ["ROOF_OPERATOR_API_KEY"] = "",
            ["SECRETS_DIR"] = Secrets,
            ["SKIP_REMOTE_CHECK"] = "false",
            ["WEB_HOST_PORT"] = "8088"
        });

        var check = checks.Should().ContainSingle("the deploy script checks the new container once, before it touches the running one").Subject;
        check.Problems.Should().BeEmpty();
        check.Warnings.Should().BeEmpty();

        // The certificate's note has its thumbprint and dates: checked for its shape, and never shown.
        var certificateNotes = check.Notes.Where(note => note.StartsWith("Kestrel:Certificates:Default: ", StringComparison.Ordinal)).ToList();
        (certificateNotes.Count == 1 && CertificateNote().IsMatch(certificateNotes[0])).Should().BeTrue("the check loads the controller's certificate, issued to roofpi");
        check.Notes.Except(certificateNotes).Should().Equal(
            "API keys: installer-operator (RoofOperator), installer-admin (RoofAdmin), web-ui-stop (RoofViewer)",
            "Deploy key: installer-operator (RoofOperator).",
            $"Identity store: {pi.OnDisk("/var/lib/hvo-roof/identity/identity.json")}.",
            $"Settings file: {pi.OnDisk("/etc/hvo-roof/config/appsettings.Local.json")}.",
            $"Managed secrets file: {pi.OnDisk("/var/lib/hvo-roof/settings-secrets/secrets.json")}.",
            "Listeners: http://localhost:8080, https://+:8443 (from ASPNETCORE_URLS; ignored: ASPNETCORE_HTTP_PORTS); HTTPS required: yes.");
    }

    [TestMethod]
    public async Task ThePi_FailsTheDeploymentCheck_WithTheHatEmulatorTurnedOnInItsSecrets()
    {
        using var pi = Pi();
        var checks = PreFlight(pi, before: () => pi.Write($"{Secrets}/HatEmulator__Enabled", "true"));

        var run = await pi.RunAsync("--answers", DocsAnswers(pi));

        run.ExitCode.Should().NotBe(0, "the deploy script stops when the check fails");
        run.Error.Should().Contain(PreFlightFailed);
        checks.Should().ContainSingle().Which.Problems.Should().SatisfyRespectively(
            problem => problem.Should().StartWith("HatEmulator:Enabled is true and the HAT's I2C device (/dev/i2c-1) is mapped."),
            problem => problem.Should().StartWith("HatEmulator:Enabled is true in the Production environment without HatEmulator:AllowOutsideDevelopment"));
    }

    [TestMethod]
    public async Task ThePi_FailsTheDeploymentCheck_WithItsLimitSwitchesIgnored()
    {
        using var pi = Pi();
        var checks = PreFlight(pi, change: ("IGNORE_PHYSICAL_LIMIT_SWITCHES", "true"));

        var run = await pi.RunAsync("--answers", DocsAnswers(pi));

        run.ExitCode.Should().NotBe(0, "the deploy script stops when the check fails");
        run.Error.Should().Contain(PreFlightFailed);
        checks.Should().ContainSingle().Which.Problems.Should().ContainSingle()
            .Which.Should().StartWith("RoofControllerOptionsV4: IgnorePhysicalLimitSwitches is true without AllowIgnoringLimitSwitchesOnPhysicalHardware, and the HAT's I2C device (/dev/i2c-1) is mapped");
    }

    [TestMethod]
    public void TheDeployScriptAndTheImage_GiveTheContainerTheSettingsTheCheckIsRunWith()
    {
        var script = DeployScript.Text();
        var start = script.IndexOf("\ncontainer_args=(\n", StringComparison.Ordinal);
        var end = script.IndexOf("\ncontainer_args+=(${extra_args[@]+\"${extra_args[@]}\"})\n", StringComparison.Ordinal);
        (start > 0 && end > start).Should().BeTrue("the script gives the container its arguments, then EXTRA_DOCKER_ARGS");
        script[start..end].Split('\n')
            .Select(line => line.Trim())
            .Select(line => line.StartsWith("container_args+=(--", StringComparison.Ordinal) ? line["container_args+=(".Length..^1] : line)
            .Where(line => line.StartsWith("--", StringComparison.Ordinal))
            .Should().Equal(ContainerArgs.SelectMany(group => group.Lines), "the check is run with the script's container arguments, in its order");
        foreach (var name in ContainerArgs.Select(group => group.When).OfType<string>().Where(when => !when.StartsWith('!')))
        {
            script.Should().Contain($"if [[ -n \"${{{name}}}\" ]]; then", "each group is given when its setting is set");
        }

        foreach (var (name, value, orEmpty) in ScriptDefaults)
        {
            script.Should().Contain($"\n{name}=${{{name}{(orEmpty ? ":-" : "-")}{value}}}\n");
        }

        script.Should().Contain("read -r -d '' -a extra_args <<<\"${EXTRA_DOCKER_ARGS}\"", "EXTRA_DOCKER_ARGS is split at spaces")
            .And.Contain("OPERATOR_KEY=$(tr -d '\\r\\n' < \"${OPERATOR_KEY_FILE}\")")
            .And.Contain("deploy_key_sha256=$(printf '%s' \"${OPERATOR_KEY}\" | sha256_hex)")
            .And.Contain($"\"${{container_args[@]}}\" \\\n    {DeployKeyArgument} \"${{DEPLOY_IMAGE}}\" --validate-deployment");

        File.ReadAllText(Path.Combine(Root, "src", "HVO.RoofControllerV4.RPi", "Dockerfile")).Should()
            .Contain("\nFROM mcr.microsoft.com/dotnet/aspnet:")
            .And.Contain("\nENV ASPNETCORE_URLS=http://+:8080 \\\n    ASPNETCORE_ENVIRONMENT=Production \\\n    TZ=UTC \\\n")
            .And.Contain("\n    RoofWeb__Urls=http://+:8088\n");
        File.Exists(Path.Combine(Path.GetDirectoryName(ProductionOptions.AppSettingsPath)!, "appsettings.Production.json"))
            .Should().BeFalse("the check reads appsettings.json alone in Production");
    }

    // A Pi with the HAT and the folders Raspberry Pi OS has, where the person types each password and PIN twice.
    private static FakeMachine Pi()
    {
        var pi = new FakeMachine().WithPi().Folder("/usr/local/sbin").Folder("/var/lib");
        pi.Types = what => what.Replace(" again", string.Empty, StringComparison.Ordinal) switch
        {
            "the first admin's password" => Password,
            "the first admin's PIN" => Pin,
            "the camera's password" => CameraPassword,
            _ => null
        };
        return pi;
    }

    // docs/install.md's answers file for the controller alone, saved in the home folder.
    private static string DocsAnswers(FakeMachine pi)
    {
        var path = Path.Join(pi.Home, "controller.json");
        var blocks = JsonBlock().Matches(File.ReadAllText(Path.Combine(Root, "docs", "install.md")))
            .Select(block => block.Groups[1].Value)
            .Where(block => block.Contains("\"roles\": [\"controller\"],", StringComparison.Ordinal))
            .ToList();
        blocks.Should().ContainSingle("docs/install.md has one answers file for the controller alone");
        pi.Write(path, blocks[0]);
        return path;
    }

    // Runs the controller's deployment check each time the deploy script runs, as its pre-flight does (after anything
    // the test does first, and with any one change to the script's settings), and stops the script when the check
    // fails, as the pre-flight does.
    private static List<DeploymentValidationResult> PreFlight(FakeMachine pi, Action? before = null, (string Name, string Value)? change = null)
    {
        var checks = new List<DeploymentValidationResult>();
        pi.DuringDeploy = () =>
        {
            before?.Invoke();
            var settings = new Dictionary<string, string>(pi.Deploys[^1], StringComparer.Ordinal);
            if (change is { } changed)
            {
                settings[changed.Name] = changed.Value;
            }

            var check = DeploymentCheck(pi, settings);
            checks.Add(check);
            pi.DeployFailure = check.IsValid ? null : PreFlightFailed;
        };
        return checks;
    }

    // The controller's deployment check in the container the deploy script makes from its settings,
    // configured as Program configures it: the ASPNETCORE_ variables lowest, then appsettings.json (there is no
    // appsettings.Production.json), the settings files, the environment, and the secrets folder, one file per setting.
    private static DeploymentValidationResult DeploymentCheck(FakeMachine pi, IReadOnlyDictionary<string, string> settings)
    {
        var variables = new Dictionary<string, string>(settings, StringComparer.Ordinal);
        foreach (var (setting, value, orEmpty) in ScriptDefaults)
        {
            if (!settings.TryGetValue(setting, out var set) || (orEmpty && set.Length == 0))
            {
                variables[setting] = value;
            }
        }

        variables["HAT_EMULATOR_ENDPOINT"].Should().BeEmpty("the real HAT: the script maps its devices, and no emulator");
        var key = settings["ROOF_OPERATOR_API_KEY"] is { Length: > 0 } given
            ? given
            : pi.Read(variables["OPERATOR_KEY_FILE"]).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);
        variables["deploy_key_sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

        List<string> arguments =
        [
            .. ContainerArgs
                .Where(group => group.When is null || (variables[group.When.TrimStart('!')].Length == 0) == group.When.StartsWith('!'))
                .SelectMany(group => group.Lines)
                .SelectMany(line => Words(line, variables)),
            .. variables["EXTRA_DOCKER_ARGS"].Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries),
            .. Words(DeployKeyArgument, variables)
        ];
        var (environment, mounts, devices) = Container(pi, arguments);

        var name = environment.GetValueOrDefault("ASPNETCORE_ENVIRONMENT", Environments.Production);
        var contentRoot = Path.GetDirectoryName(ProductionOptions.AppSettingsPath)!;
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(Configured(environment.Where(setting => setting.Key.StartsWith("ASPNETCORE_", StringComparison.Ordinal)), "ASPNETCORE_".Length));
        configuration.AddJsonFile(ProductionOptions.AppSettingsPath, optional: false, reloadOnChange: false);
        configuration.AddJsonFile(Path.Join(contentRoot, $"appsettings.{name}.json"), optional: true, reloadOnChange: false);
        configuration.AddInMemoryCollection(Configured(environment, 0));
        configuration.AddKeyPerFile(pi.OnDisk(mounts[ApiKeyFiles.ContainerSecrets]), optional: true);
        configuration.AddRoofSettingsFiles(name, contentRoot);
        var host = new HostingEnvironment
        {
            EnvironmentName = name,
            ApplicationName = "HVO.RoofControllerV4.RPi",
            ContentRootPath = contentRoot,
            ContentRootFileProvider = new NullFileProvider()
        };
        return DeploymentValidator.Validate(configuration, host, FakeMachine.Clock, hatBusPresent: devices.Contains(DeploymentValidator.HatBusDevicePath));
    }

    // A line of the script as bash reads it: its words, unquoted, with ${NAME} given.
    private static IEnumerable<string> Words(string line, IReadOnlyDictionary<string, string> variables)
        => Word().Matches(line).Select(word => Variable().Replace(
            word.Groups[1].Success ? word.Groups[1].Value : word.Value,
            variable => variables[variable.Groups[1].Value]));

    // The container Docker makes from the arguments: the image's environment with each --env after it (the last one wins),
    // a container path that is mounted read from the fake Pi, and each mount's source and device there, as Docker needs.
    private static (Dictionary<string, string> Environment, Dictionary<string, string> Mounts, List<string> Devices) Container(FakeMachine pi, List<string> arguments)
    {
        var environment = new Dictionary<string, string>(ImageEnvironment, StringComparer.Ordinal);
        var mounts = new Dictionary<string, string>(StringComparer.Ordinal);
        var devices = new List<string>();
        (arguments.Count % 2).Should().Be(0, "each option has its value");
        for (var index = 0; index < arguments.Count; index += 2)
        {
            var value = arguments[index + 1];
            switch (arguments[index])
            {
                case "--env":
                    var equals = value.IndexOf('=', StringComparison.Ordinal);
                    (equals > 0).Should().BeTrue($"--env {index / 2} gives a value");
                    environment[value[..equals]] = value[(equals + 1)..];
                    break;
                case "--mount":
                    var fields = value.Split(',').Select(field => field.Split('=', 2)).ToDictionary(field => field[0], field => field.Length > 1 ? field[1] : string.Empty, StringComparer.Ordinal);
                    fields["type"].Should().Be("bind");
                    pi.Exists(fields["src"]).Should().BeTrue($"Docker mounts {fields["src"]} only when it is on the Pi");
                    mounts[fields["dst"]] = fields["src"];
                    break;
                case "--device":
                    var (onPi, inside) = (value.Split(':')[0], value.Split(':')[1]);
                    pi.Exists(onPi).Should().BeTrue($"Docker maps {onPi} only when it is on the Pi");
                    devices.Add(inside);
                    break;
                default:
                    Assert.Fail($"The model reads --env, --mount and --device, not option {index / 2}.");
                    break;
            }
        }

        foreach (var (name, value) in environment.Where(setting => setting.Value.StartsWith('/')).ToList())
        {
            var target = mounts.Keys.Where(target => value == target || value.StartsWith(target + "/", StringComparison.Ordinal)).MaxBy(target => target.Length);
            target.Should().NotBeNull($"{name} names a path the container has only through a mount");
            environment[name] = pi.OnDisk(mounts[target!] + value[target!.Length..]);
        }

        return (environment, mounts, devices);
    }

    // Environment variables as configuration reads them: the prefix dropped, and __ between sections.
    private static IEnumerable<KeyValuePair<string, string?>> Configured(IEnumerable<KeyValuePair<string, string>> environment, int prefix)
        => environment.Select(setting => KeyValuePair.Create(setting.Key[prefix..].Replace("__", ":", StringComparison.Ordinal), (string?)setting.Value));

    // Whether any of the secrets is in the text, said without saying which.
    private static bool Shows(string text, IEnumerable<string> secrets) => secrets.Any(secret => text.Contains(secret, StringComparison.Ordinal));

    // 32 random bytes as base64url, as the installer makes each key and password.
    private static bool IsRandom(string value) => RandomValue().IsMatch(value);

    [GeneratedRegex(@"```json\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex JsonBlock();

    [GeneratedRegex(@"""([^""]*)""|[^\s""]+")]
    private static partial Regex Word();

    [GeneratedRegex(@"\$\{(\w+)\}")]
    private static partial Regex Variable();

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{43}\z")]
    private static partial Regex RandomValue();

    [GeneratedRegex(@"\AKestrel:Certificates:Default: CN=roofpi, valid \d{4}-\d{2}-\d{2} to \d{4}-\d{2}-\d{2}, thumbprint [0-9A-F]{40}\.\z")]
    private static partial Regex CertificateNote();
}
