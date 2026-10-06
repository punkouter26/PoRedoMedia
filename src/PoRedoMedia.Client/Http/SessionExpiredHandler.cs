using System.Net;
using Microsoft.AspNetCore.Components;

namespace PoRedoMedia.Client.Http;

/// <summary>
/// Sends the browser to the sign-in page when the server says the session is over. Without this
/// a lapsed cookie surfaces as a failed read in whatever component happened to ask next.
/// </summary>
public sealed class SessionExpiredHandler(NavigationManager navigation) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized
            && request.RequestUri?.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal) == true)
        {
            var here = "/" + navigation.ToBaseRelativePath(navigation.Uri);
            if (!here.StartsWith("/login", StringComparison.Ordinal))
                navigation.NavigateTo($"login?returnUrl={Uri.EscapeDataString(here)}", forceLoad: true);
        }

        return response;
    }
}
