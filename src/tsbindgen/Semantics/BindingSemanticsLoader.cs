using System.Text.Json;

namespace tsbindgen.Surface;

public static class BindingSemanticsLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = false
    };

    public static IReadOnlyList<BindingSemanticsSpec> LoadMany(IEnumerable<string> paths)
    {
        return paths.Select(Load).ToArray();
    }

    public static BindingSemanticsSpec Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Bindings semantics config not found: {path}");
        }

        var json = File.ReadAllText(path);
        var parsed = JsonSerializer.Deserialize<BindingSemanticsSpec>(json, JsonOptions);
        if (parsed is null)
        {
            throw new InvalidOperationException($"Failed to parse bindings semantics config: {path}");
        }

        Validate(parsed, path);
        return parsed;
    }

    internal static void Validate(BindingSemanticsSpec spec, string path)
    {
        if (spec.SchemaVersion != 1)
        {
            throw new InvalidOperationException(
                $"Unsupported bindings semantics schemaVersion in {path}: {spec.SchemaVersion}");
        }

        foreach (var rule in spec.MemberSemantics)
        {
            ValidateMemberSemanticsRule(rule, path, "memberSemantics");
        }
    }

    internal static void ValidateMemberSemanticsRule(
        BindingMemberSemanticsRuleSpec rule,
        string path,
        string location)
    {
        if (string.IsNullOrWhiteSpace(rule.Binding.Type))
        {
            throw new InvalidOperationException($"{path}: {location}.binding.type must be non-empty.");
        }

        if (rule.EmitSemantics is null || string.IsNullOrWhiteSpace(rule.EmitSemantics.CallStyle))
        {
            throw new InvalidOperationException($"{path}: {location}.emitSemantics.callStyle must be non-empty.");
        }

        if (rule.EmitSemantics.CallStyle is not ("receiver" or "static"))
        {
            throw new InvalidOperationException(
                $"{path}: {location}.emitSemantics.callStyle must be 'receiver' or 'static'.");
        }
    }
}
