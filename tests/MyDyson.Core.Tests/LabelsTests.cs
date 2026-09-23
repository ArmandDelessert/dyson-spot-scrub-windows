using System.Text.Json.Nodes;
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
    public void ATypedRoomShowsItsTypesLabel(string type, string label)
    {
        // Dyson auto-fills a new zone's stored name from the type and appends a digit to keep
        // storage unique ("Chambre1", "Salon12"); that field is not what the room list shows.
        Assert.Equal(label, RoomTypeLabels.Resolve(type, label, "12"));
        Assert.Equal(label, RoomTypeLabels.Resolve(type, label + "3", "12"));
        Assert.Equal(label, RoomTypeLabels.Resolve(type, null, "12"));
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
        // The REST field is camelCase ("playRoom"); "playroom" is not a value the API produces, so
        // it falls through to the stored name like any unknown type.
        Assert.Equal("Salle de jeux", RoomTypeLabels.Resolve("playRoom", "Salle de jeux", "1"));
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

public class MapEditingTests
{
    [Fact]
    public void ARenamedRoomShowsTheNameTheUserChose()
    {
        // The phone app hides the stored name for a typed room; here that would make a rename from
        // the map manager look as if nothing had happened.
        Assert.Equal("Chambre d'amis", RoomTypeLabels.Resolve("bedroom", "Chambre d'amis", "12"));
        // But the auto-filled default, with or without Dyson's uniqueness digit, still reads as the type.
        Assert.Equal("Chambre", RoomTypeLabels.Resolve("bedroom", "Chambre", "12"));
        Assert.Equal("Chambre", RoomTypeLabels.Resolve("bedroom", "Chambre1", "12"));
        Assert.Equal("Salon", RoomTypeLabels.Resolve("livingRoom", "Salon12", "14"));
        // A name that merely starts like the label is not the default.
        Assert.Equal("Chambre bis", RoomTypeLabels.Resolve("bedroom", "Chambre bis", "12"));
    }

    [Fact]
    public void TheTypeListIsOfferedWithItsDefaultNames()
    {
        Assert.Equal(30, RoomTypeLabels.All.Count);
        Assert.Contains(("bedroom", "Chambre"), RoomTypeLabels.All);
        Assert.Equal("Débarras", RoomTypeLabels.DefaultNameFor("storageRoom"));
        // "custom" has no label of its own: it is the escape hatch for a free name.
        Assert.Null(RoomTypeLabels.DefaultNameFor("custom"));
        Assert.DoesNotContain(RoomTypeLabels.All, t => t.Type == "custom");
    }

    [Fact]
    public void ARoomNameIsEncodedTheWayTheAppSendsIt()
    {
        // Captured 2026-09-19: both a typed room and a free name travel as JSON, the free one under
        // type "custom". A bare string is never sent.
        Assert.Equal("""{"type":"storageRoom","name":"D\u00E9barras"}""", RoomPreference.JoinName("Débarras", "storageRoom"));
        Assert.Equal("""{"type":"custom","name":"Pi\u00E8ce secr\u00E8te"}""", RoomPreference.JoinName("Pièce secrète", "custom"));
        Assert.Equal(("Pièce secrète", "custom"), RoomPreference.SplitName(RoomPreference.JoinName("Pièce secrète", "custom")));
    }

    [Fact]
    public void AMapEditReplyIsReadBackAndAnythingElseIsRefused()
    {
        var ok = MapEditResult.From((JsonObject)JsonNode.Parse(
            """{"msgId":"1","code":0,"method":"service.rename_room","data":{"map_id":1000000002,"map_type":3,"timestamp":1789835979}}""")!);
        Assert.Equal(new MapEditResult(1000000002, 3, 1789835979), ok);

        // A refused edit does not come back with an error field, it just is not this shape.
        Assert.Null(MapEditResult.From((JsonObject)JsonNode.Parse("""{"msgId":"1","code":1,"data":{"result":1}}""")!));
        Assert.Null(MapEditResult.From((JsonObject)JsonNode.Parse("""{"msgId":"1","code":0}""")!));
        Assert.Null(MapEditResult.From(null));
    }
}
