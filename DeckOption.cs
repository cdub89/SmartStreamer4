using CommunityToolkit.Mvvm.ComponentModel;

namespace SDRIQStreamer.App;

/// <summary>
/// One button on the SmartDeck grid: the label it shows, and whether it is the
/// value the radio currently holds (issue #59, phase 2 layout pass).
/// </summary>
/// <remarks>
/// Exists so the view can bind a "lit" class straight off the item. Comparing
/// each item against a current-value property in XAML instead would need a
/// multi-value converter inside every item template; keeping the comparison in
/// the ViewModel keeps the view declarative and the lit state unit-testable.
/// </remarks>
public partial class DeckOption(string label) : ObservableObject
{
    /// <summary>Button face, and for antennas and bands the value written to the radio.</summary>
    public string Label { get; } = label;

    [ObservableProperty]
    private bool _isCurrent;
}
