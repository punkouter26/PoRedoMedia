using System.Net.Http.Json;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Client.Services;

/// <summary>The one cached read of <c>GET /api/config</c>.</summary>
public sealed class AppConfigService(HttpClient http)
{
    private Task<AppConfigDto?>? _config;

    public Task<AppConfigDto?> GetAsync() => _config ??= http.GetFromJsonAsync("api/config", WireJson.Default.AppConfigDto);
}
