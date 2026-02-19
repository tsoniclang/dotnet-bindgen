namespace MyCompany.SplitNsExtensions;

public static class TxExtensions
{
    public static string GetExtraName(this global::System.Transactions.Transaction transaction, global::System.Transactions.ExtraType extra)
    {
        return extra.Name;
    }
}

