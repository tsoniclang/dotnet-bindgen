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
// - Application hides Router.use(...) with a covariant return (Application),
//   which *should* be considered compatible with the base overload (Router) and must not cause
//   BaseOverloadAdder to inject a redundant Router-returning overload.
//
// IMPORTANT: this fixture intentionally avoids System.Object parameters/returns so that
// Library-mode regression tests do not treat legitimate `unknown` mappings as resolution failures.

public delegate void Handler();

public abstract class RoutingHost<TSelf> where TSelf : RoutingHost<TSelf>
{
    protected TSelf self => (TSelf)this;

    // Strongly-typed overload (like Express): should remain usable from derived types.
    public virtual TSelf get(string path, Handler callback) => self;

    public virtual TSelf use(Handler callback) => self;
}

public class Router : RoutingHost<Router>
{
    // Deliberately does NOT override get(path, callback) so the base signature remains generic (TSelf).
    public override Router use(Handler callback) => this;
}

public class Application : Router
{
    // Settings getter overload: different signature from the routing overloads.
    public string? get(string name) => null;

    // Covariant return hiding: should satisfy the base Router overload (no extra Router overload needed).
    public new Application use(Handler callback) => this;
}
