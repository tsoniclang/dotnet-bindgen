using System.Text.Json;

namespace DotnetBindgen.Surface;

public static class SurfacePackageLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = false
    };

    public static IReadOnlyList<SurfacePackageSpec> LoadMany(IEnumerable<string> paths)
    {
        return paths.Select(Load).ToArray();
    }

    public static SurfacePackageSpec Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Surface package config not found: {path}");
        }

        var json = File.ReadAllText(path);
        var parsed = JsonSerializer.Deserialize<SurfacePackageSpec>(json, JsonOptions);
        if (parsed is null)
        {
            throw new InvalidOperationException($"Failed to parse surface package config: {path}");
        }

        if (parsed.SchemaVersion != 1)
        {
            throw new InvalidOperationException(
                $"Unsupported surface package schemaVersion in {path}: {parsed.SchemaVersion}");
        }

        Validate(parsed, path);
        return parsed;
    }

    private static void Validate(SurfacePackageSpec spec, string path)
    {
        foreach (var reference in spec.PrependReferences)
        {
            if (reference.Kind is not ("path" or "types"))
            {
                throw new InvalidOperationException(
                    $"{path}: prependReferences.kind must be 'path' or 'types'.");
            }
            if (string.IsNullOrWhiteSpace(reference.Target))
            {
                throw new InvalidOperationException($"{path}: prependReferences.target must be non-empty.");
            }
        }

        foreach (var file in spec.DeclarationFiles)
        {
            if (string.IsNullOrWhiteSpace(file.Path))
            {
                throw new InvalidOperationException($"{path}: declarationFiles[].path must be non-empty.");
            }

            foreach (var import in file.Imports)
            {
                if (string.IsNullOrWhiteSpace(import.From))
                {
                    throw new InvalidOperationException($"{path}: declarationFiles[{file.Path}].imports[].from must be non-empty.");
                }

                var hasNamespace = !string.IsNullOrWhiteSpace(import.Namespace);
                var hasNamed = import.Named.Count > 0;
                if (hasNamespace == hasNamed)
                {
                    throw new InvalidOperationException(
                        $"{path}: declarationFiles[{file.Path}] imports must specify exactly one of namespace or named.");
                }
            }

            ValidateGlobalBlock(file.Global, path, file.Path);

            foreach (var module in file.Modules)
            {
                if (string.IsNullOrWhiteSpace(module.Name))
                {
                    throw new InvalidOperationException($"{path}: declarationFiles[{file.Path}].modules[].name must be non-empty.");
                }

                foreach (var statement in module.Statements)
                {
                    if (statement.Kind is not ("reexport" or "reexportType" or "const"))
                    {
                        throw new InvalidOperationException(
                            $"{path}: declarationFiles[{file.Path}] module '{module.Name}' has unsupported statement kind '{statement.Kind}'.");
                    }
                }
            }
        }

        foreach (var (name, binding) in spec.SimpleBindings)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException($"{path}: simpleBindings keys must be non-empty.");
            }
            if (binding.Kind is not ("global" or "module"))
            {
                throw new InvalidOperationException($"{path}: simpleBindings['{name}'].kind must be 'global' or 'module'.");
            }
            if (string.IsNullOrWhiteSpace(binding.OwnerIdentity) || string.IsNullOrWhiteSpace(binding.Type))
            {
                throw new InvalidOperationException($"{path}: simpleBindings['{name}'] must define ownerIdentity and type.");
            }
        }

        for (var i = 0; i < spec.MemberSemantics.Count; i++)
        {
            BindingSemanticsLoader.ValidateMemberSemanticsRule(
                spec.MemberSemantics[i],
                path,
                $"memberSemantics[{i}]");
        }
    }

    private static void ValidateGlobalBlock(
        SurfaceGlobalBlockSpec? block,
        string configPath,
        string outputPath)
    {
        if (block is null) return;

        foreach (var iface in block.Interfaces)
        {
            if (string.IsNullOrWhiteSpace(iface.Name))
            {
                throw new InvalidOperationException($"{configPath}: declarationFiles[{outputPath}] global interface name must be non-empty.");
            }
            ValidateMembers(iface.Members, configPath, outputPath, iface.Name);
        }

        foreach (var cls in block.Classes)
        {
            if (string.IsNullOrWhiteSpace(cls.Name))
            {
                throw new InvalidOperationException($"{configPath}: declarationFiles[{outputPath}] global class name must be non-empty.");
            }
            ValidateMembers(cls.Members, configPath, outputPath, cls.Name);
        }

        foreach (var alias in block.TypeAliases)
        {
            if (string.IsNullOrWhiteSpace(alias.Name) || string.IsNullOrWhiteSpace(alias.Type))
            {
                throw new InvalidOperationException($"{configPath}: declarationFiles[{outputPath}] global type aliases must define name and type.");
            }
        }

        foreach (var cnst in block.Consts)
        {
            if (string.IsNullOrWhiteSpace(cnst.Name) || string.IsNullOrWhiteSpace(cnst.Type))
            {
                throw new InvalidOperationException($"{configPath}: declarationFiles[{outputPath}] global consts must define name and type.");
            }
        }

        foreach (var function in block.Functions)
        {
            if (string.IsNullOrWhiteSpace(function.Name) || string.IsNullOrWhiteSpace(function.ReturnType))
            {
                throw new InvalidOperationException($"{configPath}: declarationFiles[{outputPath}] global functions must define name and returnType.");
            }
            foreach (var param in function.Parameters)
            {
                if (string.IsNullOrWhiteSpace(param.Name) || string.IsNullOrWhiteSpace(param.Type))
                {
                    throw new InvalidOperationException(
                        $"{configPath}: declarationFiles[{outputPath}] global function '{function.Name}' parameters must define name and type.");
                }
            }
        }
    }

    private static void ValidateMembers(
        IReadOnlyList<SurfaceMemberSpec> members,
        string configPath,
        string outputPath,
        string ownerName)
    {
        foreach (var member in members)
        {
            if (member.Kind is not ("property" or "method" or "index" or "constructor" or "constructSignature" or "callSignature"))
            {
                throw new InvalidOperationException(
                    $"{configPath}: declarationFiles[{outputPath}] '{ownerName}' has unsupported member kind '{member.Kind}'.");
            }

            if (member.Kind == "property")
            {
                if (string.IsNullOrWhiteSpace(member.Name) || string.IsNullOrWhiteSpace(member.Type))
                {
                    throw new InvalidOperationException(
                        $"{configPath}: declarationFiles[{outputPath}] '{ownerName}' property members must define name and type.");
                }
            }

            if (member.Kind is "method" or "constructor" or "constructSignature" or "callSignature")
            {
                if (member.Kind == "method" && (string.IsNullOrWhiteSpace(member.Name) || string.IsNullOrWhiteSpace(member.ReturnType)))
                {
                    throw new InvalidOperationException(
                        $"{configPath}: declarationFiles[{outputPath}] '{ownerName}' method members must define name and returnType.");
                }
                if ((member.Kind == "constructSignature" || member.Kind == "callSignature") && string.IsNullOrWhiteSpace(member.ReturnType))
                {
                    throw new InvalidOperationException(
                        $"{configPath}: declarationFiles[{outputPath}] '{ownerName}' {member.Kind} members must define returnType.");
                }
                foreach (var param in member.Parameters)
                {
                    if (string.IsNullOrWhiteSpace(param.Name) || string.IsNullOrWhiteSpace(param.Type))
                    {
                        throw new InvalidOperationException(
                            $"{configPath}: declarationFiles[{outputPath}] '{ownerName}' method '{member.Name}' parameters must define name and type.");
                    }
                }
            }

            if (member.Kind == "index")
            {
                if (string.IsNullOrWhiteSpace(member.IndexParameterName) ||
                    string.IsNullOrWhiteSpace(member.IndexParameterType) ||
                    string.IsNullOrWhiteSpace(member.Type))
                {
                    throw new InvalidOperationException(
                        $"{configPath}: declarationFiles[{outputPath}] '{ownerName}' index members must define indexParameterName, indexParameterType, and type.");
                }
            }
        }
    }
}
