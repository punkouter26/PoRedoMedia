using PoRedoMedia.Api.Common;
using PoRedoMedia.Api.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveWebAssemblyComponents();

var app = builder.Build();

app.UseAntiforgery();

app.MapHealth();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(PoRedoMedia.Client.Routes).Assembly);

app.Run();

public partial class Program;
