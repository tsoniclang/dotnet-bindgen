namespace ExtensionScopesFixture;

public interface ISeq;

public interface ISeq<T> : ISeq;

public sealed class Seq<T> : ISeq<T>;

public static class SeqExtensions
{
    // Arity-0 vs arity-1 receiver pair:
    // This exists to ensure tsbindgen prefers generic receiver buckets over their arity-0 bases
    // for overlapping member names (C# "more specific receiver wins").
    public static ISeq BaseOnly(this ISeq source) => source;

    public static ISeq AsParallel(this ISeq source) => source;

    public static ISeq<T> AsParallel<T>(this ISeq<T> source) => source;

    public static ISeq<T> Where<T>(this ISeq<T> source, Func<T, bool> predicate) => source;

    public static ISeq<TResult> Select<TSource, TResult>(this ISeq<TSource> source, Func<TSource, TResult> selector) =>
        new Seq<TResult>();
}

public static class BclLinqExtensions
{
    // BCL receiver specificity: IQueryable<T> is a strict subtype of IEnumerable<T>.
    // Airplane-grade: method-table overload ordering must emit the IQueryable receiver
    // overload BEFORE the IEnumerable receiver overload so fluent chains preserve IQueryable<T>.
    public static IEnumerable<T> Stamp<T>(this IEnumerable<T> source) => source;

    public static IQueryable<T> Stamp<T>(this IQueryable<T> source) => source;
}
