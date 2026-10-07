using System.Text.RegularExpressions;

namespace Address.Core;

/// <summary>Public components projected from one serving record for sync and bulk responses.</summary>
public sealed record AddressComponents(
    string? BuildingName, string? FlatType, string? FlatNumber,
    string? LevelType, string? LevelNumber, string? NumberFirst, string? NumberLast,
    string? StreetName, string? StreetType, string? StreetSuffix,
    string? Locality, string? State, string? Postcode)
{
    /// <summary>Projects a canonical serving row, using null for absent response components.</summary>
    public static AddressComponents From(AddressRecord address)
    {
        static string? Value(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
        var flatNumber = address.FlatNumberPrefix + address.FlatNumber + address.FlatNumberSuffix;
        var levelNumber = address.LevelNumberPrefix + address.LevelNumber + address.LevelNumberSuffix;
        // Existing serving tables retain number_first but not number_last. Recover a
        // range from the canonical G-NAF line without rewriting the national tables.
        var range = address.NumberFirst.Length == 0 || address.StreetName.Length == 0 ? Match.Empty :
            Regex.Match(address.FormattedAddress,
                $@"\b{Regex.Escape(address.NumberFirst)}-([0-9]+[A-Z]?)\s+{Regex.Escape(address.StreetName)}\b",
                RegexOptions.IgnoreCase);
        return new(Value(address.BuildingName), Value(address.FlatType), Value(flatNumber),
            Value(address.LevelType), Value(levelNumber), Value(address.NumberFirst),
            range.Success ? range.Groups[1].Value : null, Value(address.StreetName),
            Value(address.StreetType), null, Value(address.LocalityName),
            Value(address.State), Value(address.Postcode));
    }
}
