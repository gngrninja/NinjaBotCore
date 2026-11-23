#nullable enable

namespace NinjaBotCore.Models.Wow.Forever
{
    /// <summary>Where Wowhead keeps Forever's data. Wowhead uses Blizzard's ids, so a link needs nothing but the id.</summary>
    public static class ForeverWowhead
    {
        public const string Site = "https://www.wowhead.com/forever";

        // Checked 2026-11-21: items from before Forever and items new in it both have a page here.
        // An id Wowhead doesn't know gets its "not found" page.
        public static string? Item(int? id) => id is > 0 ? $"{Site}/item={id}" : null;

        public static string? ItemSet(int? id) => id is > 0 ? $"{Site}/item-set={id}" : null;
    }
}
