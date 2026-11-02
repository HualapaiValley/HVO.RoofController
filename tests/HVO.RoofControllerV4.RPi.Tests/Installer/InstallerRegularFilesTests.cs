using System.Net.Sockets;
using System.Runtime.InteropServices;
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
    /// <summary>Native flag values follow the target ABI even when this test runs on another architecture.</summary>
    [TestMethod]
    [DataRow(false, Architecture.X64, false, 0x80800)]
    [DataRow(false, Architecture.X64, true, 0xA0800)]
    [DataRow(false, Architecture.Arm64, false, 0x80800)]
    [DataRow(false, Architecture.Arm64, true, 0x88800)]
    [DataRow(true, Architecture.Arm64, false, 0x1000004)]
    [DataRow(true, Architecture.Arm64, true, 0x1000104)]
    public void OpenFlags_UseTheOperatingSystemAndProcessAbi(bool isMacOS, Architecture architecture, bool noFollow, int expected)
    {
        // Linux arm64/include/uapi/asm/fcntl.h defines O_NOFOLLOW as octal 0100000 (0x8000),
        // unlike x64's asm-generic/fcntl.h octal 00400000 (0x20000). On ARM64 the latter means O_LARGEFILE.
        // https://github.com/torvalds/linux/blob/v6.8/arch/arm64/include/uapi/asm/fcntl.h
        // Darwin's XNU bsd/sys/fcntl.h defines O_NOFOLLOW as 0x100.
        RegularFiles.FlagsForOpen(isMacOS, architecture, noFollow).Should().Be(expected);
    }

    /// <summary>A real symlink to readable regular data is refused rather than followed by the native open.</summary>
    [TestMethod]
    public void ARealSymbolicLink_IsRefusedBeforeReadingItsRegularTarget()
    {
        var folder = Path.Join(Path.GetTempPath(), "hvo-regular-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder, Modes.PrivateFolder);
        try
        {
            var target = Path.Join(folder, "target.json");
            var link = Path.Join(folder, "link.json");
            File.WriteAllText(target, "{\"dome/0\":\"00000000-0000-0000-0000-000000000001\"}");
            File.CreateSymbolicLink(link, target);

            Action read = () => RegularFiles.Read(link, "/logical/alpaca-devices.json", noFollow: true);

            read.Should().ThrowExactly<NotARegularFileException>().Which.Kind.Should().Be("a symbolic link");
            RegularFiles.Read(target, "/logical/target.json", noFollow: true).Should().Equal(File.ReadAllBytes(target));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

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
