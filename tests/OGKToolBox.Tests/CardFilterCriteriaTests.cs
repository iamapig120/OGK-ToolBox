using OGKToolBox.Core.Models;

namespace OGKToolBox.Tests;

public sealed class CardFilterCriteriaTests
{
    private static readonly Card ReimuA000 = Card(1, 2000, "SSR", "A000");
    private static readonly Card ReimuA001 = Card(2, 2000, "SR", "A001");
    private static readonly Card MarisaA001 = Card(3, 2001, "SSR", "A001");

    [Fact]
    public void EmptySelectionsMatchEveryCard()
    {
        var criteria = Criteria([], [], []);
        Assert.All(new[] { ReimuA000, ReimuA001, MarisaA001 }, card => Assert.True(criteria.Matches(card)));
    }

    [Fact]
    public void ValuesWithinAGroupUseOrLogic()
    {
        var criteria = Criteria([2000, 2001], ["SSR"], []);
        Assert.True(criteria.Matches(ReimuA000));
        Assert.True(criteria.Matches(MarisaA001));
        Assert.False(criteria.Matches(ReimuA001));
    }

    [Fact]
    public void DifferentGroupsUseAndLogic()
    {
        var criteria = Criteria([2000], ["SR", "SSR"], ["A001"]);
        Assert.False(criteria.Matches(ReimuA000));
        Assert.True(criteria.Matches(ReimuA001));
        Assert.False(criteria.Matches(MarisaA001));
    }

    private static CardFilterCriteria Criteria(int[] characters, string[] rarities, string[] packages) => new(
        characters.ToHashSet(),
        rarities.ToHashSet(StringComparer.OrdinalIgnoreCase),
        packages.ToHashSet(StringComparer.OrdinalIgnoreCase));

    private static Card Card(int id, int characterId, string rarity, string package) => new(
        id, $"card{id}", $"Card {id}", characterId, $"Character {characterId}", string.Empty,
        rarity, "AQUA", null, null, null, null, new(package, $"{package}/Card.xml", 0, true));
}
