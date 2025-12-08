namespace MyCompany.Utils
{
    /// <summary>
    /// Test class for verifying stableId uniqueness across overload variations.
    /// Used by test-stableid-uniqueness.sh to ensure byref parameters produce distinct signatures.
    /// </summary>
    public class OverloadTest
    {
        // Byref vs non-byref - MUST produce different stableIds
        public void Process(int x) { }
        public void Process(ref int x) { }

        // Different param types - MUST produce different stableIds
        public void Method(int x) { }
        public void Method(string x) { }
        public void Method(int x, int y) { }

        // out parameter
        public bool TryGet(string key, out int value) { value = 0; return false; }

        // in parameter (readonly ref)
        public void ReadOnly(in int x) { }

        // Generic overloads
        public void Generic<T>(T item) { }
        public void Generic<T, U>(T item, U other) { }
    }
}
