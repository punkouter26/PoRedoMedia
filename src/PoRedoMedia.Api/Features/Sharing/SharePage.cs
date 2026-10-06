using System.Text.Encodings.Web;
using PoRedoMedia.Shared.Enums;

namespace PoRedoMedia.Api.Features.Sharing;

/// <summary>The public page a share link opens.</summary>
internal static class SharePage
{
    /// <summary>A self-contained page: link previewers and visitors never load the app itself.</summary>
    public static string Render(MediaItem item, string url)
    {
        var encode = HtmlEncoder.Default;
        var title = encode.Encode(item.Title);
        var body = item.Kind switch
        {
            MediaKind.Image => $"""<img src="{url}/content" alt="{title}">""",
            MediaKind.Video => $"""<video src="{url}/content" poster="{url}/thumb" controls playsinline preload="metadata"></video>""",
            _ => $"""<audio src="{url}/content" controls></audio>""",
        };
        if (item.Text is not null)
            body += $"<pre>{encode.Encode(item.Text)}</pre>";

        var preview = item.Kind == MediaKind.Audio ? "" : $"""<meta property="og:image" content="{url}/{(item.Kind == MediaKind.Image ? "content" : "thumb")}">""";
        if (item.Kind == MediaKind.Video)
            preview += $"""<meta property="og:video" content="{url}/content"><meta property="og:video:type" content="video/mp4">""";

        return Shell(
            title, $"<h1>{title}</h1>{body}",
            $"""<meta property="og:title" content="{title}"><meta property="og:url" content="{url}"><meta property="og:site_name" content="PoRedoMedia">{preview}""");
    }

    public static string Shell(string title, string body, string head) => $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="robots" content="noindex">
        <title>{{title}} · PoRedoMedia</title>
        {{head}}
        <style>
        body { margin: 0; padding: 1.5rem 1rem; background: #f4f5f7; color: #16181d; font: 16px/1.5 system-ui, sans-serif; }
        main { max-width: 960px; margin: 0 auto; }
        h1 { font-size: 1.4rem; overflow-wrap: anywhere; }
        img, video { max-width: 100%; max-height: 80vh; border-radius: 6px; background: #e3e6eb; }
        audio { width: 100%; }
        pre { white-space: pre-wrap; font: inherit; }
        footer { margin-top: 2rem; color: #4b5563; }
        </style>
        </head>
        <body><main>{{body}}<footer>Made with PoRedoMedia</footer></main></body>
        </html>
        """;
}
