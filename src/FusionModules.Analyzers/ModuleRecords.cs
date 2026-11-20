using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FusionModules.Analyzers;

/// <summary>
/// What a module records about itself (<see cref="ModuleRecordsGenerator"/>): how a registration is
/// written into the module's assembly attributes, and read back from them by the host's build.
/// </summary>
/// <remarks>
/// Matched by name and namespace rather than by type: each module declares the attribute in a
/// namespace of its own.
/// </remarks>
internal static class ModuleRecords
{
    /// <summary>
    /// One service a module's code registers. The display names are for the message; the
    /// identities, which carry assembly names, are what is compared: two modules may each declare an
    /// internal <c>Shop.IChecker</c>, and those are two services. Not readonly: that needs
    /// <c>IsExternalInit</c>, which netstandard2.0 lacks.
    /// </summary>
    public record struct Registration(string Service, string ServiceIdentity, string Implementation, string ImplementationIdentity, string Spellings)
    {
        /// <summary>The attribute's constructor parameters, in the order <see cref="Arguments"/> writes them and <see cref="Read"/> reads them.</summary>
        public const string Parameters = "string service, string serviceIdentity, string implementation, string implementationIdentity, string spellings";

        /// <summary>The attribute's constructor arguments, as C# literals.</summary>
        public readonly string Arguments() =>
            string.Join(", ", new[] { Service, ServiceIdentity, Implementation, ImplementationIdentity, Spellings }.Select(value => SymbolDisplay.FormatLiteral(value, quote: true)));

        /// <summary>The registration an attribute records, or null when it is not one of ours.</summary>
        public static Registration? Read(AttributeData attribute) =>
            attribute.AttributeClass is { } type &&
            type.Name == ModuleRecordsGenerator.RegistrationAttributeName &&
            type.ContainingNamespace?.ContainingNamespace?.ToDisplayString() == ModuleRecordsGenerator.AttributeNamespace &&
            attribute.ConstructorArguments is { Length: 5 } arguments &&
            arguments.All(argument => argument.Value is string)
                ? new Registration((string)arguments[0].Value!, (string)arguments[1].Value!, (string)arguments[2].Value!, (string)arguments[3].Value!, (string)arguments[4].Value!)
                : null;
    }

    /// <summary>
    /// A table the module's code maps, and the entity it maps there (MOD0017). The table is compared
    /// as <see cref="TableKey"/>; the entity, when the mapping names one, by its identity, which
    /// carries the assembly: two modules' own internal <c>Shop.Note</c> are two entities. An owned
    /// type's table, a table an entity is split into and a builder of no particular type name no
    /// entity, and leave the last three empty.
    /// </summary>
    public record struct OwnedTable(string Schema, string Table, string Entity, string EntityIdentity, string Spellings)
    {
        /// <summary>The attribute's constructor parameters, in the order <see cref="Arguments"/> writes them and <see cref="Read"/> reads them.</summary>
        public const string Parameters = "string schema, string table, string entity, string entityIdentity, string entitySpellings";

        /// <summary>The table as a reader writes it: <c>Sales.Orders</c>, or <c>Orders</c> without a schema.</summary>
        public readonly string TableName => Schema.Length == 0 ? Table : Schema + "." + Table;

        /// <summary>
        /// What tables are compared by: the name with its schema, without regard to case — SQL Server
        /// and SQLite compare them so by default, and on PostgreSQL two names that differ only in case
        /// are a trap of their own. A table without a schema is not the table of a named schema: which
        /// schema it ends up in depends on the provider and on <c>HasDefaultSchema</c>.
        /// </summary>
        public readonly string TableKey => TableName.ToUpperInvariant();

        /// <summary>The attribute's constructor arguments, as C# literals.</summary>
        public readonly string Arguments() =>
            string.Join(", ", new[] { Schema, Table, Entity, EntityIdentity, Spellings }.Select(value => SymbolDisplay.FormatLiteral(value, quote: true)));

        /// <summary>The mapping an attribute records, or null when it is not one of ours.</summary>
        public static OwnedTable? Read(AttributeData attribute) =>
            attribute.AttributeClass is { } type &&
            type.Name == ModuleRecordsGenerator.TableAttributeName &&
            type.ContainingNamespace?.ContainingNamespace?.ToDisplayString() == ModuleRecordsGenerator.AttributeNamespace &&
            attribute.ConstructorArguments is { Length: 5 } arguments &&
            arguments.All(argument => argument.Value is string)
                ? new OwnedTable((string)arguments[0].Value!, (string)arguments[1].Value!, (string)arguments[2].Value!, (string)arguments[3].Value!, (string)arguments[4].Value!)
                : null;
    }

    /// <summary>The tables the module's code maps.</summary>
    public static IEnumerable<(string Module, OwnedTable Table)> OwnedTables(IAssemblySymbol module) =>
        module.GetAttributes()
            .Select(OwnedTable.Read)
            .Where(table => table is not null)
            .Select(table => (module.Name, table!.Value));

    /// <summary>The services the module's code registers.</summary>
    public static IEnumerable<(string Module, Registration Registration)> Registrations(IAssemblySymbol module) =>
        module.GetAttributes()
            .Select(Registration.Read)
            .Where(registration => registration is not null)
            .Select(registration => (module.Name, registration!.Value));
}
