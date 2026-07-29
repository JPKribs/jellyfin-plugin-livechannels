namespace Jellyfin.Plugin.LiveChannels.Utilities;

/// <summary>
/// The artwork paths one library item carries, as far as the guide cares. A missing image is <c>null</c>.
/// </summary>
/// <param name="Primary">The primary image (a poster for a movie or series, a landscape still for an episode).</param>
/// <param name="Thumb">The landscape thumb.</param>
/// <param name="Backdrop">The backdrop.</param>
/// <param name="Logo">The clear logo.</param>
public readonly record struct ArtworkSet(string? Primary, string? Thumb, string? Backdrop, string? Logo);

/// <summary>
/// Chooses which of an item's (and its series') images fill each guide artwork slot, so a program carries both a
/// portrait poster and a landscape image and every client can render whichever shape it lays out. Pure, so the
/// selection rules can be unit tested without a library.
/// </summary>
public static class GuideImages
{
    /// <summary>
    /// Fills the four guide artwork slots from an item's own images and its parent series' images.
    /// </summary>
    /// <param name="own">The item's own artwork.</param>
    /// <param name="parent">The parent series' artwork (all <c>null</c> for a standalone item).</param>
    /// <param name="ownPrimaryIsLandscape">Whether the item's primary image is a landscape still rather than a poster, as it is for episodes and music videos.</param>
    /// <returns>The artwork for each slot: portrait primary, landscape thumb, backdrop, and logo.</returns>
    public static ArtworkSet Select(ArtworkSet own, ArtworkSet parent, bool ownPrimaryIsLandscape)
    {
        // Portrait: an episode's own primary is a landscape still, so its poster is the series poster. Falling
        // back to the item's own primary keeps something in the slot rather than leaving it empty.
        var primary = ownPrimaryIsLandscape
            ? parent.Primary ?? own.Primary
            : own.Primary ?? parent.Primary;

        // Landscape: a real thumb first, then the episode still (already landscape), then the series thumb, then
        // either backdrop, which crops acceptably into a wide slot.
        var thumb = own.Thumb
            ?? (ownPrimaryIsLandscape ? own.Primary : null)
            ?? parent.Thumb
            ?? own.Backdrop
            ?? parent.Backdrop;

        return new ArtworkSet(
            primary,
            thumb,
            own.Backdrop ?? parent.Backdrop,
            own.Logo ?? parent.Logo);
    }
}
