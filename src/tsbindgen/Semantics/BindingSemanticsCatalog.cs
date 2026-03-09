namespace tsbindgen.Surface;

public sealed class BindingSemanticsCatalog
{
    public static readonly BindingSemanticsCatalog Empty = new(
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<(string Type, string Member), string>(StringComparerTuple.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal));

    private readonly IReadOnlyDictionary<string, string> typeCallStyles;
    private readonly IReadOnlyDictionary<(string Type, string Member), string> memberCallStyles;
    private readonly IReadOnlyDictionary<string, string> sourceByKey;

    private BindingSemanticsCatalog(
        IReadOnlyDictionary<string, string> typeCallStyles,
        IReadOnlyDictionary<(string Type, string Member), string> memberCallStyles,
        IReadOnlyDictionary<string, string> sourceByKey)
    {
        this.typeCallStyles = typeCallStyles;
        this.memberCallStyles = memberCallStyles;
        this.sourceByKey = sourceByKey;
    }

    public static BindingSemanticsCatalog Create(IEnumerable<BindingSemanticsSpec> specs)
    {
        var typeCallStyles = new Dictionary<string, string>(StringComparer.Ordinal);
        var memberCallStyles = new Dictionary<(string Type, string Member), string>(StringComparerTuple.Ordinal);
        var sourceByKey = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var spec in specs)
        {
            foreach (var rule in spec.MemberSemantics)
            {
                var callStyle = rule.EmitSemantics.CallStyle;
                if (string.IsNullOrWhiteSpace(rule.Binding.Member))
                {
                    Register(
                        typeCallStyles,
                        sourceByKey,
                        $"type:{rule.Binding.Type}",
                        rule.Binding.Type,
                        callStyle);
                    continue;
                }

                var memberKey = (rule.Binding.Type, rule.Binding.Member!);
                Register(
                    memberCallStyles,
                    sourceByKey,
                    $"member:{rule.Binding.Type}::{rule.Binding.Member}",
                    memberKey,
                    callStyle);
            }
        }

        return new BindingSemanticsCatalog(typeCallStyles, memberCallStyles, sourceByKey);
    }

    public string? ResolveMethodCallStyle(string clrType, string clrMember)
    {
        if (memberCallStyles.TryGetValue((clrType, clrMember), out var exact))
        {
            return exact;
        }

        return typeCallStyles.TryGetValue(clrType, out var wholeType)
            ? wholeType
            : null;
    }

    private static void Register<TKey>(
        Dictionary<TKey, string> map,
        Dictionary<string, string> sourceByKey,
        string sourceKey,
        TKey key,
        string value)
        where TKey : notnull
    {
        if (map.TryGetValue(key, out var existing))
        {
            if (existing != value)
            {
                var originalSource = sourceByKey.TryGetValue(sourceKey, out var source)
                    ? source
                    : "unknown";
                throw new InvalidOperationException(
                    $"Conflicting bindings semantics for '{sourceKey}': '{originalSource}' says '{existing}', new value is '{value}'.");
            }
            return;
        }

        map[key] = value;
        sourceByKey[sourceKey] = value;
    }

    private sealed class StringComparerTuple : IEqualityComparer<(string Type, string Member)>
    {
        public static readonly StringComparerTuple Ordinal = new();

        public bool Equals((string Type, string Member) x, (string Type, string Member) y)
        {
            return StringComparer.Ordinal.Equals(x.Type, y.Type) &&
                StringComparer.Ordinal.Equals(x.Member, y.Member);
        }

        public int GetHashCode((string Type, string Member) obj)
        {
            return HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.Type),
                StringComparer.Ordinal.GetHashCode(obj.Member));
        }
    }
}
