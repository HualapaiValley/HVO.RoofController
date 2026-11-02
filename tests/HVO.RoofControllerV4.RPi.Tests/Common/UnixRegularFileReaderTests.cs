using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using FluentAssertions;
using HVO.RoofControllerV4.Common.IO;

namespace HVO.RoofControllerV4.RPi.Tests.Files;

[TestClass]
public sealed class UnixRegularFileReaderTests
{
    [TestMethod]
    [DataRow(false, Architecture.X64, 0xA0800)]
    [DataRow(false, Architecture.Arm64, 0x88800)]
    [DataRow(true, Architecture.Arm64, 0x1000104)]
    public void NativeOpenFlags_UseTheSupportedOperatingSystemAndProcessAbi(bool macOS, Architecture architecture, int expected)
    {
        // The Common API exposes reads, not native flags; these ABI checks cover targets other than this test runner.
        var method = typeof(UnixRegularFileReader).GetMethod("FlagsForOpen", BindingFlags.Static | BindingFlags.NonPublic)!;
        method.Invoke(null, [macOS, architecture]).Should().Be(expected);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ValidRegularFiles_KeepUnicodeBomSupport_AndThe4096ByteBoundary(bool utf16)
    {
        var directory = Directory.CreateTempSubdirectory("hvo-regular-metadata-").FullName;
        var path = Path.Combine(directory, "metadata.json");
        try
        {
            File.WriteAllText(path, "{\"name\":\"weather\"}", utf16 ? Encoding.Unicode : new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            UnixRegularFileReader.ReadAllText(path).Should().Be("{\"name\":\"weather\"}");
            File.WriteAllText(path, new string('x', 4096));
            UnixRegularFileReader.ReadAllText(path).Should().HaveLength(4096);
            File.AppendAllText(path, "x");
            Action read = () => UnixRegularFileReader.ReadAllText(path);
            read.Should().Throw<IOException>().Which.Message.Should().NotContain(path);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public void MissingAndNonRegularPaths_AreRefusedBeforeAnyPayloadIsRead()
    {
        var directory = Directory.CreateTempSubdirectory("hvo-regular-metadata-kinds-").FullName;
        try
        {
            Action missing = () => UnixRegularFileReader.ReadAllText(Path.Combine(directory, "absent"));
            missing.Should().Throw<FileNotFoundException>();
            Action folder = () => UnixRegularFileReader.ReadAllText(directory);
            folder.Should().Throw<IOException>();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
