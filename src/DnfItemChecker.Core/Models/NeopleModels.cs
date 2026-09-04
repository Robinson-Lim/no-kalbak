using System.Text.Json.Serialization;
using DnfItemChecker.Core.Json;

namespace DnfItemChecker.Core.Models;

/// <summary>A Neople row-wrapped list response: <c>{ "rows": [ ... ] }</c>.</summary>
public sealed record RowList<T>(
    [property: JsonPropertyName("rows")] IReadOnlyList<T>? Rows);

public sealed record DfServer(
    [property: JsonPropertyName("serverId")] string ServerId,
    [property: JsonPropertyName("serverName")] string ServerName);

/// <summary>A character as returned by the character search endpoint (no adventureName).</summary>
public sealed record DfCharacterSearchRow(
    [property: JsonPropertyName("serverId")] string ServerId,
    [property: JsonPropertyName("characterId")] string CharacterId,
    [property: JsonPropertyName("characterName")] string CharacterName,
    [property: JsonPropertyName("level")] int Level,
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("jobGrowId")] string JobGrowId,
    [property: JsonPropertyName("jobName")] string JobName,
    [property: JsonPropertyName("jobGrowName")] string JobGrowName,
    [property: JsonPropertyName("fame")] long? Fame);

/// <summary>Character basic info (endpoint 03) - the only place adventureName is exposed.</summary>
public sealed record DfCharacterInfo(
    [property: JsonPropertyName("serverId")] string ServerId,
    [property: JsonPropertyName("characterId")] string CharacterId,
    [property: JsonPropertyName("characterName")] string CharacterName,
    [property: JsonPropertyName("level")] int Level,
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("jobGrowId")] string JobGrowId,
    [property: JsonPropertyName("jobName")] string JobName,
    [property: JsonPropertyName("jobGrowName")] string JobGrowName,
    [property: JsonPropertyName("fame")] long? Fame,
    [property: JsonPropertyName("adventureName")] string? AdventureName,
    [property: JsonPropertyName("guildName")] string? GuildName);

/// <summary>A single named stat line. Value may be numeric or percentage string.</summary>
public sealed record DfStat(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value"), JsonConverter(typeof(FlexibleStringConverter))] string? Value);

/// <summary>Item summary as returned by the item search endpoint.</summary>
public sealed record DfItemSearchRow(
    [property: JsonPropertyName("itemId")] string ItemId,
    [property: JsonPropertyName("itemName")] string ItemName,
    [property: JsonPropertyName("itemRarity")] string ItemRarity,
    [property: JsonPropertyName("itemType")] string? ItemType,
    [property: JsonPropertyName("itemTypeDetail")] string? ItemTypeDetail,
    [property: JsonPropertyName("itemAvailableLevel")] int ItemAvailableLevel);

/// <summary>Full item detail (endpoint 25). Stats are at 최상급 100%.</summary>
public sealed record DfItemDetail(
    [property: JsonPropertyName("itemId")] string ItemId,
    [property: JsonPropertyName("itemName")] string ItemName,
    [property: JsonPropertyName("itemRarity")] string ItemRarity,
    [property: JsonPropertyName("itemType")] string? ItemType,
    [property: JsonPropertyName("itemTypeDetail")] string? ItemTypeDetail,
    [property: JsonPropertyName("itemAvailableLevel")] int ItemAvailableLevel,
    [property: JsonPropertyName("setItemId")] string? SetItemId,
    [property: JsonPropertyName("setItemName")] string? SetItemName,
    [property: JsonPropertyName("itemStatus")] IReadOnlyList<DfStat>? ItemStatus,
    [property: JsonPropertyName("itemExplain")] string? ItemExplain);

public sealed record DfEnchant(
    [property: JsonPropertyName("status")] IReadOnlyList<DfStat>? Status);

/// <summary>One equipped item (within the equipment endpoint's <c>equipment</c> array).</summary>
public sealed record DfEquippedItem(
    [property: JsonPropertyName("slotId")] string SlotId,
    [property: JsonPropertyName("slotName")] string SlotName,
    [property: JsonPropertyName("itemId")] string ItemId,
    [property: JsonPropertyName("itemName")] string ItemName,
    [property: JsonPropertyName("itemType")] string? ItemType,
    [property: JsonPropertyName("itemTypeDetail")] string? ItemTypeDetail,
    [property: JsonPropertyName("itemAvailableLevel")] int ItemAvailableLevel,
    [property: JsonPropertyName("itemRarity")] string ItemRarity,
    [property: JsonPropertyName("setItemId")] string? SetItemId,
    [property: JsonPropertyName("setItemName")] string? SetItemName,
    [property: JsonPropertyName("reinforce")] int Reinforce,
    [property: JsonPropertyName("itemGradeName")] string? ItemGradeName,
    [property: JsonPropertyName("amplificationName")] string? AmplificationName,
    [property: JsonPropertyName("refine")] int Refine,
    [property: JsonPropertyName("enchant")] DfEnchant? Enchant);

/// <summary>Equipped equipment response (endpoint 06).</summary>
public sealed record DfEquipmentResponse(
    [property: JsonPropertyName("serverId")] string ServerId,
    [property: JsonPropertyName("characterId")] string CharacterId,
    [property: JsonPropertyName("characterName")] string CharacterName,
    [property: JsonPropertyName("jobName")] string? JobName,
    [property: JsonPropertyName("jobGrowName")] string? JobGrowName,
    [property: JsonPropertyName("adventureName")] string? AdventureName,
    [property: JsonPropertyName("equipment")] IReadOnlyList<DfEquippedItem>? Equipment);

/// <summary>Character status response (endpoint 05): final aggregate stats.</summary>
public sealed record DfStatusResponse(
    [property: JsonPropertyName("status")] IReadOnlyList<DfStat>? Status);

public sealed record DfSetItemRow(
    [property: JsonPropertyName("setItemId")] string SetItemId,
    [property: JsonPropertyName("setItemName")] string SetItemName);

public sealed record DfJob(
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("jobName")] string JobName,
    [property: JsonPropertyName("rows")] IReadOnlyList<DfJobGrow>? Rows);

public sealed record DfJobGrow(
    [property: JsonPropertyName("jobGrowId")] string JobGrowId,
    [property: JsonPropertyName("jobGrowName")] string JobGrowName);
