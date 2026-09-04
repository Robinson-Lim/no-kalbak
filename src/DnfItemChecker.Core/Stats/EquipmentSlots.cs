namespace DnfItemChecker.Core.Stats;

/// <summary>
/// The 11 equipment slots the app tracks (weapon excluded by design). Accessories
/// (목걸이/팔찌/반지) additionally reach the 태초 rarity tier.
/// </summary>
public static class EquipmentSlots
{
    public const string Top = "상의";
    public const string Bottom = "하의";
    public const string Shoulder = "머리어깨";
    public const string Belt = "벨트";
    public const string Shoes = "신발";
    public const string Support = "보조장비";
    public const string Magicstone = "마법석";
    public const string Earring = "귀걸이";
    public const string Necklace = "목걸이";
    public const string Bracelet = "팔찌";
    public const string Ring = "반지";

    /// <summary>All slots in display order (방어구 → 특수장비 → 악세서리).</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Top, Bottom, Shoulder, Belt, Shoes,
        Support, Magicstone, Earring,
        Necklace, Bracelet, Ring,
    };
    /// <summary>
    /// Returns true for API rows that are not tracked equipment: the weapon and title rows.
    /// The API has used both slot ids and localized names for these rows across responses.
    /// </summary>
    public static bool IsWeaponOrTitle(
        string? slotId, string? slotName, string? itemType, string? itemTypeDetail)
        => string.Equals(slotId, "TITLE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(slotId, "WEAPON", StringComparison.OrdinalIgnoreCase)
            || string.Equals(slotName, "칭호", StringComparison.Ordinal)
            || string.Equals(slotName, "무기", StringComparison.Ordinal)
            || string.Equals(itemType, "무기", StringComparison.Ordinal)
            || string.Equals(itemTypeDetail, "무기", StringComparison.Ordinal);

    /// <summary>Slots that have a 태초 tier.</summary>
    public static readonly IReadOnlyList<string> Accessories = new[] { Necklace, Bracelet, Ring };

    public static bool IsAccessory(string slot) => slot is Necklace or Bracelet or Ring;
}
