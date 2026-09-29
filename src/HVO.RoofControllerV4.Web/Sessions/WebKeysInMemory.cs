using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace HVO.RoofControllerV4.Web.Sessions;

/// <summary>
/// Where the web UI keeps the keys that protect its sign-in cookie and forms when it has no directory for them
/// (<see cref="RoofWebOptions.DataProtectionPath"/>): in memory, so everyone signs in again when it restarts.
/// </summary>
/// <remarks>
/// It is the key manager's store, not a provider in place of it: ASP.NET Core makes its first key at start through the
/// key manager whatever provider the pages use, and without a store of its own the key manager writes that key under
/// <c>$HOME/.aspnet</c>, a directory nobody chose.
/// </remarks>
public sealed class WebKeysInMemory : IXmlRepository
{
    private readonly List<XElement> _elements = [];
    private readonly Lock _lock = new();

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        lock (_lock)
        {
            return _elements.Select(element => new XElement(element)).ToList();
        }
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        lock (_lock)
        {
            _elements.Add(new XElement(element));
        }
    }
}
