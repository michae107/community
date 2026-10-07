#if TOOLS || DEBUG
using System.Collections;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TalonVoice;

internal static class VoiceJson
{
    // Avoid JsonSerializer's shared type cache pinning collectible Godot assemblies.
    public static void Write(string path, object value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteValue(writer, value);
        File.WriteAllText(path + ".tmp", Encoding.UTF8.GetString(stream.ToArray()));
        File.Move(path + ".tmp", path, true);
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case string text: writer.WriteStringValue(text); break;
            case bool flag: writer.WriteBooleanValue(flag); break;
            case int number: writer.WriteNumberValue(number); break;
            case long number: writer.WriteNumberValue(number); break;
            case double number: writer.WriteNumberValue(number); break;
            case IEnumerable values:
                writer.WriteStartArray();
                foreach (object? item in values) WriteValue(writer, item);
                writer.WriteEndArray();
                break;
            default:
                writer.WriteStartObject();
                foreach (var property in value.GetType().GetProperties())
                {
                    writer.WritePropertyName(property.Name);
                    WriteValue(writer, property.GetValue(value));
                }
                writer.WriteEndObject();
                break;
        }
    }
}
#endif
