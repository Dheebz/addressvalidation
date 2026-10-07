using Address.Core;
using FluentAssertions;
using Xunit;

namespace Address.Core.Tests;

/// <summary>Locks down normalization, subaddress distinctions, and validation confidence policy.</summary>
public sealed class AddressServiceTests
{
    [Theory]
    [InlineData("10 Collins St, Melbourne VIC 3000", "10 COLLINS STREET MELBOURNE VIC 3000")]
    [InlineData("  Unit   4/100 George Rd ", "UNIT 4 100 GEORGE ROAD")]
    [InlineData("4/100 George Rd", "UNIT 4 100 GEORGE ROAD")]
    [InlineData("U4/100 George Rd", "UNIT 4 100 GEORGE ROAD")]
    [InlineData("Apt18C L1 54 Smith St", "APARTMENT 18C L 1 54 SMITH STREET")]
    public void NormalizesComparisonText(string input, string expected) =>
        AddressNormalizer.Normalize(input).Should().Be(expected);

    [Theory]
    [InlineData("Unit 4 10 Collins St Melbourne VIC 3000", "COLLINS")]
    [InlineData("24 smth st bris", "SMTH")]
    [InlineData("6 Packham Place Charnwood ACT 2615", "PACKHAM")]
    [InlineData("24 Smith Melbourne", "SMITH")]
    public void ExtractsStreetHintForIndexedSearch(string input, string expected) =>
        AddressNormalizer.StreetHint(AddressNormalizer.Normalize(input)).Should().Be(expected);

    [Theory]
    [InlineData("24 smth st bris", "24")]
    [InlineData("Unit 4 10 Collins St Melbourne VIC 3000", "10")]
    [InlineData("6 Packham Place Charnwood ACT 2615", "6")]
    public void ExtractsHouseNumberForIndexedSearch(string input, string expected) =>
        AddressNormalizer.HouseNumberHint(AddressNormalizer.Normalize(input)).Should().Be(expected);

    [Fact]
    public async Task ValidationNeverClaimsPostalDelivery()
    {
        var record = new AddressRecord("ID1", "10 Collins Street Melbourne VIC 3000", "", "10", "COLLINS",
            "STREET", "MELBOURNE", "VIC", "3000", null, null, "10 COLLINS STREET MELBOURNE VIC 3000");
        var result = await new AddressService(new StubRepository(record)).ValidateAsync(record.FormattedAddress, default);
        result.Valid.Should().BeTrue();
        result.Status.Should().Be("GNAF_MATCHED");
        result.PostalDeliveryStatus.Should().Be("POSTAL_DELIVERY_NOT_VERIFIED");
    }

    [Fact]
    public async Task NationalStreetAbbreviationScoresAsNormalizedMatch()
    {
        var record = new AddressRecord("GAACT714845933", "6 PACKHAM PL CHARNWOOD ACT 2615", "", "6",
            "PACKHAM", "PL", "CHARNWOOD", "ACT", "2615", null, null, "6 PACKHAM PL CHARNWOOD ACT 2615");
        var service = new AddressService(new StubRepository(record));
        var match = await service.MatchAsync("6 Packham Place Charnwood ACT 2615", default);
        match.Confidence.Should().Be(1);
        match.MatchType.Should().Be("NORMALISED");
        AddressNormalizer.AbbreviateForSearch(AddressNormalizer.Normalize("6 Packham Place Charnwood ACT 2615"))
            .Should().Be(record.SearchText);
    }

    [Fact]
    public async Task ExactShopWithRangedStreetNumberValidates()
    {
        var record = new AddressRecord("GANSW718477406", "SHOP 11 296-298 WINDSOR ST RICHMOND NSW 2753",
            "11", "296", "WINDSOR", "ST", "RICHMOND", "NSW", "2753", null, null,
            "SHOP 11 296 298 WINDSOR ST RICHMOND NSW 2753", FlatType: "SHOP");
        var result = await new AddressService(new StubRepository(record))
            .ValidateAsync(record.FormattedAddress, default);
        result.Valid.Should().BeTrue();
        result.Confidence.Should().Be(1);
        result.Address!.GnafPid.Should().Be(record.GnafPid);
    }

    [Fact]
    public async Task DifferentHouseNumberDoesNotValidate()
    {
        var record = new AddressRecord("GAACT714845933", "6 PACKHAM PL CHARNWOOD ACT 2615", "", "6",
            "PACKHAM", "PL", "CHARNWOOD", "ACT", "2615", null, null, "6 PACKHAM PL CHARNWOOD ACT 2615");
        var result = await new AddressService(new StubRepository(record))
            .ValidateAsync("8 Packham Pl Charnwood ACT 2615", default);
        result.Valid.Should().BeFalse();
        result.Confidence.Should().BeLessThan(0.8);
    }

