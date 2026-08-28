using System.Text.Json;
using System.Text.Json.Serialization;

namespace PerformanceMonitor.Networking;

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Api = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };
}
