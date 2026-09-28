using System.Text.Json;
using System.Text.Json.Serialization;

namespace HVO.RoofControllerV4.Emulator;

/// <summary>
/// Enum values by name only, as <see cref="JsonStringEnumConverter"/> reads and writes them, except that a list of names
/// ("Open, Closed") is refused unless the enum is <see cref="FlagsAttribute"/>. The default reads a list as the names'
/// OR, which for a plain enum can be another defined value: a request for one thing would inject a different one.
/// </summary>
internal sealed class NamedEnumConverter : JsonConverterFactory
{
    private readonly JsonStringEnumConverter _names = new(namingPolicy: null, allowIntegerValues: false);

    public override bool CanConvert(Type typeToConvert) => _names.CanConvert(typeToConvert);

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var names = _names.CreateConverter(typeToConvert, options);
        return typeToConvert.IsDefined(typeof(FlagsAttribute), inherit: false)
            ? names
            : (JsonConverter?)Activator.CreateInstance(typeof(OneName<>).MakeGenericType(typeToConvert), names);
    }

    private sealed class OneName<T>(JsonConverter<T> names) : JsonConverter<T>
        where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            RefuseAList(ref reader);
            return names.Read(ref reader, typeToConvert, options);
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => names.Write(writer, value, options);

        public override T ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            RefuseAList(ref reader);
            return names.ReadAsPropertyName(ref reader, typeToConvert, options);
        }

        public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => names.WriteAsPropertyName(writer, value, options);

        private static void RefuseAList(ref Utf8JsonReader reader)
        {
            if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName && reader.GetString()!.Contains(','))
            {
                throw new JsonException($"A {typeof(T).Name} is one name, not a list.");
            }
        }
    }
}
