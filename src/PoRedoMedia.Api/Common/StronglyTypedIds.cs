// Strongly-typed identifiers, so that transposed arguments such as
//     repository.GetAsync(mediaId, userId)   // wrong way round
// fail to compile instead of silently returning nothing.
//
// A guid id serialises as the bare GUID string and ToString() returns the raw GUID, so table keys
// and wire payloads are identical to a plain Guid. Do not "simplify" either.
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoRedoMedia.Api.Common;

public interface IGuidId<TSelf> where TSelf : struct, IGuidId<TSelf>
{
    Guid Value { get; }
    static abstract TSelf From(Guid value);
}

public sealed class GuidIdJsonConverter<T> : JsonConverter<T> where T : struct, IGuidId<T>
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => T.From(reader.GetGuid());

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
}

/// <summary>Identifies one stored image, video or audio item.</summary>
[JsonConverter(typeof(GuidIdJsonConverter<MediaId>))]
public readonly record struct MediaId(Guid Value) : IGuidId<MediaId>, IParsable<MediaId>
{
    public static MediaId New() => new(Guid.NewGuid());
    public static readonly MediaId Empty = new(Guid.Empty);
    public static MediaId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
    public static MediaId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s));

    public static bool TryParse(string? s, IFormatProvider? provider, out MediaId result)
    {
        var ok = Guid.TryParse(s, out var guid);
        result = new(guid);
        return ok;
    }
}

/// <summary>Identifies one run: a set of functions applied to one source.</summary>
[JsonConverter(typeof(GuidIdJsonConverter<RunId>))]
public readonly record struct RunId(Guid Value) : IGuidId<RunId>, IParsable<RunId>
{
    public static RunId New() => new(Guid.NewGuid());
    public static readonly RunId Empty = new(Guid.Empty);
    public static RunId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
    public static RunId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s));

    public static bool TryParse(string? s, IFormatProvider? provider, out RunId result)
    {
        var ok = Guid.TryParse(s, out var guid);
        result = new(guid);
        return ok;
    }
}

/// <summary>Identifies a meme sound in the library.</summary>
[JsonConverter(typeof(GuidIdJsonConverter<SoundId>))]
public readonly record struct SoundId(Guid Value) : IGuidId<SoundId>, IParsable<SoundId>
{
    public static SoundId New() => new(Guid.NewGuid());
    public static readonly SoundId Empty = new(Guid.Empty);
    public static SoundId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
    public static SoundId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s));

    public static bool TryParse(string? s, IFormatProvider? provider, out SoundId result)
    {
        var ok = Guid.TryParse(s, out var guid);
        result = new(guid);
        return ok;
    }
}

/// <summary>Identifies one cue in a director script.</summary>
[JsonConverter(typeof(GuidIdJsonConverter<EntryId>))]
public readonly record struct EntryId(Guid Value) : IGuidId<EntryId>, IParsable<EntryId>
{
    public static EntryId New() => new(Guid.NewGuid());
    public static readonly EntryId Empty = new(Guid.Empty);
    public static EntryId From(Guid value) => new(value);
    public override string ToString() => Value.ToString();
    public static EntryId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s));

    public static bool TryParse(string? s, IFormatProvider? provider, out EntryId result)
    {
        var ok = Guid.TryParse(s, out var guid);
        result = new(guid);
        return ok;
    }
}

/// <summary>
/// The signed-in user, as the identity provider names them (the NameIdentifier claim).
/// </summary>
public readonly record struct UserId(string Value)
{
    /// <summary>The value as a Table Storage partition key, which may not contain / \ # or ?.</summary>
    public string Key => Uri.EscapeDataString(Value);

    public override string ToString() => Value;

    public static UserId From(ClaimsPrincipal user) =>
        new(user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("The signed-in user has no NameIdentifier claim."));
}
