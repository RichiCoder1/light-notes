using System.Text.Json.Serialization;

namespace LightNotes.Storage;

internal sealed record NoteExportDocument(
    int SchemaVersion,
    DateTimeOffset ExportedAtUtc,
    List<NoteRecord> Notes
);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(NoteExportDocument))]
internal sealed partial class NoteExportJsonContext : JsonSerializerContext;
