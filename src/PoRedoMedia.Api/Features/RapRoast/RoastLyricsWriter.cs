using PoRedoMedia.Api.Common.Ai;
using PoRedoMedia.Shared.Enums;
using System.Diagnostics;

namespace PoRedoMedia.Api.Features.RapRoast;

/// <summary>
/// Writes the roast bars from an image description.
/// </summary>
/// <remarks>
/// Backed by <see cref="IChatCompletionService"/> (Azure OpenAI) with a deterministic heuristic
/// fallback — so the feature still works when no chat provider is configured.
/// </remarks>
public sealed class RoastLyricsWriter(IChatCompletionService chat, ILogger<RoastLyricsWriter> logger)
{
    /// <summary>
    /// What the roast may aim at. Fixed — no dial, no toggle, no intensity moves this.
    /// </summary>
    /// <remarks>
    /// This is a product decision and a practical one at once. Lyria applies safety filters to every
    /// prompt, so a roast that crosses these lines is refused upstream and the user gets no track —
    /// keeping the jabs on choices rather than characteristics is what makes the feature work at all.
    /// <para>
    /// Note what is <em>not</em> in here any more: profanity. Swearing is a register, not a target,
    /// and the two used to be welded into one constant so the only way to ask for harder language
    /// was to ask for a crueller subject. <c>CleanLanguage</c> / <c>ExplicitLanguage</c> own that axis now.
    /// </para>
    /// </remarks>
    internal const string TargetGuardrail =
        "Everything the subject CHOSE is fair game and you should be ruthless about all of it: the "
        + "outfit and every piece of it, the styling, the pose, the face they decided to pull, the "
        + "props, the room and the state of it, the lighting, the taste on display, the effort level, "
        + "the try-hard energy or the total absence of it, what the whole scene says about how they "
        + "spend their time — and the fact that out of every photo they could have picked, they "
        + "picked THIS one and thought it was a good idea. "
        + "The ONE thing you may never touch is who they are rather than what they did: "
        + "NEVER reference or imply race, ethnicity, skin tone, disability, body weight or size, "
        + "age, gender identity, sexual orientation, religion, or medical conditions. "
        + "No slurs, no sexual content, no threats of violence. "
        + "That is the entire list. Everything not on it is yours.";

    private const string CleanLanguage =
        "Keep the language clean — no profanity. "
        + "Keep it the kind of playful burn friends trade with each other, not cruelty.";

    private const string ExplicitLanguage =
        "EXPLICIT MODE. This is an uncensored diss track, not a comedy-club set. "
        + "Swear heavily and naturally throughout — fuck, shit, bitch, ass, damn and the rest of the "
        + "ordinary battle-rap vocabulary belong in these bars, and EVERY line should carry at least "
        + "one; several is better. Do not asterisk, bleep, initialise or soften a single word; write "
        + "them out in full. "
        + "Profanity here is the register, not seasoning: a clean bar in this mode is a failed bar. "
        + "Slurs remain the one hard exception — swearing is licence to be filthy and crude about "
        + "what somebody DID, never licence to attack who they ARE.";

    /// <summary>The two axes, assembled. Targets never move; language is the caller's call.</summary>
    internal static string Guardrail(bool explicitLanguage) =>
        TargetGuardrail + " " + (explicitLanguage ? ExplicitLanguage : CleanLanguage);

    /// <summary>
    /// Persona and form. Both move with <paramref name="style"/>, which is why this takes it.
    /// </summary>
    /// <remarks>
    /// <see cref="RapStyle.StandUp"/> is not a backing track with a different label — a comedian
    /// does not rhyme, and leaving "make the lines rhyme and scan to a beat" in place while calling
    /// the delivery stand-up would have produced rapped bars with a new name on the button, which is
    /// the "control that does nothing" failure this feature has already shipped once.
    /// <para>
    /// The section tags survive in both forms, and that is not cosmetic: Lyria performs what the
    /// tags delimit, and the karaoke highlighting estimates line timings from them. A tagless
    /// comedy set would break the player, so the set is written INTO that shape instead — the run
    /// of jokes under [Verse], the recurring tagline under [Chorus].
    /// </para>
    /// </remarks>
    private static string SystemPrompt(bool explicitLanguage, RapStyle style) =>
        (style == RapStyle.StandUp
            ? "You are a stand-up comedian on stage doing a roast set about the person in this photo. "
              + "Reply with ONLY the set — no preamble, no explanation, no markdown fences. "
              + "Structure it with section tags on their own lines: [Verse] then [Chorus]. "
              + "Write 8 lines of jokes under [Verse] and a 4-line recurring tagline under [Chorus] — "
              + "the bit the room chants back at them. "
              + "Write PROSE PUNCHLINES, not bars. Rhyme is not required and a forced rhyme is worse "
              + "than none — if a line only exists because it rhymes, it is a dead line. Build each "
              + "joke as setup, then turn, then tag. Time it for a laugh, not for a beat. "
            : "You are writing a roast verse for a battle rap. "
              + "Reply with ONLY the lyrics — no preamble, no explanation, no markdown fences. "
              + "Structure the output with section tags on their own lines: [Verse] then [Chorus]. "
              + "Write 8 lines under [Verse] and 4 under [Chorus]. Make the lines rhyme and scan to "
              + "a beat. ")
        + Craft + " "
        + Guardrail(explicitLanguage);

