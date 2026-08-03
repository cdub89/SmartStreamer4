using SDRIQStreamer.App;

namespace SmartSDRIQStreamer.App.Tests;

/// <summary>
/// Per-band frequency memory behind the SmartDeck band buttons (issue #59
/// phase 2b). Pure logic with no radio dependency, which is why band change is
/// app-side: it is a write to Slice.Freq, not a FlexLib verb.
/// </summary>
public class BandMemoryTests
{
    [Fact]
    public void Offers_ten_hf_bands_in_button_order()
    {
        Assert.Equal(
            ["160m", "80m", "60m", "40m", "30m", "20m", "17m", "15m", "12m", "10m"],
            BandMemory.Bands);
    }

    [Fact]
    public void An_unvisited_band_resolves_to_its_default()
    {
        var memory = new BandMemory();

        Assert.Equal(14.050, memory.Resolve("20m"));
        Assert.Equal(7.055, memory.Resolve("40m"));
    }

    [Fact]
    public void A_band_smartdeck_does_not_offer_resolves_to_absent()
    {
        // 6m and 2m are real bands HamBands knows; SmartDeck offers no button.
        var memory = new BandMemory();

        Assert.Null(memory.Resolve("6m"));
        Assert.Null(memory.Resolve("2m"));
    }

    [Fact]
    public void Leaving_a_band_remembers_where_the_operator_was()
    {
        var memory = new BandMemory();

        memory.Remember(14.031_5);

        Assert.Equal(14.031_5, memory.Resolve("20m"));
    }

    [Fact]
    public void Remembering_overwrites_the_default_and_the_previous_value()
    {
        var memory = new BandMemory();

        memory.Remember(14.031_5);
        memory.Remember(14.200);

        Assert.Equal(14.200, memory.Resolve("20m"));
    }

    [Fact]
    public void A_frequency_outside_every_offered_band_is_not_remembered()
    {
        // A slice parked on WWV at 5 MHz is not a band worth returning to, and
        // must not be stored under an empty key.
        var memory = new BandMemory();

        memory.Remember(5.000);

        Assert.Equal(14.050, memory.Resolve("20m"));
        Assert.Null(memory.Resolve(""));
    }

    [Fact]
    public void A_frequency_in_a_known_but_unoffered_band_is_not_remembered()
    {
        var memory = new BandMemory();

        memory.Remember(50.125); // 6m: HamBands knows it, SmartDeck does not offer it.

        Assert.Null(memory.Resolve("6m"));
    }

    [Fact]
    public void Switching_stores_the_departing_band_and_returns_the_entered_one()
    {
        var memory = new BandMemory();

        // Sitting on 20m at 14.0315, press 40m.
        var target = memory.SwitchTo("40m", currentFreqMhz: 14.031_5);

        Assert.Equal(7.055, target);            // 40m default, never visited
        Assert.Equal(14.031_5, memory.Resolve("20m")); // where we left 20m
    }

    [Fact]
    public void Switching_back_returns_to_where_the_operator_left_off()
    {
        var memory = new BandMemory();

        memory.SwitchTo("40m", currentFreqMhz: 14.031_5);
        var backTo20 = memory.SwitchTo("20m", currentFreqMhz: 7.118);

        Assert.Equal(14.031_5, backTo20);
        Assert.Equal(7.118, memory.Resolve("40m"));
    }

    [Fact]
    public void Switching_to_an_unoffered_band_returns_absent_but_still_records_the_departure()
    {
        var memory = new BandMemory();

        var target = memory.SwitchTo("6m", currentFreqMhz: 14.031_5);

        Assert.Null(target);
        Assert.Equal(14.031_5, memory.Resolve("20m"));
    }

    [Fact]
    public void A_supplied_store_is_written_through_so_memory_can_persist()
    {
        // The ViewModel hands in the settings dictionary itself, so bands the
        // operator leaves land directly in what gets saved.
        var store = new Dictionary<string, double>();
        var memory = new BandMemory(store);

        memory.Remember(14.031_5);

        Assert.Equal(14.031_5, Assert.Contains("20m", store));
    }

    [Fact]
    public void A_supplied_store_seeds_previously_persisted_frequencies()
    {
        var memory = new BandMemory(new Dictionary<string, double> { ["20m"] = 14.060 });

        Assert.Equal(14.060, memory.Resolve("20m"));
    }
}
