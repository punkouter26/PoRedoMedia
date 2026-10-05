using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Maui.Controls;

namespace PoRedoMedia.Mobile.ViewModels;

/// <summary>
/// One slot on the Bulk board. Prefilled as pending and filled in as the NDJSON stream lands —
/// slots complete out of order, so the index (not arrival order) drives placement.
/// </summary>
public partial class BulkItemViewModel : ObservableObject
{
    public BulkItemViewModel(int index) => Index = index;

    public int Index { get; }

    public string Label => $"#{Index + 1}";

    [ObservableProperty]
    private ImageSource? _image;

    [ObservableProperty]
    private bool _isFilled;

    [ObservableProperty]
    private bool _isFailed;

    [ObservableProperty]
    private string _statusText = "Waiting…";

    /// <summary>Raw bytes for gallery saving — not rendered, so not observable.</summary>
    public byte[]? Bytes { get; set; }

    public string ContentType { get; set; } = "image/jpeg";
}
