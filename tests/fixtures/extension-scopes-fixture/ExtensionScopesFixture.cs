namespace ExtensionScopesFixture;

public interface ISeq<T>;

public sealed class Seq<T> : ISeq<T>;

public static class SeqExtensions
{
    public static ISeq<T> Where<T>(this ISeq<T> source, Func<T, bool> predicate) => source;

    public static ISeq<TResult> Select<TSource, TResult>(this ISeq<TSource> source, Func<TSource, TResult> selector) =>
        new Seq<TResult>();
}

