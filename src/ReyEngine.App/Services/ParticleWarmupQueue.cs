using System;
using System.Collections.Generic;
using System.Diagnostics;
using ReyEngine.Rendering.Vfx;

namespace ReyEngine.App.Services;

/// <summary>
/// M694: the warm-up a particle system gets on camera entry, paid a few systems per frame instead of
/// all at once.
///
/// <para>Both viewports pre-warm a system to steady state when it enters the camera gate (M536/M595) -
/// up to 150 simulator steps per system, 0.5 ms on average and 7 ms for the worst on the jade map.
/// They did it inline, for every system that entered in the same frame; a camera swing that brings
/// forty systems into view stalled that frame by 20-30 ms, which is the "everything loads" hitch. This
/// queue takes the entering systems and warms them in order under a time budget per frame, at least
/// one per call so a queue always drains; a system is active only once it is warm, so the effects fill
/// in over a handful of frames rather than the frame stopping.</para>
/// </summary>
public sealed class ParticleWarmupQueue
{
    private readonly Queue<VfxParticleSimulator> _pending = new();
    private readonly HashSet<VfxParticleSimulator> _pendingSet = new(ReferenceEqualityComparer.Instance);
    private readonly Action<VfxParticleSimulator> _warm;
    private readonly Stopwatch _clock = new();

    /// <param name="warm">What warming a system means; the default is the simulator's own pre-warm to its
    /// fill duration. A test counts calls instead.</param>
    public ParticleWarmupQueue(Action<VfxParticleSimulator>? warm = null)
        => _warm = warm ?? (sim => sim.PreWarm(sim.FillDuration));

    /// <summary>Milliseconds a single <see cref="Pump"/> may spend; at least one system is warmed per call
    /// regardless, so a budget below the cheapest system still drains the queue one per frame.</summary>
    public double BudgetMs { get; set; } = 3.0;

    public int Pending => _pending.Count;
    public double LastPumpMs { get; private set; }
    public int LastPumped { get; private set; }

    public bool IsPending(VfxParticleSimulator sim) => _pendingSet.Contains(sim);

    /// <summary>Queue a system that just entered the gate. It is reset now, so a system that waits a few
    /// frames starts its warm-up from a clean state; queued twice is queued once.</summary>
    public void Enqueue(VfxParticleSimulator sim)
    {
        if (!_pendingSet.Add(sim)) return;
        sim.Reset();
        _pending.Enqueue(sim);
    }

    /// <summary>Forget a queued system - it left the gate before its turn.</summary>
    public void Remove(VfxParticleSimulator sim)
    {
        if (!_pendingSet.Remove(sim)) return;
        // rebuild the queue without it; queues are short, this is rare
        var keep = new List<VfxParticleSimulator>(_pending.Count);
        while (_pending.Count > 0) { var s = _pending.Dequeue(); if (!ReferenceEquals(s, sim)) keep.Add(s); }
        foreach (var s in keep) _pending.Enqueue(s);
    }

    public void Clear()
    {
        _pending.Clear();
        _pendingSet.Clear();
    }

    /// <summary>Warm queued systems until the budget is spent (at least one), appending each warmed system
    /// to <paramref name="ready"/>. Returns how many were warmed this call.</summary>
    public int Pump(List<VfxParticleSimulator> ready)
    {
        LastPumped = 0;
        LastPumpMs = 0;
        if (_pending.Count == 0) return 0;
        _clock.Restart();
        while (_pending.Count > 0 && (LastPumped == 0 || _clock.Elapsed.TotalMilliseconds < BudgetMs))
        {
            var sim = _pending.Dequeue();
            _pendingSet.Remove(sim);
            try { _warm(sim); }
            catch { /* a system that will not warm still becomes active; it simply starts empty */ }
            ready.Add(sim);
            LastPumped++;
        }
        LastPumpMs = _clock.Elapsed.TotalMilliseconds;
        return LastPumped;
    }
}
