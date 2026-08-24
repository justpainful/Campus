using System.Text.Json;
using System.Text.Json.Serialization;

namespace Campus.Domain;

/// <summary>
/// Writes an id as the string it is.
///
/// Without this, a struct wrapping one string serialises as an object with a "value" field, and
/// then cannot be read back at all — <see cref="CampusId"/> has no public constructor for the
/// serialiser to call. Any payload holding a list of ids, such as a print job's queue or a
/// collection's members, depends on this converter to survive a round trip.
/// </summary>
public sealed class CampusIdJsonConverter : JsonConverter<CampusId>
{
    public override CampusId Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        // The object form is what an earlier build wrote before this converter existed. Reading
        // it costs a few lines and saves anyone who already has a workspace on disk.
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var depth = reader.CurrentDepth;
            string? found = null;

            while (reader.Read() && !(reader.TokenType == JsonTokenType.EndObject
                                      && reader.CurrentDepth == depth))
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                var name = reader.GetString();
                reader.Read();
                if (string.Equals(name, "value", StringComparison.OrdinalIgnoreCase))
                    found = reader.GetString();
            }

            return CampusId.TryParse(found, out var recovered) ? recovered : CampusId.Empty;
        }

        return CampusId.TryParse(reader.GetString(), out var id) ? id : CampusId.Empty;
    }

    public override void Write(Utf8JsonWriter writer, CampusId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);

    /// <summary>Handles the nullable form, which is what optional links look like.</summary>
    public sealed class Nullable : JsonConverter<CampusId?>
    {
        private static readonly CampusIdJsonConverter Inner = new();

        public override CampusId? Read(
            ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            => reader.TokenType == JsonTokenType.Null ? null : Inner.Read(ref reader, type, options);

        public override void Write(Utf8JsonWriter writer, CampusId? value, JsonSerializerOptions options)
        {
            if (value is null) writer.WriteNullValue();
            else writer.WriteStringValue(value.Value.Value);
        }
    }
}
