using DnfItemChecker.Core.Comparison;
using DnfItemChecker.Core.Models;
using DnfItemChecker.Core.Stats;

namespace DnfItemChecker.App.ViewModels;

/// <summary>One equipped item plus its stored equipped-slot tooltip observation and 100% comparison.</summary>
public sealed class EquipmentRowViewModel
{
    public EquipmentRowViewModel(DfEquippedItem item, ComparisonResult result, MainStat stat)
    {
        SlotName = item.SlotName;
        ItemName = item.ItemName;
        Rarity = item.ItemRarity;
        GradeTier = item.ItemGradeName ?? "-";
        StatName = stat.ToKorean();
        ReferenceValue = result.ReferenceValue;
        ObservedValue = result.ObservedValue;
        Outcome = result.Outcome;
        Note = result.Note;
    }

    public string SlotName { get; }
    public string ItemName { get; }
    public string Rarity { get; }
    public string GradeTier { get; }
    public string StatName { get; }
    public int? ObservedValue { get; }
    public int? ReferenceValue { get; }
    public ComparisonOutcome Outcome { get; }
    public string? Note { get; }

    public string ObservedDisplay => ObservedValue is int v
        ? $"{StatName} {v}"
        : Outcome == ComparisonOutcome.Unmeasured ? "미측정/재인식 필요" : "관측값 없음";

    public string ReferenceDisplay => ReferenceValue is int v ? $"{StatName} {v}" : "참조 없음";

    public string OutcomeDisplay => Outcome switch
    {
        ComparisonOutcome.Match => "100% 충족",
        ComparisonOutcome.Below => "미달",
        ComparisonOutcome.NotFound => "판별 실패",
        ComparisonOutcome.Unmeasured => "미측정/재인식 필요",
        _ => "판정 불가",
    };
}
