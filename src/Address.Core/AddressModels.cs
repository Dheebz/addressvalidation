using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Address.Core;

/// <summary>A canonical serving row. Empty strings represent absent G-NAF components in storage.</summary>
public sealed record AddressRecord(
    string GnafPid, string FormattedAddress, string FlatNumber, string NumberFirst,
    string StreetName, string StreetType, string LocalityName, string State,
    string Postcode, double? Latitude, double? Longitude, string SearchText,
    string FlatType = "", string FlatNumberPrefix = "", string FlatNumberSuffix = "",
    string LevelType = "", string LevelNumber = "", string LevelNumberPrefix = "",
    string LevelNumberSuffix = "", string BuildingName = "")
{
    /// <summary>Search-only alias spelling; responses keep the canonical components and G-NAF ID.</summary>
    public string MatchText { get; init; } = "";
}

/// <summary>A display suggestion with its canonical row for structured responses.</summary>
public sealed record Suggestion(string Id, string Label, double Score, AddressRecord Address);
/// <summary>Identity of the active dataset served by the repository.</summary>
public sealed record DatasetMetadata(string Name, string Version);
/// <summary>Best candidate, similarity score, and reason for the match classification.</summary>
public sealed record AddressMatch(AddressRecord? Address, double Confidence, string MatchType);
/// <summary>Validation policy result; postal delivery remains explicitly unverified.</summary>
public sealed record AddressValidation(bool Valid, string Status, string Level, double Confidence,
    string PostalDeliveryStatus, AddressRecord? Address);
/// <summary>Canonical formatting when confidently matched, otherwise normalized input.</summary>
public sealed record AddressFormatting(string Formatted, bool Matched, string? GnafPid,
    AddressRecord? Address = null, double? Confidence = null, string? MatchType = null);

/// <summary>Candidate retrieval only; ranking and validation policy live in <see cref="AddressService"/>.</summary>
public interface IAddressRepository
{
    /// <summary>Looks up one canonical serving row by G-NAF PID.</summary>
    Task<AddressRecord?> GetByIdAsync(string id, CancellationToken cancellationToken);
    /// <summary>Returns a bounded candidate set; callers apply confidence policy.</summary>
    Task<IReadOnlyList<AddressRecord>> SearchAsync(string normalizedQuery, int limit, CancellationToken cancellationToken);
    /// <summary>Returns the version exposed by the stable API view.</summary>
    Task<string?> ActiveVersionAsync(CancellationToken cancellationToken);
    /// <summary>Returns the active release name and version for response provenance.</summary>
    Task<DatasetMetadata?> ActiveDatasetAsync(CancellationToken cancellationToken);
}

/// <summary>Creates one comparable spelling for user text while preserving the original response data.</summary>
public static partial class AddressNormalizer
{
    private static readonly Dictionary<string, string> Abbreviations = new(StringComparer.Ordinal)
    {
        ["ST"] = "STREET",
        ["RD"] = "ROAD",
        ["AVE"] = "AVENUE",
        ["AV"] = "AVENUE",
        ["DR"] = "DRIVE",
        ["CT"] = "COURT",
        ["CRES"] = "CRESCENT",
        ["PL"] = "PLACE",
        ["HWY"] = "HIGHWAY",
        ["U"] = "UNIT",
        ["F"] = "FLAT",
        ["APT"] = "APARTMENT",
        ["SE"] = "SUITE",
        ["STE"] = "SUITE",
        ["FL"] = "FLOOR",
        ["LVL"] = "LEVEL",
        ["TCE"] = "TERRACE",
        ["CCT"] = "CIRCUIT",
        ["CL"] = "CLOSE",
        ["BVD"] = "BOULEVARD",
        ["ESP"] = "ESPLANADE"
    };
    private static readonly Dictionary<string, string> SearchAbbreviations = new(StringComparer.Ordinal)
    {
        ["STREET"] = "ST",
        ["ROAD"] = "RD",
        ["AVENUE"] = "AVE",
        ["DRIVE"] = "DR",
        ["COURT"] = "CT",
        ["CRESCENT"] = "CRES",
        ["PLACE"] = "PL",
        ["HIGHWAY"] = "HWY",
        ["TERRACE"] = "TCE",
        ["CIRCUIT"] = "CCT",
        ["CLOSE"] = "CL",
        ["BOULEVARD"] = "BVD",
        ["ESPLANADE"] = "ESP"
    };
    private static readonly HashSet<string> StreetTypes = new(SearchAbbreviations.Keys, StringComparer.Ordinal);
    private static readonly HashSet<string> States = new(["ACT", "NSW", "NT", "QLD", "SA", "TAS", "VIC", "WA"], StringComparer.Ordinal);

