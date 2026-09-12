using Microsoft.AspNetCore.Hosting;
using Modulith;
using Orders;

[assembly: HostingStartup(typeof(Module))]

namespace Orders;

sealed class Module : ModuleBase;