    /// <summary>
    /// How a bar is built. Applies at every intensity, because this is craft rather than tone.
    /// </summary>
    /// <remarks>
    /// Added after a live Scorched run came back as an inventory of the photograph — "Gray floor,
    /// white doors, thermostat in the back", "Zebra dress, pose fixed". Every detail was accurate
    /// and not one was a joke. Handed a rich scene description, the model was reciting it with
    /// adjectives attached and letting the rhyme carry the line, which reads as tame no matter how
    /// hard the tone instruction shouts. Naming the detail is the setup; the prompt never asked for
    /// the turn, so it never got one. It also padded to the rhyme ("now everybody laughs within"),
    /// which is the same failure wearing a different hat.
    /// </remarks>
    private const string Craft =
        "CRAFT RULES, and they matter more than the word count: "
        + "Every single line must be a JOKE — a target plus a punchline. "
        + "Naming something you can see in the photo is ONLY the setup; a bar that stops at the "
        + "description is a wasted bar. State the detail, then turn it into an accusation about the "
        + "person who chose it. "
        + "Do NOT write lists of nouns with adjectives stapled on, and do not narrate the room. "
        + "No filler line ever exists just to complete a rhyme — if a line has no joke in it, "
        + "throw it out and write a different one. "
        + "Be specific to THIS photo: a bar that would work on any other picture is a dead bar. "
        + "Address the subject directly as \"you\" — narrating them in the third person is a distance "
        + "the verse cannot afford. "
        + "Cut every hedge: no \"kind of\", no \"a little\", no simile that softens the hit.";

    /// <summary>
    /// Steps an intensity down one notch. This is what <c>softened</c> means now: the retry after a
    /// refusal re-runs the dial one position cooler rather than jumping straight to the mildest
    /// setting, so a Scorched request that upset the filter comes back as a Roast — still the track
    /// the user asked for, just survivable.
    /// </summary>
    internal static RoastIntensity StepDown(RoastIntensity intensity) => intensity switch
    {
        RoastIntensity.Nuclear => RoastIntensity.Scorched,
        RoastIntensity.Scorched => RoastIntensity.Roast,
        RoastIntensity.Roast => RoastIntensity.Gentle,
        _ => RoastIntensity.Gentle,
    };

    public async Task<RoastLyrics> WriteAsync(
        string imageDescription,
        IReadOnlyList<string> tags,
        RapStyle style,
        RoastIntensity intensity,
        bool softened,
        bool explicitLanguage = false,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // One retry, so it gets to concede exactly one thing — and which one is not arbitrary.
        // Profanity is both the likeliest reason a music model's filter objected and the cheapest
        // thing to give up: dropping it keeps every punchline and every target intact, where
        // stepping the dial down rewrites the jokes. So an explicit request cleans its mouth and
        // keeps the intensity it asked for; a request that was already clean has nothing left to
        // trade but intensity, which is the behaviour this had before the toggle existed.
        var effective = softened && !explicitLanguage ? StepDown(intensity) : intensity;

        // Why the canned bars, when they turn up. Silence here is the failure this exists to stop:
        // the heuristic is mild and clean by construction, so a filtered Scorched+Explicit draft
        // came back reading as though the model had simply decided to be nice, with nothing on
        // screen to say a filter had eaten it. A degraded roast has to name its own degradation.
        string? fallbackReason = null;
        var filterRejected = false;

        if (chat.IsConfigured)
        {
            try
            {
                var lyrics = await WriteWithAiAsync(
                    imageDescription, tags, style, effective, softened, explicitLanguage, ct);
                if (!string.IsNullOrWhiteSpace(lyrics))
                {
                    sw.Stop();
                    return new RoastLyrics(lyrics, softened, sw.ElapsedMilliseconds, explicitLanguage && softened);
                }

                // Empty content IS the shape an Azure content-filter refusal takes — the service
                // logs the finish reason and hands back an empty string rather than throwing.
                logger.LogWarning("Roast lyric model returned empty content; falling back to heuristic.");
                filterRejected = true;
                fallbackReason = explicitLanguage
                    ? "The lyric model's content filter rejected the explicit draft, so these are the "
                      + "stock bars. Turning Explicit off usually gets a real verse back."
                    : "The lyric model's content filter rejected the draft, so these are the stock bars.";
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Roast lyric AI path failed; falling back to heuristic.");
                fallbackReason =
                    $"The lyric model could not be reached ({ex.GetType().Name}), so these are the stock bars.";
            }
        }
        else
        {
            fallbackReason = "No chat model is configured, so these are the stock bars.";
        }

        sw.Stop();
        return new RoastLyrics(
            Heuristic(imageDescription, tags, effective), softened, sw.ElapsedMilliseconds,
            explicitLanguage && softened, fallbackReason, filterRejected);
    }

