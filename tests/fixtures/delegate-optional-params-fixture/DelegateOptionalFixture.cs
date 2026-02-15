namespace DelegateOptionalFixture;

public delegate void OptionalControl(string control = "route");

public delegate int WeirdDelegate(int @break = 1, params string[] rest);

