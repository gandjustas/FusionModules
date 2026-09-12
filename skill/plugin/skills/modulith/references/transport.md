# Phase 4 — Transports

For each edge in the call graph from Phase 0: does it still need to be a network call?

## The pattern

1. **The contract leaves both services.** A plain `Microsoft.NET.Sdk` library, public interface,
   no `[HostingStartup]` — so it is not a module, MOD0001 does not apply, and both sides may
   reference it without either depending on the other's deployment.

   ```csharp
   // Contracts
   public interface IPricing
   {
       Task<Money> QuoteAsync(OrderId order, CancellationToken cancellationToken);
   }
   ```

2. **A `Local` module registers the direct implementation.**

   ```csharp
   protected override void ConfigureServices(WebHostBuilderContext ctx, IServiceCollection services) =>
       services.AddScoped<IPricing, Pricing>();
   ```

3. **Optionally, a `Remote` module keeps the old transport** behind the same interface — the
   `HttpClient`, gRPC or queue client implementation, moved out of the caller.

4. **The caller depends only on the interface.** Which implementation it gets is a topology
   decision, made by `HOSTINGSTARTUPASSEMBLIES`, and the caller's code does not change between
   them.

That last point is what makes this reversible. A topology can be flipped back to remote without
touching a line of business logic, which is worth having the first time the merge turns out to
have been a mistake for one particular edge.

## Keep the remote transport when

- another team or another runtime consumes it — the contract is public whatever you do internally
- it needs to scale independently, and Phase 0's replica counts back that up
- the queue's durability, retry, dead-lettering or backpressure is doing real work
- it is the boundary of a bulkhead you actually want

## Drop it when

- the only callers are services in this solution, and
- there is no scaling or isolation argument, and
- nothing downstream depends on the asynchrony

## Messaging is not a method call

This is the part to slow down on. Replacing a durable publish with an in-process call changes,
all at once:

| | before | after |
|---|---|---|
| delivery | at-least-once | at-most-once |
| failure | retried, eventually dead-lettered | the caller's exception |
| backpressure | the queue absorbs it | the caller waits |
| ordering | per partition or queue | call order |
| isolation | consumer down ≠ producer down | same process, same fate |
| transaction | separate; often an outbox | possibly the same one |

Some of those changes are the point — an outbox that existed only to make a local write and a
remote publish atomic may become unnecessary once both are local. Others are regressions that
will not show up until something fails in production.

**Every messaging edge gets an explicit decision from the user, with the delta written into
`modulith-migration.md`.** Never collapse a queue because it is technically possible.

## Failure handling after the merge

An in-process call cannot time out the way a network call could. Go through what the caller did
about failure:

- **Retries** around a local call re-run a local method. Usually pointless; occasionally harmful.
  Remove them, or move them to where the real I/O now is.
- **Timeouts** still mean something if the callee does I/O. Keep those; drop the ones that were
  guarding the network hop itself.
- **Circuit breakers** around a local call do nothing useful. Remove them and say so.
- **Fallbacks** that returned degraded results when a service was unreachable now trigger only on
  genuine faults. Check that the fallback still makes sense.
- **Blast radius grows.** A module that used to take itself down now takes the process down.
  Note it in the risk register; a topology can be the answer if a module really is that risky.

## Gate

- Both implementations of a retained contract pass the same test suite. One interface, one set of
  expectations — a property the pattern gives you for free, so use it.
- Route inventory unchanged.
- The `modulith-migration.md` entry for each edge says what was decided and why.
