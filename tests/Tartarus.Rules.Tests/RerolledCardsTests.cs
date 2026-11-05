using Tartarus.Vision;

namespace Tartarus.Rules.Tests;

public sealed class RerolledCardsTests
{
    private static readonly DateTime Click = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Old = ["chains", "axe", "whip"];

    private static RerolledCards Clicked()
    {
        var cards = new RerolledCards();
        cards.Clicked(Old, Click);
        return cards;
    }

    [Fact]
    public void NoClick_NothingIsGoing() => Assert.False(new RerolledCards().StillGoing(Old, 3, Click));

    [Fact]
    public void OldCards_AreGoing_UntilTheLimit()
    {
        var cards = Clicked();
        Assert.True(cards.StillGoing(Old, 3, Click.AddSeconds(1)));
        Assert.True(cards.StillGoing(Old, 3, Click + RerolledCards.Limit - TimeSpan.FromMilliseconds(1)));
        Assert.False(cards.StillGoing(Old, 3, Click + RerolledCards.Limit));
    }

    [Fact]
    public void SameCardsInAnotherOrder_AreTheNewOffer_AtOnce() =>
        // The Korean player's Re-roll of 2026-11-05.
        Assert.False(Clicked().StillGoing(["whip", "chains", "axe"], 3, Click.AddMilliseconds(700)));

    [Fact]
    public void OtherCards_AreTheNewOffer() => Assert.False(Clicked().StillGoing(["saw", "hook", "cage"], 3, Click.AddMilliseconds(700)));

    [Fact]
    public void OneOldCardOfTheNewOffer_IsTheNewOffer() => Assert.False(Clicked().StillGoing(["saw", "axe", "cage"], 3, Click.AddMilliseconds(700)));

    [Fact]
    public void PartOfTheOldCardsRead_IsGoing_WhateverItsPlace() => Assert.True(Clicked().StillGoing(["whip", "chains"], 3, Click.AddMilliseconds(300)));

    [Fact]
    public void NothingRead_IsNotGoing() => Assert.False(Clicked().StillGoing([], 3, Click.AddMilliseconds(300)));

    [Fact]
    public void OnceOver_TheOldOrderIsNotGoingAgain()
    {
        var cards = Clicked();
        Assert.False(cards.StillGoing(["saw", "hook", "cage"], 3, Click.AddMilliseconds(700)));
        Assert.False(cards.StillGoing(Old, 3, Click.AddMilliseconds(800)));
    }

    [Fact]
    public void ANewClick_CountsFromItself()
    {
        var cards = Clicked();
        cards.Clicked(["saw", "hook", "cage"], Click.AddSeconds(10));
        Assert.True(cards.StillGoing(["saw", "hook", "cage"], 3, Click.AddSeconds(11)));
    }
}
