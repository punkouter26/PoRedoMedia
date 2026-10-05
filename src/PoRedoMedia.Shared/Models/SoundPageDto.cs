namespace PoRedoMedia.Shared.Models;

/// <summary>One page of <c>GET /api/memelibrary/sounds</c>, plus the size of the whole filtered library.</summary>
public sealed record SoundPageDto(int TotalCount, SoundAssetDto[] Sounds);
