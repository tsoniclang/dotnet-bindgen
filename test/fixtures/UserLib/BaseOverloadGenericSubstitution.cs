namespace MyCompany.Utils;

// Regression fixture for BaseOverloadAdder + generic substitution + covariant returns.
//
// We want TypeScript to accept:
// - Application extends Router
// - Application adds a *new overload* of "get" (settings getter)
// - Base router overloads of "get" must still be present on Application to satisfy TS2430
// - But any self-referential generic returns (TSelf) must be closed (TSelf -> Router), not emitted as `unknown`.
//
// Additionally:
// - Application hides Router.use(object) with a covariant return (Application),
//   which *should* be considered compatible with the base overload (Router) and must not cause
//   BaseOverloadAdder to inject a redundant Router-returning overload.

public delegate void Handler();

public abstract class RoutingHost<TSelf> where TSelf : RoutingHost<TSelf>
{
    protected TSelf self => (TSelf)this;

    // Strongly-typed overload (like Express): should remain usable from derived types.
    public TSelf get(string path, Handler callback) => get((object)path, callback);

    // Object-based core API (self-referential generic return).
    public virtual TSelf get(object path, object callback) => self;

    public virtual TSelf use(object callback) => self;
}

public class Router : RoutingHost<Router>
{
    public override Router get(object path, object callback) => this;
    public override Router use(object callback) => this;
}

public class Application : Router
{
    // Settings getter overload: different signature from the routing overloads.
    public object? get(string name) => null;

    // Covariant return hiding: should satisfy the base Router overload (no extra Router overload needed).
    public new Application use(object callback) => this;
}

