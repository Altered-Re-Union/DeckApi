using DeckApi.Data;

namespace DeckApi.Decks;

/// <summary>
/// Sealed decks are always archived (<c>isDraft: true</c>), whatever the owner or the old API says. A sealed deck only
/// makes sense in its tournament: BGA does not get it from the player's deck list (altered-bga-api lists
/// <c>isDraft=false</c> decks only) but from altered-draft's sealed decklist, then loads it by id, where
/// <c>isDraft</c> is ignored. Archiving keeps them out of every other BGA table.
/// </summary>
public static class SealedDecks
{
    public const string Format = "sealed";

    public static void KeepArchived(Deck deck)
    {
        if (deck.Format == Format) deck.IsDraft = true;
    }
}
