namespace DnfItemChecker.Core.Stats;

/// <summary>
/// Default (부위 × 레어리티) → 최상급 100% values, generated from the Neople item catalog
/// (tools/gen_seed.py). Used to seed the JSON table on first run; editable thereafter via tab4.
/// 레어 has no 115 catalog data, so it is seeded with 0 placeholders for manual entry.
/// </summary>
internal static class StatTableSeed
{
    public static Dictionary<string, Dictionary<string, StatLine>> Build()
    {
        var table = new Dictionary<string, Dictionary<string, StatLine>>(StringComparer.Ordinal);

        void Add(string slot, string rarity, int str, int @int, int vit, int spr)
        {
            if (!table.TryGetValue(slot, out var byRarity))
                table[slot] = byRarity = new Dictionary<string, StatLine>(StringComparer.Ordinal);
            byRarity[rarity] = new StatLine(str, @int, vit, spr);
        }

        // 레어: not present in the 115 catalog (sub-유니크 farming tier). Placeholder 0 rows that the
        // user fills in tab4; the comparison treats 0 as "미입력".
        foreach (var slot in EquipmentSlots.All)
            Add(slot, RarityPalette.Rare, 0, 0, 0, 0);

        Add("상의", "유니크", 142, 142, 142, 142);
        Add("상의", "레전더리", 143, 143, 143, 143);
        Add("상의", "에픽", 144, 144, 144, 144);
        Add("하의", "유니크", 142, 142, 142, 142);
        Add("하의", "레전더리", 143, 143, 143, 143);
        Add("하의", "에픽", 144, 144, 144, 144);
        Add("머리어깨", "유니크", 133, 133, 133, 133);
        Add("머리어깨", "레전더리", 134, 134, 134, 134);
        Add("머리어깨", "에픽", 135, 135, 135, 135);
        Add("벨트", "유니크", 125, 125, 125, 125);
        Add("벨트", "레전더리", 125, 125, 125, 125);
        Add("벨트", "에픽", 127, 127, 127, 127);
        Add("신발", "유니크", 125, 125, 125, 125);
        Add("신발", "레전더리", 125, 125, 125, 125);
        Add("신발", "에픽", 127, 127, 127, 127);
        Add("보조장비", "유니크", 150, 150, 150, 150);
        Add("보조장비", "레전더리", 151, 151, 151, 151);
        Add("보조장비", "에픽", 152, 152, 152, 152);
        Add("마법석", "유니크", 174, 174, 174, 174);
        Add("마법석", "레전더리", 176, 176, 176, 176);
        Add("마법석", "에픽", 178, 178, 178, 178);
        Add("귀걸이", "유니크", 174, 174, 174, 174);
        Add("귀걸이", "레전더리", 226, 226, 226, 226);
        Add("귀걸이", "에픽", 278, 278, 278, 278);
        Add("목걸이", "유니크", 100, 151, 100, 226);
        Add("목걸이", "레전더리", 100, 152, 100, 229);
        Add("목걸이", "에픽", 100, 153, 100, 232);
        Add("목걸이", "태초", 100, 154, 100, 235);
        Add("팔찌", "유니크", 151, 100, 226, 100);
        Add("팔찌", "레전더리", 152, 100, 229, 100);
        Add("팔찌", "에픽", 153, 100, 232, 100);
        Add("팔찌", "태초", 154, 100, 235, 100);
        Add("반지", "유니크", 175, 175, 100, 100);
        Add("반지", "레전더리", 177, 177, 100, 100);
        Add("반지", "에픽", 179, 179, 100, 100);
        Add("반지", "태초", 180, 180, 100, 100);

        return table;
    }
}