    [Fact]
    public async Task DifferentPostcodeDoesNotValidate()
    {
        var record = new AddressRecord("ID1", "10 COLLINS ST MELBOURNE VIC 3000", "", "10",
            "COLLINS", "ST", "MELBOURNE", "VIC", "3000", null, null,
            "10 COLLINS ST MELBOURNE VIC 3000");

        var result = await new AddressService(new StubRepository(record))
            .ValidateAsync("10 Collins St Melbourne VIC 3001", default);

        result.Valid.Should().BeFalse();
        result.Address.Should().BeNull();
    }

    [Theory]
    [InlineData("10 Collins St Geelong VIC 3000")]
    [InlineData("10 Collins St Melbourne NSW 3000")]
    public async Task ExplicitlyWrongLocalityOrStateDoesNotValidate(string input)
    {
        var record = new AddressRecord("ID1", "10 COLLINS ST MELBOURNE VIC 3000", "", "10",
            "COLLINS", "ST", "MELBOURNE", "VIC", "3000", null, null,
            "10 COLLINS ST MELBOURNE VIC 3000");
        (await new AddressService(new StubRepository(record)).ValidateAsync(input, default))
            .Valid.Should().BeFalse();
    }

    [Fact]
    public async Task StreetAndLocalityAliasStillRequiresCorrectHouseNumber()
    {
        var record = new AddressRecord("ID1", "10 KING ST CANBERRA ACT 2600", "", "10",
            "KING", "ST", "CANBERRA", "ACT", "2600", null, null,
            "10 KING ST CANBERRA ACT 2600")
        { MatchText = "10 ROYAL STREET CAPITAL ACT 2600" };
        var service = new AddressService(new StubRepository(record));
        (await service.MatchAsync("10 Royal Street Capital ACT 2600", default)).MatchType
            .Should().Be("ALIAS");
        (await service.ValidateAsync("11 Royal Street Capital ACT 2600", default)).Valid
            .Should().BeFalse();
    }

    [Fact]
    public async Task BuildingNameMayBeOmittedWhenStreetAddressAgrees()
    {
        var record = new AddressRecord("ID1", "ROYAL ARCADE 10 COLLINS ST MELBOURNE VIC 3000", "", "10",
            "COLLINS", "ST", "MELBOURNE", "VIC", "3000", null, null,
            "ROYAL ARCADE 10 COLLINS ST MELBOURNE VIC 3000", BuildingName: "ROYAL ARCADE");

        var result = await new AddressService(new StubRepository(record))
            .ValidateAsync("10 Collins St Melbourne VIC 3000", default);

        result.Valid.Should().BeTrue();
        result.Address!.GnafPid.Should().Be("ID1");
    }

    [Fact]
    public async Task FormattingUsesCanonicalGnafAddressWhenMatched()
    {
        var record = new AddressRecord("GAACT714845933", "6 PACKHAM PL CHARNWOOD ACT 2615", "", "6",
            "PACKHAM", "PL", "CHARNWOOD", "ACT", "2615", null, null, "6 PACKHAM PL CHARNWOOD ACT 2615");
        var result = await new AddressService(new StubRepository(record))
            .FormatAsync("6 Packham Place Charnwood ACT 2615", default);
        result.Matched.Should().BeTrue();
        result.GnafPid.Should().Be(record.GnafPid);
        result.Formatted.Should().Be(record.FormattedAddress);
    }

    [Fact]
    public async Task FormattingDoesNotClaimGnafMatchForWrongUnit()
    {
        var record = new AddressRecord("ID2", "UNIT 2 14 SMITH ST SYDNEY NSW 2000", "2", "14",
            "SMITH", "ST", "SYDNEY", "NSW", "2000", null, null,
            "UNIT 2 14 SMITH ST SYDNEY NSW 2000", FlatType: "UNIT");

        var result = await new AddressService(new StubRepository(record))
            .FormatAsync("Unit 9 14 Smith St Sydney NSW 2000", default);

        result.Matched.Should().BeFalse();
        result.GnafPid.Should().BeNull();
        result.Formatted.Should().Be("UNIT 9 14 SMITH STREET SYDNEY NSW 2000");
    }

    [Fact]
    public async Task TooShortQueryDoesNotSuggestOrMatch()
    {
        var record = new AddressRecord("ID1", "10 COLLINS ST MELBOURNE VIC 3000", "", "10",
            "COLLINS", "ST", "MELBOURNE", "VIC", "3000", null, null,
            "10 COLLINS ST MELBOURNE VIC 3000");
        var service = new AddressService(new StubRepository(record));

        (await service.SuggestAsync("!", 10, default)).Should().BeEmpty();
        (await service.MatchAsync("!", default)).MatchType.Should().Be("NO_MATCH");
    }

