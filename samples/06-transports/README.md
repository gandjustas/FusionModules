# 06 — Transports

One edge of the call graph — checkout asking pricing for a quote — with the network hop present
and absent, and the same test suite proving both.

```bash
cd Host
dotnet run --launch-profile Merged                   # checkout + pricing, a method call
dotnet run --launch-profile PricingService           # pricing, published over HTTP  (:5601)
dotnet run --launch-profile CheckoutCallingPricing   # checkout + the HTTP client     (:5600)
```

## The pattern, in four projects

**[`Contracts/IPricing.cs`](Contracts/IPricing.cs)** — the contract leaves both services. A plain
library with no `[HostingStartup]`, so it is not a module, MOD0001 does not apply, and both sides
reference it without either depending on the other's deployment.

**[`Modules/Pricing`](Modules/Pricing/Module.cs)** — the direct implementation. It maps no
endpoints and needs none: everything it offers, it offers through DI.

**[`Modules/PricingApi`](Modules/PricingApi/Module.cs)** — the contract published over HTTP, and
nothing else. Separate from the implementation on purpose, because whether pricing *runs* here and
whether anyone outside may *call* it are two different topology questions.

**[`Modules/PricingClient`](Modules/PricingClient/Module.cs)** — the old transport, kept behind the
same interface. This is the step that is easy to skip, and it is the one that makes the edge a
deployment choice rather than a code change — worth having on hand the first time a merge turns out
to have been wrong for one particular edge.

**[`Modules/Checkout`](Modules/Checkout/Module.cs)** — the caller, referencing the contract and
nothing else. Its code is byte-identical in both topologies; which implementation it gets is
`HOSTINGSTARTUPASSEMBLIES`.

Note what the caller does *not* contain. No retry and no circuit breaker: in the merged topology
they would wrap a method call, where a retry re-runs a local method and a breaker does nothing.
Resilience belongs with the transport, which is to say in `PricingClientModule`, where the I/O is.

## The suite is the gate

[`Tests/PricingContractTests.cs`](Tests/PricingContractTests.cs) is abstract and knows only about
`IPricing`. [`Tests/Topologies.cs`](Tests/Topologies.cs) runs it twice — once against a single host
holding both modules, once against two hosts of the same host project talking over HTTP.

One interface, one set of expectations, run against every implementation you kept. A difference
between the two runs is a behaviour change the merge introduced, which is precisely the thing
nobody notices otherwise.

Two assertions sit outside the shared suite and are worth reading together: the pricing host
publishes `/pricing/quote`, and the merged host returns 404 for it. That the merged host serves
checkout proves little; that it does not serve pricing's public endpoint is what says the topology
is real rather than a label.

## What this sample cannot show you

Collapsing a **queue** is not the same decision as collapsing an HTTP call, and the difference does
not fit in a sample. Replacing a durable publish with an in-process call trades at-least-once for
at-most-once, retries and dead-lettering for the caller's exception, and the consumer's independent
fate for a shared one. Some of that is the point; some of it is a regression that surfaces only
when something fails in production. Every messaging edge gets an explicit decision with the delta
written down — the skill's transports reference has the table.
