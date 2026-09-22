using MyDyson.Core;

namespace MyDyson.Core.Tests;

public class RoomTypeLabelsTests
{
    [Theory]
    [InlineData("bedroom", "Chambre")]
    [InlineData("toilet", "W.-C.")]
    [InlineData("utilityRoom", "Cave")]
    [InlineData("primaryBathroom", "Salle de bain parentale")]
    [InlineData("balcony", "Balcon")]
    public void ATypedRoomShowsItsTypesLabelAndIgnoresTheStoredName(string type, string label)
    {
        // Dyson auto-fills a new zone's stored name from the type and appends a digit to keep
        // storage unique ("Chambre1", "Salon12"); the app never shows that field for a typed room.
        Assert.Equal(label, RoomTypeLabels.Resolve(type, "Chambre1", "12"));
    }

    [Fact]
    public void AnUntypedOrCustomRoomKeepsItsStoredName()
    {
        // "custom" has no default label of its own, so what the user stored is what shows.
        Assert.Equal("Pièce1", RoomTypeLabels.Resolve("custom", "Pièce1", "15"));
        Assert.Equal("Pièce1", RoomTypeLabels.Resolve(null, "Pièce1", "15"));
        Assert.Equal("Pièce1", RoomTypeLabels.Resolve("", "Pièce1", "15"));
        // A name the user deliberately chose is never second-guessed either.
        Assert.Equal("Null", RoomTypeLabels.Resolve(null, "Null", "16"));
    }

    [Fact]
    public void AnUnknownTypeFallsBackRatherThanShowingTheRawType()
    {
        // A type added by a future firmware must not leak "someNewType" into the room list.
        Assert.Equal("Chambre1", RoomTypeLabels.Resolve("someNewType", "Chambre1", "12"));
        // With nothing stored either, the zone id is the last resort.
        Assert.Equal("12", RoomTypeLabels.Resolve("someNewType", null, "12"));
    }

    [Fact]
    public void TypeMatchingIsCaseSensitiveBecauseTheApiIs()
    {
        // The REST field is camelCase ("playRoom"); "playroom" is not a value the API produces.
        Assert.Equal("Salle de jeux", RoomTypeLabels.Resolve("playRoom", "x", "1"));
        Assert.Equal("x", RoomTypeLabels.Resolve("playroom", "x", "1"));
    }
}

public class CleanStatusLabelsTests
{
    [Theory]
    [InlineData("CLEAN_COMPLETE", "Terminée")]
    [InlineData("CANT_CLEAN", "Injoignable")]
    [InlineData("CLEAN_NOT_REQUESTED", "Non demandée")]
    [InlineData("CLEAN_PENDING", "Non commencée")]
    public void TheFourObservedStatusesHaveALabel(string status, string label) =>
        Assert.Equal(label, CleanStatusLabels.Resolve(status));

    [Fact]
    public void AnAbsentStatusIsBlankAndAnUnknownOneIsShownAsIs()
    {
        Assert.Equal("", CleanStatusLabels.Resolve(null));
        // Better a raw value in the UI than silence, since this is how a new status gets noticed.
        Assert.Equal("CLEAN_SOMETHING_NEW", CleanStatusLabels.Resolve("CLEAN_SOMETHING_NEW"));
    }
}
