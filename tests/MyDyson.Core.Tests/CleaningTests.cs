using MyDyson.Core;

namespace MyDyson.Core.Tests;

public class CleaningTests
{
    [Theory]
    [InlineData(CleanType.Vacuum, "vacuum", 0)]
    [InlineData(CleanType.VacuumAndMop, "vacuumAndMop", 1)]
    [InlineData(CleanType.Mop, "mop", 2)]
    [InlineData(CleanType.VacuumThenMop, "vacuumThenMop", 3)]
    public void RestAndJdmMappingsRoundTrip(CleanType type, string rest, int jdm)
    {
        Assert.Equal(rest, type.ToRest());
        Assert.Equal(type, CleanTypes.FromRest(rest));
        Assert.Equal(jdm, type.ToJdm());
        Assert.Equal(type, CleanTypes.FromJdm(jdm));
    }

    [Fact]
    public void UnknownRestValueFallsBackToVacuum()
    {
        Assert.Equal(CleanType.Vacuum, CleanTypes.FromRest(null));
        Assert.Equal(CleanType.Vacuum, CleanTypes.FromRest("something-new"));
    }

    [Fact]
    public void RoomPreferenceIndicesMatchTheCaptures()
    {
        // From captures: index 3 is the clean type, 8 the selection flag, 10 the order.
        Assert.Equal(3, CleaningSequence.IndexCleanType);
        Assert.Equal(8, CleaningSequence.IndexSelected);
        Assert.Equal(10, CleaningSequence.IndexOrder);
    }
}
