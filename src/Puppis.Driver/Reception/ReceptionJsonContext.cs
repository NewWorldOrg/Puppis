using System.Text.Json.Serialization;

using Puppis.Driver.Ipc;

namespace Puppis.Driver.Reception;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true
)]
[JsonSerializable(typeof(ReceptionRecords))]
[JsonSerializable(typeof(ReceptionView))]
internal sealed partial class ReceptionJsonContext : JsonSerializerContext;
