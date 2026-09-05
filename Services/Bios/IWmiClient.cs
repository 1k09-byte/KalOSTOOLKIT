using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KaliteKit.Services.Bios;

/// <summary>
/// One row of a WMI query result, with case-insensitive property lookup.
/// Kept intentionally thin so it can be faked in unit tests without touching
/// the real <see cref="System.Management"/> stack.
/// </summary>
public interface IWmiRow
{
    object? GetValue(string propertyName);
    string? GetString(string propertyName);
    int? GetInt(string propertyName);
    bool? GetBool(string propertyName);
    IReadOnlyList<string> GetStringArray(string propertyName);
    bool HasProperty(string propertyName);
}

/// <summary>
/// Thin seam around WMI (System.Management). Providers depend on this instead
/// of <see cref="System.Management.ManagementObjectSearcher"/> directly so the
/// whole BIOS layer is unit-testable with a fake implementation.
/// </summary>
public interface IWmiClient
{
    /// <summary>Enumerates rows for a WQL query against the given scope root (e.g. "root\\dell\\sysmgmt").</summary>
    Task<IReadOnlyList<IWmiRow>> QueryAsync(string scope, string wqlQuery, CancellationToken ct = default);

    /// <summary>
    /// Invokes a WMI method on a single instance identified by a WQL "WHERE" filter.
    /// Returns the raw method result object (used to read the vendor return-code property).
    /// </summary>
    Task<IWmiMethodResult?> InvokeMethodAsync(
        string scope,
        string className,
        string whereClause,
        string methodName,
        IReadOnlyDictionary<string, object?> inParams,
        CancellationToken ct = default);
}

/// <summary>Raw WMI method invocation result — enough to read vendor status codes.</summary>
public interface IWmiMethodResult : IDisposable
{
    object? GetOutParameter(string name);
    int? GetInt(string name);
    string? GetString(string name);
    bool? GetBool(string name);
}