    private async Task<string> WriteWithAiAsync(
        string imageDescription,
        IReadOnlyList<string> tags,
        RapStyle style,
        RoastIntensity effective,
        bool softened,
        bool explicitLanguage,
        CancellationToken ct)
    {
        // The softened pass is the one that gave the profanity up, so it is never explicit itself —
        // but it still has to know it was asked for, to say which of the two dials moved.
        var effectiveExplicit = explicitLanguage && !softened;

        // Two independent things are said here, and they must stay independent. The intensity line
        // is what the user asked for; the retry line is the fact that the music provider already
        // rejected one draft. Folding the second into the first is what the old code did, which made
        // every retry read as "be gentle" no matter which dial position it started from.
        var toneDirection = ToneDirection(effective);

        if (softened)
        {
            // Name the concession that was actually made. Telling a model "be gentler" when what
            // changed was the profanity rule makes it soften the jokes too, and the user loses the
            // roast twice over for one refusal.
            var concession = explicitLanguage
                ? "the explicit language has been switched off — keep the punchlines, clean the words"
                : $"the dial has been stepped down to {effective}";

            toneDirection =
                "This is a SECOND attempt — the first draft was rejected by the music provider as too "
                + $"harsh, so {concession}. {toneDirection}";
        }

        var user =
            $"Photo description: {imageDescription}\n"
            + $"Detected tags: {string.Join(", ", tags)}\n"
            + $"Track style: {StyleDescription(style)}\n\n"
            + $"{toneDirection}\n"
            + "Write the roast verse and chorus now.";

        var result = await chat.CompleteAsync(SystemPrompt(effectiveExplicit, style), user, ct: ct);

        logger.LogInformation(
            "Roast lyrics written by model in {Elapsed}ms. Tokens={Tokens}, Intensity={Intensity}, "
            + "Softened={Softened}, Explicit={Explicit}",
            result.ElapsedMs, result.TokensUsed, effective, softened, effectiveExplicit);

        return Sanitize(result.Content);
    }

    /// <summary>
    /// The one instruction that moves with the dial.
    /// </summary>
    /// <remarks>
    /// <see cref="RoastIntensity.Scorched"/> is deliberately nasty, and says so in the imperative —
    /// a model given a hedged instruction hedges, and the old wording ("genuine bite", "harsher
    /// delivery") produced bars that read as a friendly ribbing however the dial was set.
    /// <para>
    /// It still restates where the line is, because turning a model up is exactly when it reaches
    /// for the cheap shot — appearance, age, weight. That single sentence is the only thing holding
    /// at this setting, so it is worth the tokens. Scorched is also the setting most likely to be
    /// refused by the music model, which is what the step-down retry is for.
    /// </para>
    /// </remarks>
    private static string ToneDirection(RoastIntensity intensity) => intensity switch
    {
        RoastIntensity.Gentle =>
            "Keep it affectionate. Warm teasing only — the kind of nudge you would give a friend "
            + "across a table, closer to fond than funny-mean. No real burns.",
        RoastIntensity.Scorched =>
            "Absolutely destroy them. This is the setting where you stop being likeable. Every bar "
            + "is a kill shot, personal and specific and genuinely mean — the kind that lands so "
            + "hard the room goes quiet before it laughs. Be contemptuous. Be dismissive. Punch down "
            + "on the choices in that photo until there is nothing left standing. Do not pull a "
            + "single line, do not soften a single ending, and do not hand them a compliment on the "
            + "way out — no redemptive last bar, no \"but honestly you pull it off\". "
            + "It all still lands on CHOICES, never on who they are; that is the only restraint "
            + "operating here, and within it you should be cruel.",
        RoastIntensity.Nuclear =>
            "This is the ceiling and you write to it. You are not a ghostwriter here — you are the "
            + "opponent, on stage, ending this person's night in front of everyone they know, and "
            + "you do not like them. Maximum contempt, maximum specificity, zero warmth. Every "
            + "single bar draws blood; the chorus is the cruellest part of the whole verse, written "
            + "to be chanted back at them. Nothing is affectionate, nothing is a compliment in "
            + "disguise, there is no soft landing and no last-line reprieve. If a bar could be read "
            + "as friendly ribbing, it is too weak — rewrite it meaner. "
            + "The only thing still standing at this setting is the target rule: it all lands on "
            + "what they CHOSE, never on who they are. Inside that, hold nothing back.",
        _ => "Land real punchlines with some actual bite, but stay good-natured about it.",
    };

