using FluentAssertions;
using HVO.RoofControllerV4.RPi.Storage;
using HVO.RoofControllerV4.RPi.Tests.Security;

namespace HVO.RoofControllerV4.RPi.Tests.Storage;

/// <summary>Flushing a directory after a rename: done on Linux, and never an error anywhere.</summary>
[TestClass]
public sealed class RoofDirectorySyncTests
{
    [TestMethod]
    public void ADirectory_IsFlushedOnLinux()
    {
        using var directory = new TemporaryDirectory();

        RoofDirectorySync.TryFlush(directory.Path).Should().Be(OperatingSystem.IsLinux());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("/no/such/directory/for/hvo-roof")]
    public void APathThatCannotBeFlushed_IsReportedWithoutThrowing(string path)
    {
        RoofDirectorySync.TryFlush(path).Should().BeFalse();
    }
}
