using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using PoRedoMedia.Api.Common.Ai;
using PoRedoMedia.Api.Features.MemeCaption;
using PoRedoMedia.Shared.Enums;

namespace PoRedoMedia.Api.Features.Eval;

/// <summary>
/// CLI: <c>dotnet run --project src/PoRedoMedia.Api -- eval-ai [--dir &lt;folder&gt;] [--count 10] [--ollama &lt;model&gt;] [--out &lt;file.html&gt;]</c>.
/// Writes meme captions for a fixed set of pictures with each caption variant, has the judge score
/// them side by side, and writes an HTML report. A prompt or model change is then a number that
/// moved, not an impression. It calls real models and says what that cost.
/// </summary>
/// <remarks>
/// The pictures come from <c>--dir</c>, or else are the newest uploaded pictures in storage, so it
/// runs with no setup on a machine that has used the app. The judge is the one that picks the best
/// draft in the app (<see cref="AiJudge"/>), so the report measures what production optimises for.
/// Variants: one draft; the best of several by the judge; and, with <c>--ollama</c>, one draft from
/// a local model, which costs nothing and is what a free prompt iteration loop runs on.
/// </remarks>
public static class EvalAiCommand
{
    private sealed record Variant(string Name, ICaptionWriter Writer);

    private sealed record Row(string Name, byte[] Image, string Description, List<(string Variant, string Caption, double Score)> Results);

    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var chat = services.GetService<IChatClient>();
        var judge = services.GetService<AiJudge>();
        var vision = services.GetService<IVisionServiceRouter>();
        if (chat is null || judge is null || vision is null)
        {
            Console.Error.WriteLine("eval-ai needs a chat model (OpenAI:Endpoint and OpenAI:Key) and a vision provider.");
            return 1;
        }

        var pictures = await PicturesAsync(services, Option(args, "--dir"), int.TryParse(Option(args, "--count"), out var n) ? n : 10);
        if (pictures.Count == 0)
        {
            Console.Error.WriteLine("No pictures: pass --dir <folder of .jpg/.png>, or upload some pictures in the app first.");
            return 1;
        }

        List<Variant> variants =
        [
            new("one draft", new ChatCaptionWriter(chat)),
            new($"best of {ChatCaptionWriter.Drafts}", new ChatCaptionWriter(chat, judge)),
        ];
        if (Option(args, "--ollama") is { } local)
            variants.Add(new($"ollama:{local}", new ChatCaptionWriter(ChatClients.Ollama(configuration, local))));

        var tally = AiMeter.Begin();
        var rows = new List<Row>();
        foreach (var (name, original) in pictures)
        {
            var image = ImageBytes.ForProcessing(original);
            Console.WriteLine($"· {name}");
            var seen = await vision.Resolve(null).AnalyzeAsync(image);

            var captions = new List<(string Variant, string Caption)>();
            foreach (var variant in variants)
            {
                try
                {
                    var caption = await variant.Writer.WriteAsync(seen.Description, seen.Tags);
                    captions.Add((variant.Name, $"{caption.Top} / {caption.Bottom}"));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    captions.Add((variant.Name, $"(failed: {ex.Message})"));
                }
            }

            // Scored side by side, so each score is relative to the other variants on the same picture.
            var scores = await judge.ScoreAsync($"Meme caption for: {seen.Description}", ChatCaptionWriter.Rubric, [.. captions.Select(c => c.Caption)]);
            rows.Add(new Row(name, ImageBytes.ToJpeg(original, 320), seen.Description,
                [.. captions.Select((c, i) => (c.Variant, c.Caption, c.Caption.StartsWith("(failed", StringComparison.Ordinal) ? 0 : scores[i]))]));
        }

