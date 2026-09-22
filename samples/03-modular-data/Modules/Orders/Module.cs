using Microsoft.AspNetCore.Hosting;
using FusionModules;
using Orders;

[assembly: HostingStartup(typeof(Module))]

namespace Orders;

sealed class Module : ModuleBase;
