using Address.Core;
using FluentAssertions;
using Xunit;

namespace Address.Core.Tests;

/// <summary>Checks the structured fields exposed by both API and bulk responses.</summary>
public sealed class AddressComponentsTests
{
    [Fact]
    public void ProjectsSubaddressAndStreetRangeFromGnafRecord()
    {
        var record = new AddressRecord("GANSW718477406", "SHOP 11 296-298 WINDSOR ST RICHMOND NSW 2753",
            "11", "296", "WINDSOR", "ST", "RICHMOND", "NSW", "2753", null, null,
            "SHOP 11 296 298 WINDSOR ST RICHMOND NSW 2753", FlatType: "SHOP",
            LevelType: "L", LevelNumber: "1", BuildingName: "WINDSOR CENTRE");

        var components = AddressComponents.From(record);

        components.FlatType.Should().Be("SHOP");
        components.FlatNumber.Should().Be("11");
        components.LevelType.Should().Be("L");
        components.LevelNumber.Should().Be("1");
        components.NumberFirst.Should().Be("296");
        components.NumberLast.Should().Be("298");
        components.BuildingName.Should().Be("WINDSOR CENTRE");
        components.StreetName.Should().Be("WINDSOR");
        components.State.Should().Be("NSW");
    }

    [Fact]
    public void OmitsAbsentComponentsInsteadOfReturningEmptyStrings()
    {
        var record = new AddressRecord("A", "100 GEORGE ST SYDNEY NSW 2000", "", "100",
            "GEORGE", "ST", "SYDNEY", "NSW", "2000", null, null,
            "100 GEORGE ST SYDNEY NSW 2000");

        var components = AddressComponents.From(record);

        components.FlatType.Should().BeNull();
        components.FlatNumber.Should().BeNull();
        components.LevelType.Should().BeNull();
        components.LevelNumber.Should().BeNull();
        components.NumberLast.Should().BeNull();
        components.StreetSuffix.Should().BeNull();
    }
}
