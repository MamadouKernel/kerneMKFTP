using System.IO.Enumeration;

namespace KernelMK.Core;

/// <summary>
/// Filtre de nom de fichier supportant plusieurs motifs séparés par ';' (ex: "*.txt;*.csv;*.xml"),
/// insensible à la casse. Un filtre vide ou null accepte tous les fichiers.
/// </summary>
public static class FilePatternMatcher
{
    public static bool IsMatch(string fileName, string? filterCsv)
    {
        if (string.IsNullOrWhiteSpace(filterCsv)) return true;

        var patterns = filterCsv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (patterns.Length == 0) return true;

        return patterns.Any(p => FileSystemName.MatchesSimpleExpression(p, fileName, ignoreCase: true));
    }
}
