using System.Text.Json;
using System.Text.Json.Serialization;

namespace HVO.RoofControllerV4.Client;

/// <summary>
/// JSON as the controller writes it: web defaults (camelCase names, case-insensitive reads) and enums as their names.
/// </summary>
public static class RoofClientJson
{
    /// <summary>Options for reading and writing API bodies and hub messages. Shared and read-only once used.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    /// <summary>A new, writable copy of <see cref="Options"/>.</summary>
    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
