#pragma warning disable IDE0130 // Namespace does not match folder structure: it stands in for EF Core.

/// <summary>
/// Stands in for EF Core's non-generic options type, which every AddDbContext registers. Matched by
/// full name, so the test needs no EF Core reference.
/// </summary>
namespace Microsoft.EntityFrameworkCore
{
    internal sealed class DbContextOptions(string context)
    {
        public string Context { get; } = context;
    }
}
