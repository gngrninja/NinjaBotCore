namespace NinjaBotHelpers.Wago;

/// <summary>
/// Result container for wago.tools item data fetch
/// </summary>
public class WagoItemsResult
{
    /// <summary>
    /// List of parsed items from the CSV
    /// </summary>
    public List<WagoItem> Items { get; set; } = new();

    /// <summary>
    /// Total number of rows processed
    /// </summary>
    public int TotalRows { get; set; }

    /// <summary>
    /// Number of rows that failed to parse
    /// </summary>
    public int FailedRows { get; set; }
}

/// <summary>
/// Individual item data from wago.tools ItemSparse CSV
/// </summary>
public class WagoItem
{
    /// <summary>
    /// Item ID
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Display name of the item (from Display_lang column)
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Quality ID (0-7) from OverallQualityID
    /// </summary>
    public int QualityId { get; set; }

    /// <summary>
    /// Item level from ItemLevel column
    /// </summary>
    public int ItemLevel { get; set; }

    /// <summary>
    /// Required level from RequiredLevel column
    /// </summary>
    public int RequiredLevel { get; set; }

    /// <summary>
    /// Inventory type ID from InventoryType column
    /// </summary>
    public int InventoryTypeId { get; set; }

    /// <summary>
    /// Expansion ID from ExpansionID column
    /// </summary>
    public int ExpansionId { get; set; }
}

/// <summary>
/// Static mappings for wago.tools data to human-readable names
/// </summary>
public static class WagoFieldMappings
{
    /// <summary>
    /// Maps quality ID to quality name
    /// Based on WoW item quality enum
    /// </summary>
    public static readonly Dictionary<int, string> QualityNames = new()
    {
        { 0, "Poor" },
        { 1, "Common" },
        { 2, "Uncommon" },
        { 3, "Rare" },
        { 4, "Epic" },
        { 5, "Legendary" },
        { 6, "Artifact" },
        { 7, "Heirloom" }
    };

    /// <summary>
    /// Maps inventory type ID to slot name
    /// Based on WoW InventoryType enum
    /// </summary>
    public static readonly Dictionary<int, string> InventoryTypeNames = new()
    {
        { 0, "Non-equippable" },
        { 1, "Head" },
        { 2, "Neck" },
        { 3, "Shoulder" },
        { 4, "Shirt" },
        { 5, "Chest" },
        { 6, "Waist" },
        { 7, "Legs" },
        { 8, "Feet" },
        { 9, "Wrist" },
        { 10, "Hands" },
        { 11, "Finger" },
        { 12, "Trinket" },
        { 13, "One-Hand" },
        { 14, "Shield" },
        { 15, "Ranged" },
        { 16, "Back" },
        { 17, "Two-Hand" },
        { 18, "Bag" },
        { 19, "Tabard" },
        { 20, "Robe" },
        { 21, "Main Hand" },
        { 22, "Off Hand" },
        { 23, "Held In Off-Hand" },
        { 24, "Ammo" },
        { 25, "Thrown" },
        { 26, "Ranged Right" },
        { 28, "Relic" }
    };

    /// <summary>
    /// Maps expansion ID to expansion name
    /// </summary>
    public static readonly Dictionary<int, string> ExpansionNames = new()
    {
        { 0, "Classic" },
        { 1, "The Burning Crusade" },
        { 2, "Wrath of the Lich King" },
        { 3, "Cataclysm" },
        { 4, "Mists of Pandaria" },
        { 5, "Warlords of Draenor" },
        { 6, "Legion" },
        { 7, "Battle for Azeroth" },
        { 8, "Shadowlands" },
        { 9, "Dragonflight" },
        { 10, "The War Within" }
    };

    /// <summary>
    /// Get quality name from ID, with fallback
    /// </summary>
    public static string GetQualityName(int qualityId)
    {
        return QualityNames.TryGetValue(qualityId, out var name) ? name : "Unknown";
    }

    /// <summary>
    /// Get inventory type name from ID, with fallback
    /// </summary>
    public static string GetInventoryTypeName(int inventoryTypeId)
    {
        return InventoryTypeNames.TryGetValue(inventoryTypeId, out var name) ? name : null!;
    }

    /// <summary>
    /// Get expansion name from ID, with fallback
    /// </summary>
    public static string GetExpansionName(int expansionId)
    {
        return ExpansionNames.TryGetValue(expansionId, out var name) ? name : "Unknown";
    }
}
