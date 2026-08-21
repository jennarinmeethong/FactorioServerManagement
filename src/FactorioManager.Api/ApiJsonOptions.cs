using System.Text.Json;
using System.Text.Json.Serialization;

namespace FactorioManager.Api;

public static class ApiJsonOptions
{
    public static void Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
    }
}
