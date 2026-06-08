using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FinPlat.TestContainers.Fixtures;

/// <summary>
/// Fluent builder for creating typed test fixtures. Supports default values,
/// overrides via With(), and serialization to JSON/Base64 for queue messages.
/// </summary>
/// <typeparam name="T">The fixture type to build.</typeparam>
public class FixtureBuilder<T> where T : class, new()
{
    private readonly List<Action<T>> _configurations = new();

    /// <summary>
    /// Creates a new fixture builder with default values for <typeparamref name="T"/>.
    /// </summary>
    public FixtureBuilder() { }

    /// <summary>
    /// Creates a fixture builder starting from a template instance.
    /// The template is cloned via JSON round-trip to avoid mutating the original.
    /// </summary>
    public FixtureBuilder(T template)
    {
        var json = JsonSerializer.Serialize(template);
        var clone = JsonSerializer.Deserialize<T>(json)!;
        _configurations.Add(_ => CopyProperties(clone, _));
    }

    /// <summary>
    /// Applies a configuration action to the fixture.
    /// Multiple With() calls are applied in order.
    /// </summary>
    public FixtureBuilder<T> With(Action<T> configure)
    {
        _configurations.Add(configure);
        return this;
    }

    /// <summary>Builds the fixture instance.</summary>
    public T Build()
    {
        var instance = new T();
        foreach (var config in _configurations)
            config(instance);
        return instance;
    }

    /// <summary>Builds and serializes to JSON.</summary>
    public string BuildJson(JsonSerializerOptions? options = null)
        => JsonSerializer.Serialize(Build(), options ?? DefaultJsonOptions);

    /// <summary>Builds and serializes to base64-encoded JSON (for queue messages).</summary>
    public string BuildBase64Json(JsonSerializerOptions? options = null)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(BuildJson(options)));

    private static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static void CopyProperties(T source, T target)
    {
        foreach (var prop in typeof(T).GetProperties())
        {
            if (prop.CanRead && prop.CanWrite)
            {
                var value = prop.GetValue(source);
                if (value is not null)
                    prop.SetValue(target, value);
            }
        }
    }
}

/// <summary>
/// Registry of named fixture templates. Register reusable fixture definitions
/// once and retrieve them by name across test classes.
/// </summary>
public class FixtureRegistry
{
    private readonly Dictionary<string, object> _factories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a named fixture factory.
    /// </summary>
    /// <typeparam name="T">The fixture type.</typeparam>
    /// <param name="name">Unique name for this fixture (e.g., "bigcat-closed-order").</param>
    /// <param name="factory">Factory function that creates the fixture.</param>
    public FixtureRegistry Register<T>(string name, Func<T> factory) where T : class
    {
        _factories[name] = factory;
        return this;
    }

    /// <summary>
    /// Creates a new instance of a registered fixture.
    /// </summary>
    /// <typeparam name="T">Expected fixture type.</typeparam>
    /// <param name="name">Registered fixture name.</param>
    public T Create<T>(string name) where T : class
    {
        if (!_factories.TryGetValue(name, out var factory))
            throw new InvalidOperationException($"No fixture registered with name '{name}'.");

        if (factory is not Func<T> typedFactory)
            throw new InvalidCastException(
                $"Fixture '{name}' is not of type {typeof(T).Name}.");

        return typedFactory();
    }

    /// <summary>
    /// Creates a builder pre-populated from a registered fixture template.
    /// Allows further customization before building.
    /// </summary>
    public FixtureBuilder<T> CreateBuilder<T>(string name) where T : class, new()
    {
        var instance = Create<T>(name);
        return new FixtureBuilder<T>(instance);
    }

    /// <summary>Returns true if a fixture with the given name is registered.</summary>
    public bool Contains(string name) => _factories.ContainsKey(name);

    /// <summary>Returns all registered fixture names.</summary>
    public IEnumerable<string> GetNames() => _factories.Keys;
}
