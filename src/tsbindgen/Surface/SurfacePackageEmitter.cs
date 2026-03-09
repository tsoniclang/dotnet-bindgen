using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tsbindgen.Surface;

public static class SurfacePackageEmitter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void EmitAll(string outputDirectory, IReadOnlyList<SurfacePackageSpec> specs)
    {
        foreach (var spec in specs)
        {
            Emit(outputDirectory, spec);
        }
    }

    public static void Emit(string outputDirectory, SurfacePackageSpec spec)
    {
        Directory.CreateDirectory(outputDirectory);

        foreach (var file in spec.DeclarationFiles)
        {
            var filePath = Path.Combine(outputDirectory, file.Path);
            var fileDir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(fileDir))
            {
                Directory.CreateDirectory(fileDir);
            }

            File.WriteAllText(filePath, RenderDeclarationFile(file));
        }

        if (spec.SimpleBindings.Count > 0)
        {
            var bindingsPath = Path.Combine(outputDirectory, "bindings.json");
            var json = JsonSerializer.Serialize(new
            {
                bindings = spec.SimpleBindings.ToDictionary(
                    entry => entry.Key,
                    entry => new
                    {
                        kind = entry.Value.Kind,
                        assembly = entry.Value.Assembly,
                        type = entry.Value.Type,
                        staticType = entry.Value.StaticType,
                        csharpName = entry.Value.CSharpName,
                        typeSemantics = entry.Value.TypeSemantics
                    })
            }, JsonOptions);
            File.WriteAllText(bindingsPath, json + Environment.NewLine);
        }

        if (spec.SurfaceManifest is JsonElement manifest)
        {
            var manifestPath = Path.Combine(outputDirectory, "tsonic.surface.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine);
        }

        if (spec.BindingsManifest is JsonElement bindingsManifest)
        {
            var manifestPath = Path.Combine(outputDirectory, "tsonic.bindings.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(bindingsManifest, JsonOptions) + Environment.NewLine);
        }

        if (spec.PrependReferences.Count > 0)
        {
            var indexPath = Path.Combine(outputDirectory, "index.d.ts");
            var existing = File.Exists(indexPath) ? File.ReadAllText(indexPath) : "export {};\n";
            existing = StripLeadingReferences(existing);
            var refs = string.Join(
                Environment.NewLine,
                spec.PrependReferences.Select(RenderReference));

            var trimmedExisting = existing.TrimStart();
            File.WriteAllText(indexPath, refs + Environment.NewLine + Environment.NewLine + trimmedExisting);
        }
    }

    private static string StripLeadingReferences(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        while (lines.Count > 0)
        {
            var first = lines[0].Trim();
            if (first.StartsWith("/// <reference ", StringComparison.Ordinal))
            {
                lines.RemoveAt(0);
                continue;
            }
            if (first.Length == 0)
            {
                lines.RemoveAt(0);
                continue;
            }
            break;
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderReference(SurfaceReferenceSpec spec)
    {
        return spec.Kind switch
        {
            "path" => $"/// <reference path=\"{spec.Target}\" />",
            "types" => $"/// <reference types=\"{spec.Target}\" />",
            _ => throw new InvalidOperationException($"Unsupported reference kind: {spec.Kind}")
        };
    }

    private static string RenderDeclarationFile(SurfaceDeclarationFileSpec file)
    {
        var sb = new StringBuilder();
        var renderedGlobalBlock = file.Global is not null && HasGlobalContent(file.Global);

        foreach (var import in file.Imports)
        {
            sb.AppendLine(RenderImport(import));
        }

        if (file.Imports.Count > 0 &&
            (renderedGlobalBlock || file.Modules.Count > 0 || file.Statements.Count > 0 || file.ExportEmpty))
        {
            sb.AppendLine();
        }

        if (renderedGlobalBlock && file.Global is not null)
        {
            sb.AppendLine("declare global {");
            RenderGlobalBlock(sb, file.Global);
            sb.AppendLine("}");
        }

        if (file.Modules.Count > 0)
        {
            if (renderedGlobalBlock)
            {
                sb.AppendLine();
            }

            for (var i = 0; i < file.Modules.Count; i++)
            {
                if (i > 0) sb.AppendLine();
                RenderModule(sb, file.Modules[i]);
            }
        }

        if (file.Statements.Count > 0)
        {
            if (renderedGlobalBlock || file.Modules.Count > 0)
            {
                sb.AppendLine();
            }

            foreach (var statement in file.Statements)
            {
                sb.AppendLine(RenderTopLevelStatement(statement));
            }
        }

        if (file.ExportEmpty)
        {
            if (sb.Length > 0 && !sb.ToString().EndsWith(Environment.NewLine + Environment.NewLine))
            {
                sb.AppendLine();
            }
            sb.AppendLine("export {};");
        }

        return sb.ToString();
    }

    private static string RenderImport(SurfaceImportSpec import)
    {
        var prefix = import.TypeOnly ? "import type " : "import ";
        if (!string.IsNullOrWhiteSpace(import.Namespace))
        {
            return $"{prefix}* as {import.Namespace} from \"{import.From}\";";
        }

        var names = string.Join(
            ", ",
            import.Named.Select(spec =>
                string.IsNullOrWhiteSpace(spec.Alias) ? spec.Name : $"{spec.Name} as {spec.Alias}"));

        return $"{prefix}{{ {names} }} from \"{import.From}\";";
    }

    private static void RenderGlobalBlock(StringBuilder sb, SurfaceGlobalBlockSpec block)
    {
        var wroteAny = false;

        foreach (var cls in block.Classes)
        {
            if (wroteAny) sb.AppendLine();
            RenderClass(sb, cls, 1);
            wroteAny = true;
        }

        foreach (var iface in block.Interfaces)
        {
            if (wroteAny) sb.AppendLine();
            RenderInterface(sb, iface, 1);
            wroteAny = true;
        }

        foreach (var alias in block.TypeAliases)
        {
            if (wroteAny) sb.AppendLine();
            sb.AppendLine($"{Indent(1)}type {alias.Name}{RenderTypeParameters(alias.TypeParameters)} = {alias.Type};");
            wroteAny = true;
        }

        foreach (var cnst in block.Consts)
        {
            if (wroteAny) sb.AppendLine();
            sb.AppendLine($"{Indent(1)}const {cnst.Name}: {cnst.Type};");
            wroteAny = true;
        }

        foreach (var function in block.Functions)
        {
            if (wroteAny) sb.AppendLine();
            sb.AppendLine($"{Indent(1)}function {function.Name}{RenderTypeParameters(function.TypeParameters)}({RenderParameters(function.Parameters)}): {function.ReturnType};");
            wroteAny = true;
        }
    }

    private static bool HasGlobalContent(SurfaceGlobalBlockSpec block)
    {
        return block.Classes.Count > 0 ||
               block.Interfaces.Count > 0 ||
               block.TypeAliases.Count > 0 ||
               block.Consts.Count > 0 ||
               block.Functions.Count > 0;
    }

    private static void RenderClass(StringBuilder sb, SurfaceClassSpec spec, int indent)
    {
        var extendsClause = string.IsNullOrWhiteSpace(spec.Extends) ? "" : $" extends {spec.Extends}";
        sb.AppendLine($"{Indent(indent)}class {spec.Name}{RenderTypeParameters(spec.TypeParameters)}{extendsClause} {{");
        foreach (var member in spec.Members)
        {
            sb.AppendLine($"{Indent(indent + 1)}{RenderMember(member)}");
        }
        sb.Append($"{Indent(indent)}}}");
        sb.AppendLine();
    }

    private static void RenderInterface(StringBuilder sb, SurfaceInterfaceSpec spec, int indent)
    {
        var extendsClause = spec.Extends.Count > 0 ? $" extends {string.Join(", ", spec.Extends)}" : "";
        sb.AppendLine($"{Indent(indent)}interface {spec.Name}{RenderTypeParameters(spec.TypeParameters)}{extendsClause} {{");
        foreach (var member in spec.Members)
        {
            sb.AppendLine($"{Indent(indent + 1)}{RenderMember(member)}");
        }
        sb.Append($"{Indent(indent)}}}");
        sb.AppendLine();
    }

    private static void RenderModule(StringBuilder sb, SurfaceModuleSpec spec)
    {
        sb.AppendLine($"declare module \"{spec.Name}\" {{");
        foreach (var statement in spec.Statements)
        {
            sb.AppendLine($"{Indent(1)}{RenderModuleStatement(statement)}");
        }
        sb.AppendLine("}");
    }

    private static string RenderTopLevelStatement(SurfaceTopLevelStatementSpec spec)
    {
        return spec.Kind switch
        {
            "const" => $"export const {spec.Name}: {spec.Type};",
            _ => throw new InvalidOperationException($"Unsupported top-level statement kind: {spec.Kind}")
        };
    }

    private static string RenderModuleStatement(SurfaceModuleStatementSpec spec)
    {
        return spec.Kind switch
        {
            "reexport" => $"export {{ {string.Join(", ", spec.Names)} }} from \"{spec.From}\";",
            "reexportType" => $"export type {{ {string.Join(", ", spec.Names)} }} from \"{spec.From}\";",
            "const" => $"export const {spec.Name}: {spec.Type};",
            _ => throw new InvalidOperationException($"Unsupported module statement kind: {spec.Kind}")
        };
    }

    private static string RenderMember(SurfaceMemberSpec spec)
    {
        return spec.Kind switch
        {
            "property" => $"{(spec.Readonly ? "readonly " : "")}{spec.Name}{(spec.Optional ? "?" : "")}: {spec.Type};",
            "method" => $"{spec.Name}{RenderTypeParameters(spec.TypeParameters)}({RenderParameters(spec.Parameters)}): {spec.ReturnType};",
            "constructor" => $"constructor({RenderParameters(spec.Parameters)});",
            "constructSignature" => $"new{RenderTypeParameters(spec.TypeParameters)}({RenderParameters(spec.Parameters)}): {spec.ReturnType};",
            "callSignature" => $"{RenderTypeParameters(spec.TypeParameters)}({RenderParameters(spec.Parameters)}): {spec.ReturnType};",
            "index" => $"{(spec.Readonly ? "readonly " : "")}[{spec.IndexParameterName}: {spec.IndexParameterType}]: {spec.Type};",
            _ => throw new InvalidOperationException($"Unsupported member kind: {spec.Kind}")
        };
    }

    private static string RenderTypeParameters(IReadOnlyList<string> typeParameters)
    {
        return typeParameters.Count == 0 ? "" : $"<{string.Join(", ", typeParameters)}>";
    }

    private static string RenderParameters(IReadOnlyList<SurfaceParameterSpec> parameters)
    {
        return string.Join(", ", parameters.Select(param =>
        {
            var rest = param.Rest ? "..." : "";
            var optional = param.Optional ? "?" : "";
            return $"{rest}{param.Name}{optional}: {param.Type}";
        }));
    }

    private static string Indent(int level) => new(' ', level * 2);
}
