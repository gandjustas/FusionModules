using Customers;
using Modulith;

[assembly: HostingStartup(typeof(Module))]

namespace Customers;

// Nothing to configure. The module exists so that the assembly is activated, which is what puts
// it in the registry and therefore into the model.
sealed class Module : ModuleBase;
