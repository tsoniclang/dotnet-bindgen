namespace FriendlyImportsLib;

public sealed class Box<T>
{
}

// Intentionally ends with "_1" but is NOT generic.
// Used to ensure dotnet-bindgen does not "prettify" based on name alone.
public sealed class Database_1
{
}

// Generic type whose CLR base name includes "_1".
// Used to ensure friendly aliasing is derived from CLR metadata (backtick arity),
// and that aliasing is collision-safe when a non-generic sibling exists.
public sealed class Database_1<T>
{
}
