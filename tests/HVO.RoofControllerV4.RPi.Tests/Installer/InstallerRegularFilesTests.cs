using System.Net.Sockets;
using System.Runtime.Versioning;
using FluentAssertions;
using HVO.RoofControllerV4.Installer.Machine;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>Platform-specific open errors retain the regular-file guard's refusal contract.</summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerRegularFilesTests
{
    /// <summary>Darwin's distinct socket error and existing unsupported-file errors identify the logical store.</summary>
    [TestMethod]
    [DataRow(102, true)]
    [DataRow(45, true)]
    [DataRow(95, false)]
    [DataRow(6, false)]
    public void UnsupportedFileOpenErrors_AreTypedNonRegularRefusals(int error, bool isMacOS)
    {
        Action open = () => RegularFiles.OpenFailure(error, "/physical/state.json", "/logical/store.json", isMacOS);

        var failure = open.Should().ThrowExactly<NotARegularFileException>().Which;
        failure.Kind.Should().Be("a socket or a device");
        failure.Message.Should().StartWith("/logical/store.json is ");
    }

    /// <summary>Linux error 102 and unknown errors keep their I/O classification and original errno.</summary>
    [TestMethod]
    [DataRow(102, false)]
    [DataRow(12345, false)]
    [DataRow(12345, true)]
    public void OtherOpenErrors_RemainGenericIOExceptions(int error, bool isMacOS)
    {
        Action open = () => RegularFiles.OpenFailure(error, "/physical/state.json", "/logical/store.json", isMacOS);

        var failure = open.Should().ThrowExactly<IOException>().Which;
        failure.HResult.Should().Be(error);
        failure.Message.Should().Contain("/physical/state.json");
    }

    /// <summary>Missing paths still produce no handle.</summary>
    [TestMethod]
    [DataRow(2)]
    [DataRow(20)]
    public void MissingPaths_StillReturnNoHandle(int error)
    {
        RegularFiles.OpenFailure(error, "/physical/state.json", "/logical/store.json", false).Should().BeNull();
    }

    /// <summary>Permission failures still identify the physical path.</summary>
    [TestMethod]
    [DataRow(1)]
    [DataRow(13)]
    public void DeniedPaths_KeepTheirPhysicalPath(int error)
    {
        Action open = () => RegularFiles.OpenFailure(error, "/physical/state.json", "/logical/store.json", false);

        open.Should().ThrowExactly<UnauthorizedAccessException>().WithMessage("Access to the path '/physical/state.json' is denied.");
    }

    /// <summary>A real bound Unix-domain socket is refused through the production read path without waiting on it.</summary>
    [TestMethod]
    [Timeout(10_000)]
    public void ARealUnixSocket_IsRefusedBeforeReading()
    {
        // Keep the bound pathname below Darwin's socket-path limit, independently of a long inherited TMPDIR.
        var folder = Path.Join("/tmp", "hvo-regular-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var path = Path.Join(folder, "socket");
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(path));

            Action read = () => RegularFiles.Read(path, "/logical/store.json");

            var failure = read.Should().ThrowExactly<NotARegularFileException>().Which;
            failure.Kind.Should().Be("a socket or a device");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
