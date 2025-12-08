namespace MyCompany.Utils
{
    /// <summary>
    /// Test class for verifying ref/in/out parameter metadata tracking.
    ///
    /// COMPILER-GRADE CONTRACT:
    /// Callable identity = stableId + modifier vector
    /// - stableId contains CLR type names with '&amp;' for byref
    /// - modifier vector (parameterModifiers) distinguishes ref/out/in semantics
    /// - Both are REQUIRED for correct overload resolution and call emission
    ///
    /// Note: C# doesn't allow overloading that differs only in ref vs in (CS0663),
    /// but we can test that each modifier type is correctly tracked in metadata.
    /// </summary>
    public static class OverloadRefInOut
    {
        // Value vs ref overload (legal in C#)
        public static int F(int x) => 1;           // No modifier
        public static int F(ref int x) => 2;       // ref modifier

        // Separate method with in parameter (can't overload with ref)
        public static int G(in int x) => x;        // in modifier (readonly ref)

        // TryGet pattern with out parameter
        public static bool TryGet(string key, out int value)
        {
            value = 42;
            return true;
        }

        // Test: ref vs different type (legal, different signature)
        public static void Update(ref int x) { x++; }
        public static bool Update(string key, out int value) { value = 0; return false; }

        // COMPILER-GRADE TEST CASES: H/K/L prove encoding is correct per modifier kind on int&
        // All three have the SAME CLR byref element type (System.Int32&) but different modifiers.
        // Tsonic must use parameterModifiers to distinguish them.
        public static void H(ref int x) { x = x + 1; }    // ref: mutable byref
        public static void K(out int x) { x = 0; }        // out: definite assignment
        public static void L(in int x) { _ = x; }         // in: readonly byref
    }
}
