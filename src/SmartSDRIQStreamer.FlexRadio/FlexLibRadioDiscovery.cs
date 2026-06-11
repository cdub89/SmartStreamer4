using System;
using System.Collections.Concurrent;
using System.ComponentModel;
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
    // Properties on a discovered Radio whose change must re-emit the record so the
    // connect-target / station list stays current. "GuiClients" carries the station
    // list (the reported bug); "Status" flips Available <-> InUse.
    private static readonly string[] RefreshTriggerProperties = ["GuiClients", "Status"];

    private readonly ConcurrentDictionary<string, DiscoveredRadio> _radios = new();
    // Radios we have a PropertyChanged subscription on, keyed by serial, so we can
    // unsubscribe on removal / Stop without re-querying API.RadioList.
    private readonly ConcurrentDictionary<string, Radio> _subscribed = new();
    // Serializes the publish decision (is this radio still subscribed?) with the
    // _radios write, so a PropertyChanged callback that fires concurrently with
    // OnFlexRadioRemoved/Stop cannot resurrect a just-removed radio. Events are
    // raised outside this lock.
    private readonly object _gate = new();

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

        lock (_gate)
        {
            foreach (var radio in _subscribed.Values)
                radio.PropertyChanged -= OnFlexRadioPropertyChanged;
            _subscribed.Clear();
            _radios.Clear();
        }

        API.CloseSession();
    }

    private void OnFlexRadioAdded(Radio radio)
    {
        // Bug fix 2026-06-11 (issue #45 re-validation): for an already-known radio,
        // FlexLib does NOT re-raise RadioAdded; Discovery_RadioDiscovered ->
        // RefreshRadio -> Radio.UpdateGuiClientsList mutates GuiClients in place and
        // raises RaisePropertyChanged(GuiClients) (Radio.cs). Without subscribing to
        // that, our snapshot's Stations froze at first-add, so a SmartSDR client that
        // closed and re-opened never reappeared in the connect-target list until a
        // disconnect cycled the radio. Subscribe here so GuiClients/Status changes
        // re-emit the record live. Reported by the maintainer during live re-test.
        DiscoveredRadio discovered;
        lock (_gate)
        {
            if (_subscribed.TryAdd(radio.Serial, radio))
                radio.PropertyChanged += OnFlexRadioPropertyChanged;

            discovered = ToDiscoveredRadio(radio);
            _radios[radio.Serial] = discovered;
        }
        RadioAdded?.Invoke(discovered);
    }

    private void OnFlexRadioRemoved(Radio radio)
    {
        DiscoveredRadio? removed;
        lock (_gate)
        {
            if (_subscribed.TryRemove(radio.Serial, out var tracked))
                tracked.PropertyChanged -= OnFlexRadioPropertyChanged;
            _radios.TryRemove(radio.Serial, out removed);
        }

        if (removed is not null)
            RadioRemoved?.Invoke(removed);
    }

    private void OnFlexRadioPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not Radio radio ||
            Array.IndexOf(RefreshTriggerProperties, e.PropertyName) < 0)
            return;

        DiscoveredRadio discovered;
        lock (_gate)
        {
            // Gate against an OnFlexRadioRemoved/Stop that raced this in-flight
            // callback: FlexLib raises PropertyChanged on its own threads, so the
            // radio may already be removed by the time we run. If it is no longer
            // the currently-subscribed instance, drop the update rather than
            // resurrect a dead _radios entry. The check and the _radios write are
            // atomic under _gate with respect to removal.
            if (!_subscribed.TryGetValue(radio.Serial, out var current) ||
                !ReferenceEquals(current, radio))
                return;

            // Re-emit through RadioAdded: the ViewModel's OnRadioAdded upserts
            // (replaces the existing entry in place) and rebuilds the connect
            // targets, so this is the established "added or updated" path, not a
            // duplicate add.
            discovered = ToDiscoveredRadio(radio);
            _radios[radio.Serial] = discovered;
        }
        RadioAdded?.Invoke(discovered);
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
        // Snapshot the station names under GuiClientsLockObj. FlexLib mutates the
        // backing List<GUIClient> under this same lock in UpdateGuiClientsList, and
        // our PropertyChanged handler runs right when that mutation fires, so a
        // lock-free LINQ pass here can throw "collection was modified" or read a
        // torn list. Copy to plain strings inside the lock; sort/dedupe outside.
        List<string?> raw;
        lock (radio.GuiClientsLockObj)
        {
            if (radio.GuiClients is null || radio.GuiClients.Count == 0)
                return [];

            raw = radio.GuiClients
                .Select(c => c.Station?.Trim())
                .ToList();
        }

        return raw
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();
    }
}
