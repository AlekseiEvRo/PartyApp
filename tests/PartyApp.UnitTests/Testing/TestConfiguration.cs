using Microsoft.Extensions.Configuration;

namespace PartyApp.UnitTests.Testing;

public static class TestConfiguration
{
    public static IConfigurationRoot Create(params (string Key, string? Value)[] values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
    }

    public static IConfigurationRoot Empty() => Create();
}