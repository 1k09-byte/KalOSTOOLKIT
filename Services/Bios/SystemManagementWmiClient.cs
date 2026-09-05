using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;

namespace KaliteKit.Services.Bios;

/// <summary>Wraps a raw <see cref="ManagementBaseObject"/> as an <see cref="IWmiRow"/>.</summary>
internal sealed class WmiObjectRow : IWmiRow
{
    private readonly ManagementBaseObject _obj;

    public WmiObjectRow(ManagementBaseObject obj) => _obj = obj;

    public bool HasProperty(string propertyName) => _obj.Properties.OfType<PropertyData>()
        .Any(p => string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase));

    public object? GetValue(string propertyName)
    {
        var prop = _obj.Properties.OfType<PropertyData>()
            .FirstOrDefault(p => string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase));
        return prop?.Value;
    }

    public string? GetString(string propertyName)
    {
        var value = GetValue(propertyName);
        if (value is string s) return s;
        if (value is null) return null;
        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    public int? GetInt(string propertyName)
    {
        var value = GetValue(propertyName);
        return value switch
        {
            null => null,
            int i => i,
            uint u => (int)u,
            short s => (int)s,
            ushort us => (int)us,
            byte b => (int)b,
            long l => (int)l,
            ulong ul => (int)ul,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    public bool? GetBool(string propertyName)
    {
        var value = GetValue(propertyName);
        return value switch
        {
            null => null,
            bool b => b,
            int i => i != 0,
            uint u => u != 0,
            string s => bool.TryParse(s, out var parsed) ? parsed : null,
            _ => null,
        };
    }

    public IReadOnlyList<string> GetStringArray(string propertyName)
    {
        var value = GetValue(propertyName);
        if (value is string[] arr) return arr;
        if (value is object[] objs)
        {
            return objs.Select(o => Convert.ToString(o, CultureInfo.InvariantCulture) ?? string.Empty).ToList();
        }
        if (value is string one) return new[] { one };
        return Array.Empty<string>();
    }
}

/// <summary>Wraps a WMI method-invocation result.</summary>
internal sealed class WmiMethodInvocationResult : IWmiMethodResult
{
    private readonly ManagementBaseObject? _result;

    public WmiMethodInvocationResult(ManagementBaseObject? result, int? methodExitCodeHint = null)
    {
        _result = result;
        _exitCodeHint = methodExitCodeHint;
    }

    private readonly int? _exitCodeHint;

    public void Dispose() => _result?.Dispose();

    public object? GetOutParameter(string name)
    {
        if (_result is null || string.IsNullOrEmpty(name)) return null;
        var prop = _result.Properties
            .OfType<PropertyData>()
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        return prop?.Value;
    }

    public int? GetInt(string name)
    {
        var value = GetOutParameter(name);
        if (value is null) return _exitCodeHint;
        return value switch
        {
            int i => i,
            uint u => (int)u,
            _ => _exitCodeHint,
        };
    }

    public string? GetString(string name)
    {
        var value = GetOutParameter(name);
        return value switch
        {
            null => null,
            string s => s,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };
    }

    public bool? GetBool(string name)
    {
        var value = GetOutParameter(name);
        return value switch
        {
            null => null,
            bool b => b,
            int i => i != 0,
            _ => null,
        };
    }
}

/// <summary>
/// Production <see cref="IWmiClient"/> backed by System.Management.
/// Handles the two most common WMI exception surface areas (authorization /
/// "not found") and translates them into readable text.
/// </summary>
public sealed class SystemManagementWmiClient : IWmiClient
{
    public async Task<IReadOnlyList<IWmiRow>> QueryAsync(string scope, string wqlQuery, CancellationToken ct = default)
    {
        var rows = new List<IWmiRow>();
        var scopeObj = new ManagementScope(scope, BuildConnectionOptions());
        scopeObj.Connect();

        using var searcher = new ManagementObjectSearcher(scopeObj, new ObjectQuery(wqlQuery));
        using var collection = await Task.Run(() => searcher.Get(), ct).ConfigureAwait(false);
        foreach (ManagementBaseObject obj in collection)
        {
            if (obj.Clone() is ManagementBaseObject cloned)
            {
                rows.Add(new WmiObjectRow(cloned));
            }
        }
        return rows;
    }

    public async Task<IWmiMethodResult?> InvokeMethodAsync(
        string scope,
        string className,
        string whereClause,
        string methodName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken ct = default)
    {
        var scopeObj = new ManagementScope(scope, BuildConnectionOptions());
        scopeObj.Connect();

        var path = new ManagementPath($"{className}.{whereClause}");
        using var obj = new ManagementObject(scopeObj, path, null);
        obj.Get();

        // Build named in-parameters via GetMethodParameters so parameter order never
        // matters and the InvokeMethod(ManagementBaseObject) overload is unambiguous.
        using var inParams = obj.GetMethodParameters(methodName);
        if (inParams is not null)
        {
            foreach (var kvp in arguments)
            {
                bool matches = inParams.Properties.OfType<PropertyData>()
                    .Any(p => string.Equals(p.Name, kvp.Key, StringComparison.OrdinalIgnoreCase));
                if (matches)
                {
                    inParams[kvp.Key] = kvp.Value;
                }
            }
        }

        var resultBox = await Task.Run(() => obj.InvokeMethod(methodName, inParams, new InvokeMethodOptions()), ct).ConfigureAwait(false);
        if (resultBox is null)
        {
            return new WmiMethodInvocationResult(null);
        }
        using (resultBox)
        {
            // Clone before the source is released so the returned wrapper stays valid.
            var clone = resultBox.Clone() as ManagementBaseObject;
            return new WmiMethodInvocationResult(clone);
        }
    }

    private static ConnectionOptions BuildConnectionOptions()
    {
        // No impersonation needed for the built-in WMI providers we target; using
        // the current identity keeps scope synchronous with the app's elevation.
        return new ConnectionOptions
        {
            Timeout = TimeSpan.FromSeconds(20),
            EnablePrivileges = true,
            Authentication = AuthenticationLevel.Default,
            Impersonation = ImpersonationLevel.Impersonate,
        };
    }
}