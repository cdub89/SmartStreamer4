using SDRIQStreamer.App;
using SDRIQStreamer.FlexRadio;

namespace SmartSDRIQStreamer.App.Tests;

/// <summary>
/// Per-band state memory behind the SmartDeck band buttons (issue #59 phase 2b,
/// widened past frequency to mode, both antennas and AGC-T on 2026-08-03). Pure
/// logic with no radio dependency, which is why band change is app-side: it is
/// a write to Slice.Freq, not a FlexLib verb.
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

    // ── Frequency ────────────────────────────────────────────────────────────

    [Fact]
    public void An_unvisited_band_resolves_to_its_default()
    {
        var memory = new BandMemory();

        Assert.Equal(14.050, memory.Resolve("20m")?.FreqMhz);
        Assert.Equal(7.055, memory.Resolve("40m")?.FreqMhz);
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

        memory.Remember(new BandState(14.031_5));

        Assert.Equal(14.031_5, memory.Resolve("20m")?.FreqMhz);
    }

    [Fact]
    public void Remembering_overwrites_the_default_and_the_previous_value()
    {
        var memory = new BandMemory();

        memory.Remember(new BandState(14.031_5));
        memory.Remember(new BandState(14.200));

        Assert.Equal(14.200, memory.Resolve("20m")?.FreqMhz);
    }

    [Fact]
    public void A_frequency_outside_every_offered_band_is_not_remembered()
    {
        // A slice parked on WWV at 5 MHz is not a band worth returning to, and
        // must not be stored under an empty key.
        var memory = new BandMemory();

        memory.Remember(new BandState(5.000));

        Assert.Equal(14.050, memory.Resolve("20m")?.FreqMhz);
        Assert.Null(memory.Resolve(""));
    }

    [Fact]
    public void A_frequency_in_a_known_but_unoffered_band_is_not_remembered()
    {
        var memory = new BandMemory();

        // 6m: HamBands knows it, SmartDeck does not offer it.
        memory.Remember(new BandState(50.125));

        Assert.Null(memory.Resolve("6m"));
    }

    [Fact]
    public void Switching_stores_the_departing_band_and_returns_the_entered_one()
    {
        var memory = new BandMemory();

        // Sitting on 20m at 14.0315, press 40m.
        var target = memory.SwitchTo("40m", new BandState(14.031_5));

        Assert.Equal(7.055, target?.FreqMhz);                 // 40m default, never visited
        Assert.Equal(14.031_5, memory.Resolve("20m")?.FreqMhz); // where we left 20m
    }

    [Fact]
    public void Switching_back_returns_to_where_the_operator_left_off()
    {
        var memory = new BandMemory();

        memory.SwitchTo("40m", new BandState(14.031_5));
        var backTo20 = memory.SwitchTo("20m", new BandState(7.118));

        Assert.Equal(14.031_5, backTo20?.FreqMhz);
        Assert.Equal(7.118, memory.Resolve("40m")?.FreqMhz);
    }

    [Fact]
    public void Switching_to_an_unoffered_band_returns_absent_but_still_records_the_departure()
    {
        var memory = new BandMemory();

        var target = memory.SwitchTo("6m", new BandState(14.031_5));

        Assert.Null(target);
        Assert.Equal(14.031_5, memory.Resolve("20m")?.FreqMhz);
    }

    // ── The rest of the remembered state ─────────────────────────────────────

    [Fact]
    public void An_unvisited_band_remembers_nothing_but_the_frequency()
    {
        // A band's first visit tunes it and changes nothing else. Guessing an
        // antenna would be a worse failure than changing nothing.
        var state = new BandMemory().Resolve("20m");

        Assert.NotNull(state);
        Assert.Null(state.Mode);
        Assert.Null(state.RxAntenna);
        Assert.Null(state.TxAntenna);
        Assert.Null(state.AgcThreshold);
    }

    [Fact]
    public void Leaving_a_band_remembers_mode_antennas_and_agc_alongside_frequency()
    {
        var memory = new BandMemory();

        memory.Remember(new BandState(14.031_5, SliceMode.Cw, "ANT2", "ANT1", 65));

        var state = memory.Resolve("20m");
        Assert.Equal(14.031_5, state?.FreqMhz);
        Assert.Equal(SliceMode.Cw, state?.Mode);
        Assert.Equal("ANT2", state?.RxAntenna);
        Assert.Equal("ANT1", state?.TxAntenna);
        Assert.Equal(65, state?.AgcThreshold);
    }

    [Fact]
    public void Each_band_keeps_its_own_state()
    {
        var memory = new BandMemory();

        memory.Remember(new BandState(14.031_5, SliceMode.Cw, "ANT2", "ANT2", 65));
        memory.Remember(new BandState(7.118, SliceMode.Lsb, "ANT1", "ANT1", 30));

        Assert.Equal(SliceMode.Cw, memory.Resolve("20m")?.Mode);
        Assert.Equal("ANT2", memory.Resolve("20m")?.RxAntenna);
        Assert.Equal(SliceMode.Lsb, memory.Resolve("40m")?.Mode);
        Assert.Equal("ANT1", memory.Resolve("40m")?.RxAntenna);
    }

    [Fact]
    public void A_mode_smartdeck_does_not_offer_is_recorded_as_absent()
    {
        // The caller passes SliceInfo.OfferedMode, which is null for DIGU and
        // friends. Storing null means the restore leaves mode alone rather than
        // needing an untyped mode write.
        var memory = new BandMemory();

        memory.Remember(new BandState(14.074, Mode: null, RxAntenna: "ANT1"));

        Assert.Null(memory.Resolve("20m")?.Mode);
        Assert.Equal("ANT1", memory.Resolve("20m")?.RxAntenna);
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    [Fact]
    public void A_supplied_store_is_written_through_so_memory_can_persist()
    {
        // The ViewModel hands in the settings dictionary itself, so bands the
        // operator leaves land directly in what gets saved.
        var store = new Dictionary<string, BandState>();
        var memory = new BandMemory(store);

        memory.Remember(new BandState(14.031_5, SliceMode.Cw, "ANT2", "ANT1", 65));

        Assert.Equal(14.031_5, Assert.Contains("20m", store).FreqMhz);
        Assert.Equal(SliceMode.Cw, Assert.Contains("20m", store).Mode);
    }

    [Fact]
    public void A_supplied_store_seeds_previously_persisted_state()
    {
        var memory = new BandMemory(
            new Dictionary<string, BandState> { ["20m"] = new(14.060, SliceMode.Usb, "RX_A", "ANT2", 40) });

        var state = memory.Resolve("20m");
        Assert.Equal(14.060, state?.FreqMhz);
        Assert.Equal(SliceMode.Usb, state?.Mode);
        Assert.Equal("RX_A", state?.RxAntenna);
        Assert.Equal("ANT2", state?.TxAntenna);
        Assert.Equal(40, state?.AgcThreshold);
    }
}
