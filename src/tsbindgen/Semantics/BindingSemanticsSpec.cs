using System.Text.Json.Serialization;

namespace tsbindgen.Surface;

public sealed class EmitSemanticsSpec
{
    [JsonPropertyName("callStyle")]
    public string? CallStyle { get; init; }

    [JsonPropertyName("callableStaticAccessorKind")]
    public string? CallableStaticAccessorKind { get; init; }
}

public sealed class TypeSemanticsSpec
{
    [JsonPropertyName("contributesTypeIdentity")]
    public bool ContributesTypeIdentity { get; init; }
}

public sealed class BindingSemanticsTargetSpec
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("member")]
    public string? Member { get; init; }
}

public sealed class BindingMemberSemanticsRuleSpec
{
    [JsonPropertyName("binding")]
    public BindingSemanticsTargetSpec Binding { get; init; } = new();

    [JsonPropertyName("emitSemantics")]
    public EmitSemanticsSpec EmitSemantics { get; init; } = new();
}

public sealed class BindingSemanticsSpec
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("memberSemantics")]
    public IReadOnlyList<BindingMemberSemanticsRuleSpec> MemberSemantics { get; init; } = [];
}
