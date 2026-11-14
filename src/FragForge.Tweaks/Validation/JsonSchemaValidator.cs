using System.Text.Json;
using System.Text.RegularExpressions;

namespace FragForge.Tweaks.Validation;

public sealed class JsonSchemaValidator
{
    private readonly JsonElement _schema;
    public JsonSchemaValidator(string schemaJson) => _schema = JsonDocument.Parse(schemaJson).RootElement.Clone();
    public IReadOnlyList<string> Validate(JsonElement instance)
    {
        var errors = new List<string>();
        Validate(instance, _schema, "$", errors, 0);
        return errors;
    }
    private void Validate(JsonElement instance, JsonElement schema, string path, List<string> errors, int depth)
    {
        if (depth > 64) { errors.Add($"{path}: profundidad máxima excedida"); return; }
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var pointer = reference.GetString()!;
            if (!pointer.StartsWith("#/", StringComparison.Ordinal)) { errors.Add($"{path}: solo se admiten referencias locales"); return; }
            var resolved = _schema;
            foreach (var segment in pointer[2..].Split('/')) resolved = resolved.GetProperty(segment.Replace("~1", "/").Replace("~0", "~"));
            Validate(instance, resolved, path, errors, depth + 1);
            return;
        }
        if (schema.TryGetProperty("type", out var type))
        {
            var expected = type.GetString();
            var valid = expected switch
            {
                "object" => instance.ValueKind == JsonValueKind.Object,
                "array" => instance.ValueKind == JsonValueKind.Array,
                "string" => instance.ValueKind == JsonValueKind.String,
                "boolean" => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer" => instance.ValueKind == JsonValueKind.Number && instance.TryGetInt64(out _),
                "number" => instance.ValueKind == JsonValueKind.Number,
                _ => true
            };
            if (!valid) { errors.Add($"{path}: se esperaba {expected}, se obtuvo {instance.ValueKind}"); return; }
        }
        if (schema.TryGetProperty("enum", out var enumeration) && !enumeration.EnumerateArray().Any(e => JsonElement.DeepEquals(e, instance))) errors.Add($"{path}: valor fuera de enum");
        if (instance.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required)) foreach (var property in required.EnumerateArray()) if (!instance.TryGetProperty(property.GetString()!, out _)) errors.Add($"{path}: falta '{property.GetString()}'");
            var properties = schema.TryGetProperty("properties", out var p) ? p : default;
            var additional = !schema.TryGetProperty("additionalProperties", out var a) || a.ValueKind != JsonValueKind.False;
            foreach (var property in instance.EnumerateObject())
            {
                if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var child)) Validate(property.Value, child, path + "." + property.Name, errors, depth + 1);
                else if (!additional) errors.Add($"{path}: propiedad no permitida '{property.Name}'");
            }
        }
        if (instance.ValueKind == JsonValueKind.Array)
        {
            if (schema.TryGetProperty("minItems", out var min) && instance.GetArrayLength() < min.GetInt32()) errors.Add($"{path}: menos de {min.GetInt32()} elementos");
            if (schema.TryGetProperty("items", out var items))
            {
                var index = 0;
                foreach (var item in instance.EnumerateArray()) Validate(item, items, $"{path}[{index++}]", errors, depth + 1);
            }
        }
        if (instance.ValueKind == JsonValueKind.String)
        {
            var value = instance.GetString()!;
            if (schema.TryGetProperty("minLength", out var min) && value.Length < min.GetInt32()) errors.Add($"{path}: longitud mínima {min.GetInt32()}");
            if (schema.TryGetProperty("pattern", out var pattern) && !Regex.IsMatch(value, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) errors.Add($"{path}: no cumple el patrón");
        }
        if (instance.ValueKind == JsonValueKind.Number)
        {
            if (schema.TryGetProperty("minimum", out var min) && instance.GetDouble() < min.GetDouble()) errors.Add($"{path}: menor que mínimo");
            if (schema.TryGetProperty("maximum", out var max) && instance.GetDouble() > max.GetDouble()) errors.Add($"{path}: mayor que máximo");
        }
    }
}
