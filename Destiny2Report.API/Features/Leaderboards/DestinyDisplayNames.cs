using System.Globalization;
using System.Text;

namespace Destiny2Report.API.Features.Leaderboards;

public static class DestinyDisplayNames
{
    private static readonly IReadOnlyDictionary<string, string> PatrolDestinations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Arcadian Valley"] = "Nessus",
        ["Echo Mesa"] = "IO",
        ["Hellas Basin"] = "Mars",
        ["New Pacific Arcology"] = "Titan",
        ["Nessus"] = "Nessus",
        ["IO"] = "IO",
        ["Mars"] = "Mars",
        ["Titan"] = "Titan",
        ["The Pale Heart"] = "The Pale Heart",
        ["European Dead Zone"] = "European Dead Zone",
        ["The Moon"] = "The Moon",
        ["Europa"] = "Europa",
        ["Neomuna"] = "Neomuna",
        ["Kepler"] = "Kepler",
        ["The Dreaming City"] = "The Dreaming City",
        ["The Tangled Shore"] = "The Tangled Shore",
        ["Savathûn's Throne World"] = "Savathûn's Throne World",
        ["Cosmodrome"] = "Cosmodrome",
        ["Mercury"] = "Mercury",
        ["Tharsis Expanse"] = "Tharsis Expanse",
        ["Eternity"] = "Eternity"
    };

    public static bool TryCanonicalPatrolDestination(string? name, out string canonical) => PatrolDestinations.TryGetValue(name ?? "", out canonical!);

    public static string HumanizeIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var builder = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (index > 0 && char.IsUpper(character) && char.IsLower(value[index - 1])) builder.Append(' ');
            builder.Append(character);
        }
        return builder.ToString();
    }

    public static string Slug(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingDash = false;
        foreach (var character in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(character))
            {
                if (pendingDash && builder.Length > 0) builder.Append('-');
                builder.Append(char.ToLowerInvariant(character));
                pendingDash = false;
            }
            else pendingDash = true;
        }
        return builder.ToString();
    }
}
