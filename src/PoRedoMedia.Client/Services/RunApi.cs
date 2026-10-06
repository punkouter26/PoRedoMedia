using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.Client.Services;

/// <summary>Starting and following runs.</summary>
public sealed class RunApi(HttpClient http, NavigationManager navigation)
{
    public async Task<(RunDto? Run, string? Error)> StartAsync(RunRequest request)
    {
        using var response = await http.PostAsJsonAsync("api/runs", request, WireJson.Default.RunRequest);
        return response.IsSuccessStatusCode
            ? (await response.Content.ReadFromJsonAsync(WireJson.Default.RunDto), null)
            : (null, await MediaApi.ReasonAsync(response));
    }

    /// <summary>Asks a queued or running run to stop. Returns the reason when it could not be.</summary>
    public async Task<string?> CancelAsync(Guid id)
    {
        using var response = await http.DeleteAsync($"api/runs/{id}");
        return response.IsSuccessStatusCode ? null : await MediaApi.ReasonAsync(response);
    }

    public Task<RunDto?> GetAsync(Guid id) => http.GetFromJsonAsync($"api/runs/{id}", WireJson.Default.RunDto);

    /// <summary>The user's runs, newest first.</summary>
    public async Task<List<RunDto>> ListAsync() => await http.GetFromJsonAsync("api/runs", WireJson.Default.ListRunDto) ?? [];

    public Task<QuotaStatusDto?> GetQuotaAsync() => http.GetFromJsonAsync("api/quota", WireJson.Default.QuotaStatusDto);

    public async Task<List<MemeTemplateDto>> GetMemeTemplatesAsync() =>
        await http.GetFromJsonAsync("api/meme-templates", WireJson.Default.ListMemeTemplateDto) ?? [];

    public async Task<List<string>> GetBulkPromptsAsync() =>
        await http.GetFromJsonAsync("api/bulk-prompts", WireJson.Default.ListString) ?? [];

    public async Task<string?> SaveBulkPromptsAsync(List<string> prompts)
    {
        using var response = await http.PutAsJsonAsync("api/bulk-prompts", prompts, WireJson.Default.ListString);
        return response.IsSuccessStatusCode ? null : await MediaApi.ReasonAsync(response);
    }

    /// <summary>
    /// Subscribes to a run's progress events. Dispose the result to stop. Events sent before the
    /// subscription was in place are not replayed, so read the run once after this returns.
    /// </summary>
    /// <param name="onGap">
    /// Called after a reconnect, when events may have been missed. The caller reads the run again
    /// there, or a run that ended in the gap spins forever.
    /// </param>
    /// <param name="onClosed">Called when reconnecting is given up: no more events will come.</param>
    public async Task<IAsyncDisposable> FollowAsync(Guid runId, Func<RunProgressDto, Task> onProgress, Func<Task> onGap, Func<Task> onClosed)
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(navigation.ToAbsoluteUri("hubs/run"))
            .WithAutomaticReconnect()
            .AddJsonProtocol(o => o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, WireJson.Default))
            .Build();
        hub.On("RunProgress", onProgress);
        hub.Reconnected += async _ =>
        {
            await hub.InvokeAsync("JoinRun", runId.ToString());
            await onGap();
        };
        hub.Closed += _ => onClosed();
        await hub.StartAsync();
        await hub.InvokeAsync("JoinRun", runId.ToString());
        return hub;
    }
}
