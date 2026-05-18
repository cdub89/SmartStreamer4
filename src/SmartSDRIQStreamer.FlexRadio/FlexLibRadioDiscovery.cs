using System;
using System.Collections.Concurrent;
using System.Linq;
using Flex.Smoothlake.FlexLib;

namespace SDRIQStreamer.FlexRadio;

/// <summary>
/// Implements <see cref="IRadioDiscovery"/> using FlexLib's <see cref="API"/>.
/// Wraps the static FlexLib events and translates <see cref="Radio"/> objects into
/// <see cref="DiscoveredRadio"/> records so FlexLib types never leak to the rest of the app.
/// </summary>
public sealed class FlexLibRadioDiscovery : IRadioDiscovery
{
    private readonly ConcurrentDictionary<string, DiscoveredRadio> _radios = new();

    public IReadOnlyList<DiscoveredRadio> DiscoveredRadios => _radios.Values.ToList();

    public event Action<DiscoveredRadio>? RadioAdded;
    public event Action<DiscoveredRadio>? RadioRemoved;

    public void Start()
    {
        API.ProgramName = "SmartStreamer4";
        API.IsGUI = false;

        API.RadioAdded += OnFlexRadioAdded;
        API.RadioRemoved += OnFlexRadioRemoved;

        API.Init();
    }

    public void Stop()
    {
        API.RadioAdded -= OnFlexRadioAdded;
        API.RadioRemoved -= OnFlexRadioRemoved;

        API.CloseSession();
        _radios.Clear();
    }

    private void OnFlexRadioAdded(Radio radio)
    {
        var discovered = ToDiscoveredRadio(radio);
        _radios[radio.Serial] = discovered;
        RadioAdded?.Invoke(discovered);
    }

    private void OnFlexRadioRemoved(Radio radio)
    {
        if (_radios.TryRemove(radio.Serial, out var discovered))
            RadioRemoved?.Invoke(discovered);
    }

    private static DiscoveredRadio ToDiscoveredRadio(Radio r) =>
        new(
            Serial:    r.Serial   ?? string.Empty,
            Model:     r.Model    ?? string.Empty,
            Nickname:  r.Nickname ?? string.Empty,
            Callsign:  r.Callsign ?? string.Empty,
            IP:        r.IP,
            Status:    r.Status   ?? string.Empty,
            Stations:  ResolveStations(r));

    private static IReadOnlyList<string> ResolveStations(Radio radio)
    {
        if (radio.GuiClients is null || radio.GuiClients.Count == 0)
            return [];

        return radio.GuiClients
            .Select(c => c.Station?.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();
    }
}
