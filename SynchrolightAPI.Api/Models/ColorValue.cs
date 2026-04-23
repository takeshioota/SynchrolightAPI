using System.Text.Json;
using System.Text.Json.Serialization;
using SynchrolightAPI.Domain;

namespace SynchrolightAPI.Api.Models;

/// <summary>
/// 色指定: RGB値オブジェクト {"r":255,"g":0,"b":0} またはプリセット名 "red" の両方に対応
/// </summary>
[JsonConverter(typeof(ColorValueConverter))]
public class ColorValue
{
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }

    public Rgb ToRgb() => new(R, G, B);

    private static readonly Dictionary<string, (byte r, byte g, byte b)> Presets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = (255, 0, 0),
        ["green"] = (0, 255, 0),
        ["blue"] = (0, 0, 255),
        ["white"] = (255, 255, 255),
        ["black"] = (0, 0, 0),
    };

    public static ColorValue FromPreset(string name)
    {
        if (!Presets.TryGetValue(name, out var p))
            throw new ArgumentException($"Unknown preset color: {name}");
        return new ColorValue { R = p.r, G = p.g, B = p.b };
    }

    public static bool TryParsePreset(string name, out ColorValue? color)
    {
        if (Presets.TryGetValue(name, out var p))
        {
            color = new ColorValue { R = p.r, G = p.g, B = p.b };
            return true;
        }
        color = null;
        return false;
    }
}

public class ColorValueConverter : JsonConverter<ColorValue>
{
    public override ColorValue? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var name = reader.GetString()!;
            return ColorValue.FromPreset(name);
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            byte r = 0, g = 0, b = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    var prop = reader.GetString()!;
                    reader.Read();
                    switch (prop.ToLowerInvariant())
                    {
                        case "r": r = (byte)reader.GetInt32(); break;
                        case "g": g = (byte)reader.GetInt32(); break;
                        case "b": b = (byte)reader.GetInt32(); break;
                    }
                }
            }
            return new ColorValue { R = r, G = g, B = b };
        }

        throw new JsonException("Color must be a string preset name or an {r,g,b} object.");
    }

    public override void Write(Utf8JsonWriter writer, ColorValue value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("r", value.R);
        writer.WriteNumber("g", value.G);
        writer.WriteNumber("b", value.B);
        writer.WriteEndObject();
    }
}