    [Fact]
    public async Task SuggestionsCollapseDuplicateAddressLabels()
    {
        var first = new AddressRecord("A", "1 COLLINS ST MELBOURNE VIC 3000", "", "1", "COLLINS",
            "ST", "MELBOURNE", "VIC", "3000", null, null, "1 COLLINS ST MELBOURNE VIC 3000");
        var second = first with { GnafPid = "B" };
        var suggestions = await new AddressService(new StubRepository(first, second))
            .SuggestAsync("1 Collins St Melbourne VIC 3000", 10, default);
        suggestions.Should().ContainSingle();
    }

    [Theory]
    [InlineData("Apt 2 14 Smith St Sydney NSW 2000")]
    [InlineData("Flat 2 14 Smith St Sydney NSW 2000")]
    [InlineData("2/14 Smith St Sydney NSW 2000")]
    public async Task ResidentialAliasesMatchSameUnit(string input)
    {
        var record = new AddressRecord("ID2", "UNIT 2 14 SMITH ST SYDNEY NSW 2000", "2", "14",
            "SMITH", "ST", "SYDNEY", "NSW", "2000", null, null,
            "UNIT 2 14 SMITH ST SYDNEY NSW 2000", FlatType: "UNIT");
        var result = await new AddressService(new StubRepository(record)).ValidateAsync(input, default);
        result.Valid.Should().BeTrue();
        result.Address!.GnafPid.Should().Be("ID2");
    }

    [Fact]
    public async Task UnitNumberCannotMaskWrongStreetNumber()
    {
        var record = new AddressRecord("ID4", "UNIT 1 1 SMITH ST SYDNEY NSW 2000", "1", "1",
            "SMITH", "ST", "SYDNEY", "NSW", "2000", null, null,
            "UNIT 1 1 SMITH ST SYDNEY NSW 2000", FlatType: "UNIT");
        var result = await new AddressService(new StubRepository(record)).ValidateAsync(
            "Unit 1 5 Smith St Sydney NSW 2000", default);
        result.Valid.Should().BeFalse();
    }

    [Theory]
    [InlineData("Suite 18C Level 1 54 Smith St Sydney NSW 2000", true)]
    [InlineData("Suite 18B Level 1 54 Smith St Sydney NSW 2000", false)]
    [InlineData("Suite 18C Level 2 54 Smith St Sydney NSW 2000", false)]
    [InlineData("Unit 18C Level 1 54 Smith St Sydney NSW 2000", false)]
    public async Task SuiteSuffixAndLevelMustAgree(string input, bool expected)
    {
        var record = new AddressRecord("ID3", "SUITE 18C LEVEL 1 54 SMITH ST SYDNEY NSW 2000",
            "18", "54", "SMITH", "ST", "SYDNEY", "NSW", "2000", null, null,
            "SUITE 18C LEVEL 1 54 SMITH ST SYDNEY NSW 2000", FlatType: "SE",
            FlatNumberSuffix: "C", LevelType: "L", LevelNumber: "1");
        var result = await new AddressService(new StubRepository(record)).ValidateAsync(input, default);
        result.Valid.Should().Be(expected);
        if (!expected) result.Status.Should().Be("SUBADDRESS_UNCONFIRMED");
    }

    [Theory]
    [InlineData("Lower Ground", "LG")]
    [InlineData("LG", "LG")]
    [InlineData("Upper Ground", "UG")]
    [InlineData("Mezzanine", "M")]
    public void NamedLevelsRemainDistinct(string input, string expected) =>
        SubaddressParser.Parse(AddressNormalizer.Normalize(input)).LevelType.Should().Be(expected);

    [Fact]
    public async Task LowerGroundAliasDoesNotBecomeGround()
    {
        var record = new AddressRecord("ID5", "SHOP 2 LOWER GROUND 10 SMITH ST SYDNEY NSW 2000",
            "2", "10", "SMITH", "ST", "SYDNEY", "NSW", "2000", null, null,
            "SHOP 2 LOWER GROUND 10 SMITH ST SYDNEY NSW 2000", FlatType: "SHOP", LevelType: "LG");
        var service = new AddressService(new StubRepository(record));
        (await service.ValidateAsync("Shop 2 LG 10 Smith St Sydney NSW 2000", default)).Valid.Should().BeTrue();
        (await service.ValidateAsync("Shop 2 Ground 10 Smith St Sydney NSW 2000", default)).Valid.Should().BeFalse();
    }

    private sealed class StubRepository(params AddressRecord[] records) : IAddressRepository
    {
        public Task<AddressRecord?> GetByIdAsync(string id, CancellationToken ct) =>
            Task.FromResult<AddressRecord?>(records.FirstOrDefault(x => x.GnafPid == id));
        public Task<IReadOnlyList<AddressRecord>> SearchAsync(string query, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AddressRecord>>(records);
        public Task<string?> ActiveVersionAsync(CancellationToken ct) => Task.FromResult<string?>("sample-1");
        public Task<DatasetMetadata?> ActiveDatasetAsync(CancellationToken ct) => Task.FromResult<DatasetMetadata?>(new("G-NAF", "test"));
    }
}
