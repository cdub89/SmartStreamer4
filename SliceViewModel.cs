using CommunityToolkit.Mvvm.ComponentModel;
using SDRIQStreamer.FlexRadio;

namespace SDRIQStreamer.App;

/// <summary>
/// Presentation wrapper around <see cref="SliceInfo"/>.
/// </summary>
public partial class SliceViewModel : ObservableObject
{
    public SliceInfo Slice { get; private set; }
    public int DaxIqChannel { get; private set; }

    public string DisplayLabel  => Slice.DisplayLabel;
    public string ClientStation => Slice.ClientStation;
    public bool HasDaxIqChannel => DaxIqChannel > 0;

    // Clean, summarized fields for the Launch-page slice list (issue #28):
    // "Slice X | band (freq) | DAX IQ n | DAX RX n".
    public string SliceLabel => $"Slice {Slice.Letter}";
    public string BandFreqDisplay => HamBands.BandFreq(Slice.FreqMHz);

    /// <summary>DAX-IQ (panadapter) channel label, e.g. "DAX IQ 1" (empty when none).</summary>
    public string DaxIqDisplay => HasDaxIqChannel ? $"DAX IQ {DaxIqChannel}" : string.Empty;

    /// <summary>DAX RX audio channel label, e.g. "DAX RX 1" (empty when none).</summary>
    public string DaxRxDisplay => Slice.DaxAudioChannel > 0 ? $"DAX RX {Slice.DaxAudioChannel}" : string.Empty;

    [ObservableProperty]
    private bool _isSkimmerRunning;

    public SliceViewModel(SliceInfo slice, int daxIqChannel)
    {
        Slice = slice;
        DaxIqChannel = daxIqChannel;
    }

    public void Update(SliceInfo updated)
    {
        Slice = updated;
        OnPropertyChanged(nameof(DisplayLabel));
        OnPropertyChanged(nameof(BandFreqDisplay));
        OnPropertyChanged(nameof(DaxRxDisplay));
    }

    public void UpdateDaxIqChannel(int daxIqChannel)
    {
        if (DaxIqChannel == daxIqChannel)
            return;

        DaxIqChannel = daxIqChannel;
        OnPropertyChanged(nameof(HasDaxIqChannel));
        OnPropertyChanged(nameof(DaxIqDisplay));
    }
}
