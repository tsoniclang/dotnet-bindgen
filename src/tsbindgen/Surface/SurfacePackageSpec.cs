using System.Text.Json;
using System.Text.Json.Serialization;

namespace tsbindgen.Surface;

public sealed class SurfacePackageSpec
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("prependReferences")]
    public IReadOnlyList<SurfaceReferenceSpec> PrependReferences { get; init; } = [];

    [JsonPropertyName("declarationFiles")]
    public IReadOnlyList<SurfaceDeclarationFileSpec> DeclarationFiles { get; init; } = [];

    [JsonPropertyName("simpleBindings")]
    public IReadOnlyDictionary<string, SurfaceSimpleBindingSpec> SimpleBindings { get; init; } =
        new Dictionary<string, SurfaceSimpleBindingSpec>(StringComparer.Ordinal);

    [JsonPropertyName("memberSemantics")]
    public IReadOnlyList<BindingMemberSemanticsRuleSpec> MemberSemantics { get; init; } = [];

    [JsonPropertyName("surfaceManifest")]
    public JsonElement? SurfaceManifest { get; init; }

    [JsonPropertyName("bindingsManifest")]
    public JsonElement? BindingsManifest { get; init; }
}

public sealed class SurfaceReferenceSpec
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    [JsonPropertyName("target")]
    public string Target { get; init; } = "";
}

public sealed class SurfaceDeclarationFileSpec
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = "";

    [JsonPropertyName("imports")]
    public IReadOnlyList<SurfaceImportSpec> Imports { get; init; } = [];

    [JsonPropertyName("global")]
    public SurfaceGlobalBlockSpec? Global { get; init; }

    [JsonPropertyName("modules")]
    public IReadOnlyList<SurfaceModuleSpec> Modules { get; init; } = [];

    [JsonPropertyName("statements")]
    public IReadOnlyList<SurfaceTopLevelStatementSpec> Statements { get; init; } = [];

    [JsonPropertyName("exportEmpty")]
    public bool ExportEmpty { get; init; }
}

public sealed class SurfaceImportSpec
{
    [JsonPropertyName("from")]
    public string From { get; init; } = "";

    [JsonPropertyName("typeOnly")]
    public bool TypeOnly { get; init; } = true;

    [JsonPropertyName("namespace")]
    public string? Namespace { get; init; }

    [JsonPropertyName("named")]
    public IReadOnlyList<SurfaceImportBindingSpec> Named { get; init; } = [];
}

public sealed class SurfaceImportBindingSpec
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("alias")]
    public string? Alias { get; init; }
}

public sealed class SurfaceGlobalBlockSpec
{
    [JsonPropertyName("interfaces")]
    public IReadOnlyList<SurfaceInterfaceSpec> Interfaces { get; init; } = [];

    [JsonPropertyName("typeAliases")]
    public IReadOnlyList<SurfaceTypeAliasSpec> TypeAliases { get; init; } = [];

    [JsonPropertyName("consts")]
    public IReadOnlyList<SurfaceConstSpec> Consts { get; init; } = [];

    [JsonPropertyName("functions")]
    public IReadOnlyList<SurfaceFunctionSpec> Functions { get; init; } = [];

    [JsonPropertyName("classes")]
    public IReadOnlyList<SurfaceClassSpec> Classes { get; init; } = [];
}

public sealed class SurfaceFunctionSpec
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("typeParameters")]
    public IReadOnlyList<string> TypeParameters { get; init; } = [];

    [JsonPropertyName("parameters")]
    public IReadOnlyList<SurfaceParameterSpec> Parameters { get; init; } = [];

    [JsonPropertyName("returnType")]
    public string ReturnType { get; init; } = "";
}

public sealed class SurfaceInterfaceSpec
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("typeParameters")]
    public IReadOnlyList<string> TypeParameters { get; init; } = [];

    [JsonPropertyName("extends")]
    public IReadOnlyList<string> Extends { get; init; } = [];

    [JsonPropertyName("members")]
    public IReadOnlyList<SurfaceMemberSpec> Members { get; init; } = [];
}

public sealed class SurfaceClassSpec
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("typeParameters")]
    public IReadOnlyList<string> TypeParameters { get; init; } = [];

    [JsonPropertyName("extends")]
    public string? Extends { get; init; }

    [JsonPropertyName("members")]
    public IReadOnlyList<SurfaceMemberSpec> Members { get; init; } = [];
}

public sealed class SurfaceTypeAliasSpec
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("typeParameters")]
    public IReadOnlyList<string> TypeParameters { get; init; } = [];

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";
}

public sealed class SurfaceConstSpec
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";
}

public sealed class SurfaceMemberSpec
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("readonly")]
    public bool Readonly { get; init; }

    [JsonPropertyName("optional")]
    public bool Optional { get; init; }

    [JsonPropertyName("parameters")]
    public IReadOnlyList<SurfaceParameterSpec> Parameters { get; init; } = [];

    [JsonPropertyName("typeParameters")]
    public IReadOnlyList<string> TypeParameters { get; init; } = [];

    [JsonPropertyName("returnType")]
    public string? ReturnType { get; init; }

    [JsonPropertyName("indexParameterName")]
    public string? IndexParameterName { get; init; }

    [JsonPropertyName("indexParameterType")]
    public string? IndexParameterType { get; init; }
}

public sealed class SurfaceParameterSpec
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("optional")]
    public bool Optional { get; init; }

    [JsonPropertyName("rest")]
    public bool Rest { get; init; }
}

public sealed class SurfaceModuleSpec
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("statements")]
    public IReadOnlyList<SurfaceModuleStatementSpec> Statements { get; init; } = [];
}

public sealed class SurfaceModuleStatementSpec
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    [JsonPropertyName("from")]
    public string? From { get; init; }

    [JsonPropertyName("names")]
    public IReadOnlyList<string> Names { get; init; } = [];

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

public sealed class SurfaceTopLevelStatementSpec
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

public sealed class SurfaceSimpleBindingSpec
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    [JsonPropertyName("assembly")]
    public string Assembly { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("staticType")]
    public string? StaticType { get; init; }

    [JsonPropertyName("csharpName")]
    public string? CSharpName { get; init; }

    [JsonPropertyName("typeSemantics")]
    public TypeSemanticsSpec? TypeSemantics { get; init; }
}
