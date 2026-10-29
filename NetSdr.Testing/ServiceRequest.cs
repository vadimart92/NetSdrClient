using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace NetSdr.Testing;

/// <summary>One request a client sent to a <see cref="SupervisionService"/>, as read and, where it could be, parsed.</summary>
public sealed class ServiceRequest
{
    private ServiceRequest(string line, JsonElement? json, string? command)
    {
        Line = line;
        Json = json;
        Command = command;
    }

    /// <summary>
    /// The request as received: the JSON object from its opening to its closing brace, or, for something that is not
    /// JSON, the line without its line break.
    /// </summary>
    public string Line { get; }

    /// <summary>The parsed object; <see langword="null"/> when the request is not a JSON object.</summary>
    public JsonElement? Json { get; }

    /// <summary>
    /// The string in the field <see cref="SupervisionService.CommandField"/>; <see langword="null"/> when the request is
    /// not a JSON object or that field is missing or not a string.
    /// </summary>
    public string? Command { get; }

    /// <summary>Whether the request has the field <paramref name="field"/>, whatever its value.</summary>
    public bool Has(string field) => Json is { } json && json.TryGetProperty(field, out _);

    /// <summary>
    /// Reads the field <paramref name="field"/> as <typeparamref name="T"/>, with the web defaults of System.Text.Json
    /// (names case-insensitive, numbers also from strings); <c>default</c> when the field is missing.
    /// </summary>
    /// <exception cref="JsonException">The value cannot be read as <typeparamref name="T"/>.</exception>
    public T? Get<T>(string field) =>
        Json is { } json && json.TryGetProperty(field, out JsonElement value)
            ? value.Deserialize<T>(SupervisionService.JsonOptions)
            : default;

    /// <summary>Reads the field <paramref name="field"/> as <typeparamref name="T"/>; false when it is missing, null or of another type.</summary>
    public bool TryGet<T>(string field, [MaybeNullWhen(false)] out T value)
    {
        value = default;
        if (Json is not { } json || !json.TryGetProperty(field, out JsonElement element))
        {
            return false;
        }

        try
        {
            value = element.Deserialize<T>(SupervisionService.JsonOptions);
            return value is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Parses a request; one that is not a JSON object is kept with <see cref="Json"/> null.</summary>
    internal static ServiceRequest Parse(string line, string commandField)
    {
        JsonElement root;
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new ServiceRequest(line, null, null);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return new ServiceRequest(line, null, null);
        }

        string? command = root.TryGetProperty(commandField, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
        return new ServiceRequest(line, root, command);
    }
}
