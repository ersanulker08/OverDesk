namespace Overdesk.Core.Platform;

/// <summary>Small secrets (API keys) kept in the OS keychain, never in the settings file.</summary>
public interface ISecretStore
{
    string? Get(string name);

    /// <summary>Store a secret; null or empty removes it.</summary>
    void Set(string name, string? value);

    /// <summary>Same as <see cref="Set"/>, but false when the store refused it, so the caller can tell the user.</summary>
    bool TrySet(string name, string? value)
    {
        Set(name, value);
        return true;
    }
}

/// <summary>Fallback where no keychain exists: keeps secrets for this run only.</summary>
public sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new();
    public string? Get(string name) => _values.GetValueOrDefault(name);

    public void Set(string name, string? value)
    {
        if (string.IsNullOrEmpty(value)) _values.Remove(name);
        else _values[name] = value;
    }
}
