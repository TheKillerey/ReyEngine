using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ReyEngine.Formats.Characters.Simulation;

/// <summary>
/// M867: the on-disk form of a <see cref="ChampionSimDefinition"/>. Indented, camelCase, properties in declaration order,
/// LF line ends and no byte-order mark, so the same definition always produces the same bytes and
/// <c>Write(Read(bytes))</c> is byte-identical to <c>bytes</c>.
/// </summary>
public static class ChampionSimJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static byte[] Write(ChampionSimDefinition definition) =>
        new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(definition, Options) + "\n");

    public static string WriteString(object value) => JsonSerializer.Serialize(value, Options) + "\n";

    public static ChampionSimDefinition Read(byte[] json) =>
        JsonSerializer.Deserialize<ChampionSimDefinition>(json, Options)
        ?? throw new InvalidDataException("not a champion simulation definition");
}
