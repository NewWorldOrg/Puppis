using System.Text.Json.Serialization;

namespace Puppis.Driver.Reception;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true
)]
[JsonSerializable(typeof(ReceptionRecords))]
internal sealed partial class ReceptionJsonContext : JsonSerializerContext;