    /// <summary>Expands common notation, removes punctuation/diacritics, and uppercases tokens for comparison.</summary>
    public static string Normalize(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        // Expand a leading 3/100 into UNIT 3 100 before punctuation is removed;
        // otherwise the slash loses its subaddress meaning.
        input = SlashUnit().Replace(input, "UNIT $1 $2");
        input = JoinedSubaddress().Replace(input, "$1 $2");
        var decomposed = input.Normalize(NormalizationForm.FormD);
        var ascii = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                ascii.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : ' ');
        var tokens = Whitespace().Split(ascii.ToString().Trim());
        return string.Join(" ", tokens.Select(t => Abbreviations.GetValueOrDefault(t, t)));
    }

    /// <summary>Produces an indexed short street-type spelling from already normalized text.</summary>
    public static string AbbreviateForSearch(string normalizedInput) =>
        string.Join(" ", normalizedInput.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => SearchAbbreviations.GetValueOrDefault(t, t)));

    /// <summary>Extracts a likely street name to constrain candidate retrieval.</summary>
    public static string? StreetHint(string normalizedInput)
    {
        var tokens = normalizedInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var typeIndex = Array.FindIndex(tokens, StreetTypes.Contains);
        if (typeIndex > 0)
        {
            var numberIndex = Array.FindLastIndex(tokens, typeIndex - 1,
                t => t.Length > 0 && char.IsDigit(t[0]));
            var start = numberIndex + 1;
            if (start < typeIndex)
                return string.Join(" ", tokens[start..typeIndex]);
        }
        var lastNumber = Array.FindLastIndex(tokens, t => t.Length > 0 && char.IsDigit(t[0]));
        if (lastNumber >= 0 && lastNumber + 1 < tokens.Length && tokens[lastNumber + 1].All(char.IsLetter))
            return tokens[lastNumber + 1];
        return tokens.Length > 0 && tokens[0].All(char.IsLetter) && !StreetTypes.Contains(tokens[0])
            ? tokens[0] : null;
    }

    /// <summary>Finds an unambiguous street number without mistaking a unit or postcode for one.</summary>
    public static string? HouseNumberHint(string normalizedInput)
    {
        var tokens = normalizedInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var typeIndex = Array.FindIndex(tokens, StreetTypes.Contains);
        var end = typeIndex > 0 ? typeIndex : tokens.Length;
        if (typeIndex < 0 && end > 0 && tokens[^1].Length == 4 && tokens[^1].All(char.IsDigit)) end--;
        var numbers = tokens[..end].Where(t => Regex.IsMatch(t, @"^\d+[A-Z]?$"))
            .Select(t => Regex.Match(t, @"^\d+").Value).ToArray();
        if (numbers.Length == 1) return numbers[0];
        // With a unit or floor, the final number before the street is the
        // house number. Without one, multiple numbers are ambiguous.
        return numbers.Length >= 2 && SubaddressParser.Parse(normalizedInput) is { HasFlat: true } or { HasLevel: true }
            ? numbers[^1] : null;
    }

    /// <summary>Finds the last Australian state or territory token in normalized input.</summary>
    public static string? StateHint(string normalizedInput) =>
        normalizedInput.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(States.Contains);

    /// <summary>Extracts the locality between the street type and state when both are present.</summary>
    public static string? LocalityHint(string normalizedInput)
    {
        var tokens = normalizedInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var stateIndex = Array.FindLastIndex(tokens, States.Contains);
        if (stateIndex < 0) return null;
        var streetTypeIndex = Array.FindLastIndex(tokens, stateIndex, StreetTypes.Contains);
        return streetTypeIndex >= 0 && streetTypeIndex + 1 < stateIndex
            ? string.Join(' ', tokens[(streetTypeIndex + 1)..stateIndex]) : null;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^\s*(\d+[A-Za-z]?)/(\d+[A-Za-z]?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SlashUnit();

    [GeneratedRegex(@"\b(APT|UNIT|FLAT|SUITE|SHOP|OFFICE|ROOM|LEVEL|LVL|FLOOR|FL|U|F|L)(\d+[A-Za-z]?)(?![A-Za-z])", RegexOptions.IgnoreCase)]
    private static partial Regex JoinedSubaddress();
}

/// <summary>Parsed flat/unit and floor details supplied by a caller.</summary>
public sealed record Subaddress(string Type, string Number, string LevelType, string LevelNumber)
{
    /// <summary>Whether the caller supplied a flat, unit, or equivalent number.</summary>
    public bool HasFlat => Number.Length > 0;
    /// <summary>Whether the caller supplied a recognised level type.</summary>
    public bool HasLevel => LevelType.Length > 0;
}

/// <summary>Recognises common unit and floor notation and maps types to G-NAF codes.</summary>
public static partial class SubaddressParser
{
    /// <summary>Parses common flat and floor forms after normalization.</summary>
    public static Subaddress Parse(string normalized)
    {
        var flat = Flat().Match(normalized);
        var level = Level().Match(normalized);
        var levelType = level.Success ? LevelType(level.Groups[1].Value) : "";
        var levelNumber = level.Success ? level.Groups[2].Value : "";
        // A bare "L 10" near an address can be a street fragment. Only keep
        // a non-generic level number when a house/street pattern follows it.
        if (levelType is not ("" or "L") && levelNumber.Length > 0 &&
            !Regex.IsMatch(normalized[(level.Index + level.Length)..], @"\b\d+[A-Z]?\s+[A-Z]{2,}\b"))
            levelNumber = "";
        return new(flat.Success ? FlatType(flat.Groups[1].Value) : "",
            flat.Success ? flat.Groups[2].Value : "",
            levelType, levelNumber);
    }

    /// <summary>Maps user-facing flat labels to the compact type codes used by G-NAF.</summary>
    public static string FlatType(string value) => value switch
    {
        "U" or "UNIT" => "UNIT",
        "F" or "FLAT" => "FLAT",
        "APT" or "APARTMENT" => "APT",
        "SE" or "STE" or "SUITE" => "SE",
        "SHOP" => "SHOP",
        "OFF" or "OFFC" or "OFFICE" => "OFFC",
        "RM" or "ROOM" => "ROOM",
        "STU" or "STUDIO" => "STU",
        "TNHS" or "TOWNHOUSE" => "TNHS",
        "VLLA" or "VILLA" => "VLLA",
        "PTHS" or "PENTHOUSE" => "PTHS",
        "FY" or "FCTY" or "FACTORY" => "FCTY",
        "WE" or "WHSE" or "WAREHOUSE" => "WHSE",
        "CARS" or "CARSPACE" => "CARS",
        "GRGE" or "GARAGE" => "GRGE",
        "HSE" or "HOUSE" => "HSE",
        "LOT" => "LOT",
        "SITE" => "SITE",
        _ => value
    };

    /// <summary>Maps floor labels and abbreviations to G-NAF level codes.</summary>
    public static string LevelType(string value) => value switch
    {
        "L" or "LVL" or "LEVEL" or "FL" or "FLOOR" => "L",
        "G" or "GROUND" => "G",
        "B" or "BASEMENT" => "B",
        "LG" or "LOWERGROUND" or "LOWER GROUND" => "LG",
        "UG" or "UPPERGROUND" or "UPPER GROUND" => "UG",
        "M" or "MEZZANINE" => "M",
        "SB" or "SUBBASEMENT" or "SUB BASEMENT" => "SB",
        _ => value
    };

    /// <summary>Allows common residential labels to match while keeping other flat types distinct.</summary>
    public static bool FlatTypesCompatible(string input, string candidate) =>
        // Unit, flat and apartment are common interchangeable descriptions;
        // shop, office and other G-NAF types remain distinct.
        input == candidate ||
        (input is "UNIT" or "FLAT" or "APT" && candidate is "UNIT" or "FLAT" or "APT");

    [GeneratedRegex(@"\b(UNIT|U|FLAT|F|APARTMENT|APT|SUITE|SE|STE|SHOP|OFFICE|OFF|OFFC|ROOM|RM|STUDIO|STU|TOWNHOUSE|TNHS|VILLA|VLLA|PENTHOUSE|PTHS|FACTORY|FCTY|FY|WAREHOUSE|WHSE|WE|CARSPACE|CARS|GARAGE|GRGE|HOUSE|HSE|LOT|SITE)\s+([A-Z]?\d+[A-Z]?)\b")]
    private static partial Regex Flat();

    [GeneratedRegex(@"\b(LOWER GROUND|UPPER GROUND|SUB BASEMENT|LEVEL|LVL|L|FLOOR|FL|GROUND|G|BASEMENT|B|LOWERGROUND|LG|UPPERGROUND|UG|MEZZANINE|M|SUBBASEMENT|SB)(?:\s+([A-Z]?\d+[A-Z]?))?\b")]
    private static partial Regex Level();
}

/// <summary>Applies suggestion ranking, match classification, and validation policy to repository candidates.</summary>
public sealed class AddressService(IAddressRepository repository)
{
    /// <summary>Retrieves a canonical address by its G-NAF PID.</summary>
    public Task<AddressRecord?> GetByIdAsync(string id, CancellationToken ct) => repository.GetByIdAsync(id, ct);

    /// <summary>Ranks repository candidates and removes duplicate display labels.</summary>
    public async Task<IReadOnlyList<Suggestion>> SuggestAsync(string input, int limit, CancellationToken ct)
    {
        var query = AddressNormalizer.Normalize(input);
        if (query.Length < 2) return [];
        var candidates = await repository.SearchAsync(query, Math.Clamp(limit, 1, 20), ct);
        // Alias queries may return the same canonical row more than once. Keep
        // the highest-scoring label once, while preserving its structured row.
        return candidates.Select(x => new Suggestion(x.GnafPid, x.FormattedAddress,
            ScoreCandidate(query, x), x)).OrderByDescending(x => x.Score)
            .GroupBy(x => x.Label, StringComparer.OrdinalIgnoreCase).Select(group => group.First())
            .Take(limit).ToArray();
    }

    /// <summary>Returns the best candidate and classification, including low-confidence partial matches.</summary>
    public async Task<AddressMatch> MatchAsync(string input, CancellationToken ct)
    {
        var query = AddressNormalizer.Normalize(input);
        if (query.Length < 2) return new(null, 0, "NO_MATCH");
        var candidates = await repository.SearchAsync(query, 20, ct);
        var winner = candidates.Select(x => new { Address = x, Score = ScoreCandidate(query, x) })
            .OrderByDescending(x => x.Score).FirstOrDefault();
        if (winner is null) return new(null, 0, "NO_MATCH");
        var requestedSubaddress = SubaddressParser.Parse(query);
        // A plausible building with the wrong or absent unit must remain
        // unconfirmed even when the street-level candidate looks close.
        var type = winner.Score < 0.8 && (requestedSubaddress.HasFlat || requestedSubaddress.HasLevel) &&
            AddressNormalizer.HouseNumberHint(query) == winner.Address.NumberFirst &&
            query.Contains(winner.Address.StreetName, StringComparison.Ordinal)
            ? "SUBADDRESS_UNCONFIRMED" :
            input.Trim().Equals(winner.Address.FormattedAddress, StringComparison.OrdinalIgnoreCase)
            ? "EXACT" : winner.Address.MatchText.Length > 0 && winner.Score >= 0.8 ? "ALIAS" :
            CanonicalAliases(query) == CanonicalAliases(AddressNormalizer.Normalize(winner.Address.SearchText)) ? "NORMALISED"
            : winner.Score >= 0.8 ? "FUZZY" : "PARTIAL";
        return new(winner.Address, winner.Score, type);
    }

    /// <summary>Accepts only scores at or above the policy threshold with no explicit component conflict.</summary>
    public async Task<AddressValidation> ValidateAsync(string input, CancellationToken ct)
    {
        var match = await MatchAsync(input, ct);
        // A threshold is a product policy, not a postal-delivery assertion.
        var valid = match.Address is not null && match.Confidence >= 0.8;
        var dataset = valid ? await repository.ActiveDatasetAsync(ct) : null;
        var status = valid ? dataset?.Name == "G-NAF" ? "GNAF_MATCHED" : "SAMPLE_MATCHED" :
            match.MatchType == "SUBADDRESS_UNCONFIRMED" ? "SUBADDRESS_UNCONFIRMED" : "NO_MATCH";
        return new(valid, status, valid ? "ADDRESS" : "NONE",
            match.Confidence, "POSTAL_DELIVERY_NOT_VERIFIED", valid ? match.Address : null);
    }

    /// <summary>Normalizes input without consulting a dataset.</summary>
    public static string Format(string input) => AddressNormalizer.Normalize(input);

    /// <summary>Returns the canonical G-NAF line only for a confident match.</summary>
    public async Task<AddressFormatting> FormatAsync(string input, CancellationToken ct)
    {
        var match = await MatchAsync(input, ct);
        return match.Address is not null && match.Confidence >= 0.8
            ? new(match.Address.FormattedAddress, true, match.Address.GnafPid, match.Address,
                match.Confidence, match.MatchType)
            : new(Format(input), false, null, MatchType: match.MatchType);
    }

    private static double ScoreCandidate(string query, AddressRecord candidate)
    {
        var requested = SubaddressParser.Parse(query);
        var candidateType = SubaddressParser.FlatType(candidate.FlatType.Length > 0 ? candidate.FlatType :
            candidate.FlatNumber.Length > 0 ? "UNIT" : "");
        var candidateFlat = candidate.FlatNumberPrefix + candidate.FlatNumber + candidate.FlatNumberSuffix;
        var candidateLevelType = SubaddressParser.LevelType(candidate.LevelType);
        var candidateLevel = candidate.LevelNumberPrefix + candidate.LevelNumber + candidate.LevelNumberSuffix;
        var candidateText = AddressNormalizer.Normalize(candidate.MatchText.Length > 0 ? candidate.MatchText : candidate.SearchText);
        // A complete text match also covers ranged street numbers (for example 296-298).
        // Their second number must not be mistaken for a different house number.
        if (CanonicalAliases(query) == CanonicalAliases(candidateText)) return 1;
        var score = Score(CanonicalAliases(query), CanonicalAliases(candidateText));
        if (candidate.BuildingName.Length > 0)
            score = Math.Max(score, Score(CanonicalAliases(query), CanonicalAliases(
                candidateText.Replace(AddressNormalizer.Normalize(candidate.BuildingName), "", StringComparison.Ordinal).Trim())));
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // A close street/locality match with a different house number is not address validation.
        var houseNumber = AddressNormalizer.HouseNumberHint(query);
        // Every component conflict is capped below the 0.8 acceptance floor.
        // Text similarity cannot override a conflicting unit, house, locality,
        // state or postcode supplied by the caller.
        if (!string.IsNullOrEmpty(candidate.NumberFirst) && houseNumber is not null && houseNumber != candidate.NumberFirst)
            score = Math.Min(score, 0.79);
        if (!string.IsNullOrEmpty(candidate.Postcode) && tokens.Any(t => t.Length == 4 && t.All(char.IsDigit)) &&
            !tokens.Contains(candidate.Postcode))
            score = Math.Min(score, 0.79);
        var state = AddressNormalizer.StateHint(query);
        if (state is not null && state != candidate.State)
            score = Math.Min(score, 0.79);
        var locality = AddressNormalizer.LocalityHint(query);
        var matchedLocality = AddressNormalizer.LocalityHint(candidateText);
        if (locality is not null && matchedLocality is not null && locality != matchedLocality)
            score = Math.Min(score, 0.79);
        if (requested.HasFlat && (requested.Number != candidateFlat ||
            !SubaddressParser.FlatTypesCompatible(requested.Type, candidateType)))
            score = Math.Min(score, 0.79);
        if (requested.HasLevel && (requested.LevelType != candidateLevelType ||
            requested.LevelNumber != candidateLevel))
            score = Math.Min(score, 0.79);
        if (!requested.HasFlat && candidateFlat.Length > 0) score = Math.Min(score, 0.79);
        if (!requested.HasLevel && candidateLevelType.Length > 0) score = Math.Min(score, 0.79);
        return score;
    }

    private static string CanonicalAliases(string value)
    {
        // These are notation equivalences for scoring, not edits to the
        // canonical components returned from G-NAF.
        value = Regex.Replace(value, @"\b(UNIT|FLAT|APARTMENT)\b", "UNIT");
        value = Regex.Replace(value, @"\b(LEVEL|FLOOR|L|FL)\b", "LEVEL");
        value = Regex.Replace(value, @"\b(LOWER GROUND|LG)\b", "LG");
        value = Regex.Replace(value, @"\b(UPPER GROUND|UG)\b", "UG");
        value = Regex.Replace(value, @"\b(SUB BASEMENT|SUBBASEMENT|SB)\b", "SB");
        value = Regex.Replace(value, @"\b(MEZZANINE|M)\b", "M");
        value = Regex.Replace(value, @"\b(GROUND|G)\b", "G");
        value = Regex.Replace(value, @"\b(BASEMENT|B)\b", "B");
        return value;
    }

    // Deterministic token overlap. Exact and normalized equality are 1.0.
    /// <summary>Returns deterministic token overlap in the range zero to one; it is not a probability.</summary>
    public static double Score(string normalizedInput, string normalizedCandidate)
    {
        if (normalizedInput == normalizedCandidate) return 1;
        var input = normalizedInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var candidate = normalizedCandidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (input.Length == 0 || candidate.Length == 0) return 0;
        var matching = input.Count(token => candidate.Any(c => c == token || (token.Length >= 3 && c.StartsWith(token, StringComparison.Ordinal))));
        return Math.Round((double)matching / Math.Max(input.Length, candidate.Length), 3);
    }
}
