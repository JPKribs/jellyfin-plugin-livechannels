using System;
using System.Collections;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LiveChannels.Models;

/// <summary>
/// A channel's ordered loop plus the instant its first item airs. A rebuilt loop is spliced onto the previous
/// one: it is rotated so the item airing at the rebuild comes first, and anchored at that item's original start,
/// so a rebuild never rewrites what is on now (the guide keeps the same program, the stream does not jump). A
/// loop with no anchor is the legacy, unspliced form: free-running from the fixed epoch, or for a daypart
/// channel the chain from the configuration-save anchor. A loop may also carry a lead: the Up Next bumper that
/// opens every item's slot, so each slot on the schedule is the lead plus the item, and the item itself starts
/// the lead's length after its slot does.
/// </summary>
public sealed class ProgramLoop : IReadOnlyList<ProgramEntry>
{
    private readonly IReadOnlyList<ProgramEntry> _items;

    /// <summary>Initializes a new instance of the <see cref="ProgramLoop"/> class.</summary>
    /// <param name="items">The ordered items.</param>
    /// <param name="anchorUtc">When the first item airs, or <c>null</c> for the legacy unspliced schedule.</param>
    /// <param name="leadTicks">The length of the bumper that opens every slot, in ticks (0 for none).</param>
    public ProgramLoop(IReadOnlyList<ProgramEntry> items, DateTime? anchorUtc, long leadTicks = 0)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = items;
        AnchorUtc = anchorUtc;
        LeadTicks = Math.Max(0, leadTicks);
    }

    /// <summary>Gets the UTC instant the first item airs, or <c>null</c> for the legacy unspliced schedule.</summary>
    public DateTime? AnchorUtc { get; }

    /// <summary>Gets the length of the Up Next bumper that opens every slot, in ticks (0 when the channel shows none).</summary>
    public long LeadTicks { get; }

    /// <inheritdoc />
    public int Count => _items.Count;

    /// <inheritdoc />
    public ProgramEntry this[int index] => _items[index];

    /// <summary>The splice anchor of a loop, or <c>null</c> when it is not a spliced <see cref="ProgramLoop"/>.</summary>
    /// <param name="programs">The loop.</param>
    /// <returns>The anchor, or <c>null</c>.</returns>
    public static DateTime? AnchorOf(IReadOnlyList<ProgramEntry> programs)
        => programs is ProgramLoop { AnchorUtc: { } anchor } ? anchor : null;

    /// <summary>The bumper lead of a loop in ticks, or 0 when it is not a <see cref="ProgramLoop"/> or has none.</summary>
    /// <param name="programs">The loop.</param>
    /// <returns>The lead in ticks.</returns>
    public static long LeadOf(IReadOnlyList<ProgramEntry> programs)
        => programs is ProgramLoop loop ? loop.LeadTicks : 0;

    /// <summary>The length of one item's slot on the schedule: the loop's lead plus the item itself.</summary>
    /// <param name="programs">The loop.</param>
    /// <param name="index">The item.</param>
    /// <returns>The slot length in ticks.</returns>
    public static long SlotTicks(IReadOnlyList<ProgramEntry> programs, int index)
        => programs[index].DurationTicks + LeadOf(programs);

    /// <inheritdoc />
    public IEnumerator<ProgramEntry> GetEnumerator() => _items.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