        var output = Option(args, "--out")
            ?? Path.Combine("artifacts", "eval", $"eval-{DateTime.Now.ToString("yyMMdd-HHmm", CultureInfo.InvariantCulture)}.html");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, Report(variants.Select(v => v.Name).ToList(), rows, tally));

        Console.WriteLine();
        foreach (var variant in variants)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {variant.Name,-24} mean {Mean(rows, variant.Name):0.00}  wins {Wins(rows, variant.Name)}"));
        Console.WriteLine($"  {tally.Summary()}");
        Console.WriteLine($"Report: {Path.GetFullPath(output)}");
        return 0;
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>The pictures in a folder, or else the newest pictures users uploaded.</summary>
    private static async Task<List<(string Name, byte[] Bytes)>> PicturesAsync(IServiceProvider services, string? dir, int count)
    {
        if (dir is not null)
        {
            return [.. Directory.EnumerateFiles(dir)
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp")
                .Order(StringComparer.OrdinalIgnoreCase)
                .Take(count)
                .Select(f => (Path.GetFileName(f), File.ReadAllBytes(f)))];
        }

        var blobs = services.GetRequiredService<BlobStorageService>();
        var uploads = (await services.GetRequiredService<IMediaRepository>().ListAllAsync())
            .Where(m => m is { Kind: MediaKind.Image, Status: MediaStatus.Ready, ParentId: null })
            .OrderByDescending(m => m.CreatedAt)
            .Take(count);
        var pictures = new List<(string, byte[])>();
        foreach (var item in uploads)
            pictures.Add((item.Title, await blobs.ReadAllBytesAsync(item.SourcePath)));
        return pictures;
    }

    private static double Mean(List<Row> rows, string variant) =>
        rows.Select(r => r.Results.First(x => x.Variant == variant).Score).DefaultIfEmpty(0).Average();

    private static int Wins(List<Row> rows, string variant) =>
        rows.Count(r => r.Results.MaxBy(x => x.Score).Variant == variant);

    private static string Report(List<string> variants, List<Row> rows, AiTally tally)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var inv = CultureInfo.InvariantCulture;
        var html = new StringBuilder();
        html.Append("""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>AI caption eval</title><style>
            :root{--bg:#fff;--fg:#1a1a1a;--muted:#666;--line:#ddd;--win:#e6f4ea}
            @media (prefers-color-scheme:dark){:root{--bg:#141414;--fg:#eee;--muted:#999;--line:#333;--win:#173d24}}
            body{background:var(--bg);color:var(--fg);font:15px/1.45 system-ui,sans-serif;margin:0 auto;max-width:1100px;padding:16px}
            table{border-collapse:collapse;width:100%}td,th{border-bottom:1px solid var(--line);padding:8px;text-align:left;vertical-align:top}
            img{width:160px;border-radius:6px}.muted{color:var(--muted);font-size:13px}.win{background:var(--win)}
            .score{font-variant-numeric:tabular-nums;font-weight:600}
            </style></head><body>
            """);
        html.Append(inv, $"<h1>Meme caption eval</h1><p class=\"muted\">{rows.Count} picture(s) · {E(DateTime.Now.ToString("yyyy-MM-dd HH:mm", inv))} · {E(tally.Summary() ?? "no AI calls")}</p>");
        html.Append("<h2>Summary</h2><table><tr><th>Variant</th><th>Mean score (1–10)</th><th>Wins</th></tr>");
        foreach (var v in variants)
            html.Append(inv, $"<tr><td>{E(v)}</td><td class=\"score\">{Mean(rows, v):0.00}</td><td>{Wins(rows, v)}</td></tr>");
        html.Append("</table><h2>Pictures</h2><table><tr><th>Picture</th>");
        foreach (var v in variants)
            html.Append(inv, $"<th>{E(v)}</th>");
        html.Append("</tr>");
        foreach (var row in rows)
        {
            html.Append(inv, $"<tr><td><img alt=\"{E(row.Name)}\" src=\"data:image/jpeg;base64,{Convert.ToBase64String(row.Image)}\"><div class=\"muted\">{E(row.Description)}</div></td>");
            var best = row.Results.Max(x => x.Score);
            foreach (var (_, caption, score) in row.Results)
                html.Append(inv, $"<td{(score == best ? " class=\"win\"" : "")}><div>{E(caption)}</div><div class=\"score\">{score:0.#}</div></td>");
            html.Append("</tr>");
        }

        html.Append("</table></body></html>");
        return html.ToString();
    }
}
