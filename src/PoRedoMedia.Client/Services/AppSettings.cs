using System.Diagnostics.CodeAnalysis;
using Microsoft.JSInterop;

namespace PoRedoMedia.Client.Services;

/// <summary>One switch under Settings → Advanced.</summary>
public sealed record AppSetting(string Key, string Label, string Hint);

/// <summary>
/// The switches under Settings → Advanced, remembered in this browser. Every one starts off, so the
/// app first shows its simplest face; switching one on adds to the screen and takes nothing away.
/// </summary>
// ponytail: per browser, not per account, like UiStore.
public sealed class AppSettings(IJSRuntime js)
{
    public static readonly AppSetting OnDeviceAi = new("po.adv.device-ai", "On-device AI",
        "Describe pictures in this browser, for free. Downloads about 230 MB the first time.");

    public static readonly AppSetting Recipes = new("po.adv.recipes", "Recipes",
        "Save a set of ticked functions and use it again with one tap.");

    public static readonly AppSetting CostEstimates = new("po.adv.costs", "Cost estimates",
        "What each run is likely to cost in AI fees, and the total for this tab.");

    /// <summary>Kept under the key the effects layer reads (js/fx/fx-core.js), which also honours reduced motion.</summary>
    public static readonly AppSetting VisualEffects = new("po.fx", "Visual effects",
        "Glass backdrop, particles, the run orb, picture reveals and confetti.");

    public static readonly AppSetting[] Advanced = [OnDeviceAi, Recipes, CostEstimates, VisualEffects];

    /// <summary>Raised after a switch changes, so whatever it shows or hides can update.</summary>
    public event Action? Changed;

    // Read straight from the browser's storage: the WASM runtime calls JS in-process, so pages
    // can ask while they render instead of waiting a round trip.
    public bool this[AppSetting setting] => Read(setting.Key) == "on";

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The result is a string, which trimming always keeps.")]
    private string? Read(string key) => js is IJSInProcessRuntime local ? local.Invoke<string?>("poMedia.read", key) : null;

    public async Task SetAsync(AppSetting setting, bool on)
    {
        if (setting == VisualEffects)
            await js.InvokeVoidAsync("poFx.setEnabled", on);
        else
            await js.InvokeVoidAsync("poMedia.write", setting.Key, on ? "on" : null);
        Changed?.Invoke();
    }
}
