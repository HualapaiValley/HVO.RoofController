using System.Text.Json;
using System.Text.Json.Serialization;

namespace HVO.RoofControllerV4.iPad.Services;

/// <summary>
/// Serializer settings matching the controller: camelCase properties and enums as their PascalCase names.
/// </summary>
public static class RoofControllerJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
