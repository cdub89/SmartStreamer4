using CommunityToolkit.Mvvm.ComponentModel;

namespace SDRIQStreamer.App;

/// <summary>
/// One editable row of the Digital Config tab's per-slice binding list: the
/// slice's DAX RX audio channel and CAT / UDP ports, pre-set to defaults and
/// editable by the operator (issue #28). Edits raise <see cref="ObservableObject.PropertyChanged"/>,
/// which the owning ViewModel uses to persist the change.
/// </summary>
public partial class DigitalSliceConfigViewModel : ObservableObject
{
    public DigitalSliceConfigViewModel(string sliceLetter, int daxRxChannel, int catPort, int udpPort)
    {
        SliceLetter = sliceLetter;
        _daxRxChannel = daxRxChannel;
        _catPort = catPort;
        _udpPort = udpPort;
    }

    public string SliceLetter { get; }
    public string SliceLabel => $"Slice {SliceLetter}";

    [ObservableProperty]
    private int _daxRxChannel;

    [ObservableProperty]
    private int _catPort;

    [ObservableProperty]
    private int _udpPort;

    public DigitalSliceBinding ToBinding() => new()
    {
        SliceLetter = SliceLetter,
        DaxRxChannel = DaxRxChannel,
        CatPort = CatPort,
        UdpPort = UdpPort,
    };
}
