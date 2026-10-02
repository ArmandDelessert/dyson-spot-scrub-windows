using System.Text.Json.Nodes;
using Dyss.Core;

namespace Dyss.Core.Tests;

public class RoomTypeLabelsTests
{
    [Fact]
    public void TheStoredNameIsWhatShowsWhateverTheType()
    {
        // The phone app shows a typed room under its type's label; this app shows the name the
        // room actually carries, so that what was typed in a rename is what appears.
        Assert.Equal("Chambre de Paul", RoomTypeLabels.Resolve("bedroom", "Chambre de Paul", "12"));
        Assert.Equal("Salon12", RoomTypeLabels.Resolve("livingRoom", "Salon12", "14"));
        // Seen on a real account: a room typed W.-C. still named after its creation.
        Assert.Equal("Salle de bain", RoomTypeLabels.Resolve("toilet", "Salle de bain", "11"));
        Assert.Equal("Pièce1", RoomTypeLabels.Resolve("custom", "Pièce1", "15"));
        Assert.Equal("Null", RoomTypeLabels.Resolve(null, "Null", "16"));
    }

    [Fact]
    public void WithNoNameTheTypeStandsInThenTheId()
    {
        // Never seen from the robot (a room shown as "<Null>" really is named that), but a missing
        // name should still read as something.
        Assert.Equal("Balcon", RoomTypeLabels.Resolve("balcony", null, "15"));
        Assert.Equal("Balcon", RoomTypeLabels.Resolve("balcony", "", "15"));
        Assert.Equal("15", RoomTypeLabels.Resolve("custom", null, "15"));
        Assert.Equal("15", RoomTypeLabels.Resolve("someNewType", null, "15"));
    }

    [Theory]
    [InlineData("livingRoom", "Salon", "")]              // name and type agree: the type adds nothing
    [InlineData("livingRoom", "Salon1", "Salon")]        // they differ: both are worth seeing
    [InlineData("toilet", "Salle de bain", "W.-C.")]
    [InlineData("custom", "Pièce secrète", "Personnalisée")]
    [InlineData(null, "Pièce1", "Personnalisée")]        // what a split leaves behind: no type at all
    [InlineData("", "Pièce1", "Personnalisée")]
    [InlineData("someNewType", "Pièce", "someNewType")]  // a future type is shown raw, not hidden
    [InlineData("balcony", null, "")]                    // no name: the label already stands in for it
    public void TheTypeHintOnlyAppearsWhenItSaysSomethingTheNameDoesNot(string? type, string? name, string hint) =>
        Assert.Equal(hint, RoomTypeLabels.TypeHint(type, name));

    [Fact]
    public void TypeMatchingIsCaseSensitiveBecauseTheApiIs()
    {
        // The REST field is camelCase ("playRoom"); "playroom" is not a value the API produces.
        Assert.Equal("Salle de jeux", RoomTypeLabels.DefaultNameFor("playRoom"));
        Assert.Null(RoomTypeLabels.DefaultNameFor("playroom"));
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
    public void TheTwoReplyShapesOfAMapEditAreBothRead()
    {
        // rename_room, split_room and arrange_room name the map they re-saved...
        var named = MapEditResult.From((JsonObject)JsonNode.Parse(
            """{"msgId":"1","code":0,"method":"service.rename_room","data":{"map_id":1000000002,"map_type":3,"timestamp":1789835979}}""")!);
        Assert.Equal(new MapEditResult(1000000002, 3, 1789835979), named);

        // ...while rename_map and set_cur_map answer with a bare result code. Reading only the
        // first shape reported every successful map rename as a refusal.
        var coded = MapEditResult.From((JsonObject)JsonNode.Parse(
            """{"msgId":"1","code":0,"method":"service.rename_map","data":{"result":0}}""")!);
        Assert.NotNull(coded);
        Assert.Null(coded.MapId);
    }

    [Fact]
    public void ARefusedOrUnrecognisedReplyIsNothing()
    {
        // A refused edit does not come back with an error field; result 1 is the refusal.
        Assert.Null(MapEditResult.From((JsonObject)JsonNode.Parse("""{"msgId":"1","code":1,"data":{"result":1}}""")!));
        Assert.Null(MapEditResult.From((JsonObject)JsonNode.Parse("""{"msgId":"1","code":0,"data":{}}""")!));
        Assert.Null(MapEditResult.From((JsonObject)JsonNode.Parse("""{"msgId":"1","code":0}""")!));
        Assert.Null(MapEditResult.From(null));
    }
}