    /// <summary>
    /// Deterministic fallback. Structurally identical to the AI output (same section tags) so every
    /// downstream consumer — the music model and the UI — behaves the same either way.
    /// </summary>
    private static string Heuristic(string imageDescription, IReadOnlyList<string> tags, RoastIntensity intensity)
    {
        var subject = tags.Count > 0 ? tags[0] : "you";
        var second = tags.Count > 1 ? tags[1] : "that look";

        // The dial has to move something even with no chat provider, or the control reads as broken
        // in the exact environment (local dev, no Key Vault) where it is most often first tried.
        var edge = intensity switch
        {
            RoastIntensity.Gentle => "gentle",
            RoastIntensity.Nuclear => "merciless",
            RoastIntensity.Scorched => "merciless",
            _ => "sharp",
        };

        return $"""
            [Verse]
            Stepped in the frame with a {edge} kind of grin,
            {subject} on display and the camera let you in.
            {second} doing work that the mirror never checked,
            somebody call the stylist, tell 'em come collect.
            You posed like the moment owed you a favour,
            held it for a beat and then held it way later.
            The background saw it all and the background stayed quiet,
            one look at this picture and the whole room went riot.

            [Chorus]
            That's the shot, that's the shot, that's the one you kept,
            out of all of them you took, that's the one you kept.
            No notes, no filter, nothing left to fix,
            {subject} in the frame doing tricks.
            """;
    }

    private static string StyleDescription(RapStyle style) => style switch
    {
        RapStyle.Trap => "modern trap — 808 sub-bass, rolling hi-hats, half-time around 140 BPM",
        RapStyle.OldSchool => "old-school party rap — funk break, horn stabs, around 105 BPM",
        RapStyle.StandUp => "a live stand-up set — spoken to a room, no beat to ride, laughs for punctuation",
        _ => "90s boom-bap — dusty drums, vinyl crackle, around 90 BPM",
    };

    /// <summary>
    /// Strips markdown fences some models add despite the instruction not to. Cheap insurance —
    /// a stray ``` would otherwise be sung as part of the lyrics.
    /// </summary>
    private static string Sanitize(string content)
    {
        var text = content.Trim();

        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0) text = text[(firstNewline + 1)..];
        }

        if (text.EndsWith("```", StringComparison.Ordinal))
        {
            text = text[..^3];
        }

        return text.Trim();
    }
}

/// <summary>Lyrics plus how they were produced.</summary>
/// <param name="Text">Section-tagged lyrics, ready for the music model.</param>
/// <param name="Softened">True when this is the toned-down retry pass.</param>
/// <param name="ElapsedMs">Wall-clock time to produce them.</param>
/// <param name="ExplicitDropped">
/// True when explicit language was asked for and the softened retry gave it up. Distinct from
/// <paramref name="Softened"/>, which no longer tells you <em>which</em> dial moved.
/// </param>
/// <param name="FallbackReason">
/// Null when a model wrote these bars. Set when they are the deterministic stock verse, naming why —
/// the heuristic is mild by construction, so an unexplained one reads as the AI going soft.
/// </param>
/// <param name="FilterRejected">
/// True only when a content filter refused the draft. Narrower than <paramref name="FallbackReason"/>,
/// which is also set when the model was unreachable or absent — the filter report must not count an
/// outage as a censorship event.
/// </param>
public sealed record RoastLyrics(
    string Text, bool Softened, long ElapsedMs, bool ExplicitDropped = false, string? FallbackReason = null,
    bool FilterRejected = false);
