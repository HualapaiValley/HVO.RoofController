using System.Net;
using HVO.RoofControllerV4.RPi.Middleware;
using HVO.RoofControllerV4.RPi.Security;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace HVO.RoofControllerV4.RPi.Tests.Middleware;

[TestClass]
public sealed class RequireHttpsMiddlewareTests
{
    [TestMethod]
    [DataRow("127.0.0.1", true)]
    [DataRow("127.0.0.2", true)]
    [DataRow("::1", true)]
    [DataRow("::ffff:127.0.0.1", true)]
    [DataRow("192.168.1.5", false)]
    [DataRow("::ffff:192.168.1.5", false)]
    [DataRow("fe80::1", false)]
    public void IsLoopback_RecognisesLocalPeers(string address, bool expected)
    {
        Assert.AreEqual(expected, RequireHttpsMiddleware.IsLoopback(IPAddress.Parse(address)));
    }

    [TestMethod]
    public void IsLoopback_NullAddress_IsTreatedAsLocal()
    {
        Assert.IsTrue(RequireHttpsMiddleware.IsLoopback(null));
    }

    [TestMethod]
    [DataRow(null, "Development", false)]
    [DataRow(null, "Production", true)]
    [DataRow(null, "Staging", true)]
    [DataRow(true, "Development", true)]
    [DataRow(true, "Production", true)]
    [DataRow(false, "Development", false)]
    [DataRow(false, "Production", false)]
    public void IsHttpsRequired_DefaultsOnOutsideDevelopment(bool? configured, string environment, bool expected)
    {
        var options = new RoofControllerSecurityOptions { RequireHttps = configured };

        Assert.AreEqual(expected, RequireHttpsMiddleware.IsHttpsRequired(options, new StubHostEnvironment(environment)));
    }

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "HVO.RoofControllerV4.RPi.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
