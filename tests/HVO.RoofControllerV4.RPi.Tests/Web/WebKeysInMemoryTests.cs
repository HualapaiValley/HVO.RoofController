using System.Xml.Linq;
using FluentAssertions;
using HVO.RoofControllerV4.Web.Sessions;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>The web UI's keys when it has no directory for them: kept in memory, each element as it was stored.</summary>
[TestClass]
public sealed class WebKeysInMemoryTests
{
    [TestMethod]
    public void StoredElements_AreReturned_AsTheyWereStored()
    {
        var keys = new WebKeysInMemory();
        keys.GetAllElements().Should().BeEmpty();
        var first = new XElement("key", new XAttribute("id", "1"));

        keys.StoreElement(first, "key-1");
        keys.StoreElement(new XElement("key", new XAttribute("id", "2")), "key-2");
        first.SetAttributeValue("id", "changed");
        keys.GetAllElements().First().SetAttributeValue("id", "changed too");

        keys.GetAllElements().Select(element => (string?)element.Attribute("id")).Should().Equal("1", "2");
    }
}
