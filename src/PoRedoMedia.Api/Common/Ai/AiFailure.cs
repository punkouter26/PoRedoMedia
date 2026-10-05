using System.ClientModel;
using Azure;

namespace PoRedoMedia.Api.Common.Ai;

/// <summary>Why an upstream AI call failed, in the terms the user-facing copy needs.</summary>
public enum AiFailureKind { Other, RateLimited, ContentFiltered, Misconfigured }

/// <summary>
/// Classifies an AI provider failure once, for every slice that turns one into a user-facing reason.
/// </summary>
/// <remarks>
/// The ImageAnalysis and RapRoast slices used to sniff <c>ex.Message</c> for "429" and "401" — which
/// also matched any message that happened to contain those digits (a request id, a byte count). The
/// status now comes from the typed exceptions the SDKs actually throw. Content filtering is the one
/// case still read from the message, because neither SDK surfaces that code as a typed member; each
/// slice keeps its own wording of the consequence.
/// </remarks>
public static class AiFailure
{
    public static AiFailureKind Classify(Exception ex)
    {
        if (IsContentFiltered(ex.Message)) return AiFailureKind.ContentFiltered;

        int? status = ex switch
        {
            ClientResultException c => c.Status,
            RequestFailedException r => r.Status,
            HttpRequestException h => (int?)h.StatusCode,
            _ => null,
        };

        return status switch
        {
            429 => AiFailureKind.RateLimited,
            // 404 is how Azure OpenAI reports a deployment name that does not exist.
            401 or 403 or 404 => AiFailureKind.Misconfigured,
            _ => AiFailureKind.Other,
        };
    }

    /// <summary>
    /// Whether a failure is a content-safety refusal. Azure OpenAI reports it as
    /// <c>HTTP 400 (content_filter)</c>, OpenAI.com as <c>content_policy_violation</c>; matching only
    /// the latter once left every Azure refusal falling through to an opaque 500.
    /// </summary>
    public static bool IsContentFiltered(string? message) =>
        message is not null
        && (message.Contains("content_filter", StringComparison.OrdinalIgnoreCase)
            || message.Contains("content_policy_violation", StringComparison.OrdinalIgnoreCase));
}
