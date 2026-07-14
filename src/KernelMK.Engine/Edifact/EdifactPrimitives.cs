using System.Text;

namespace KernelMK.Engine.Edifact;

/// <summary>
/// Primitives bas niveau EDIFACT : séparateurs standards (élément '+', composant ':', terminateur
/// de segment ''', caractère d'échappement '?'), échappement des valeurs, et construction de segments.
/// </summary>
public static class Edi
{
    public const char ElementSeparator = '+';
    public const char ComponentSeparator = ':';
    public const char SegmentTerminator = '\'';
    public const char ReleaseChar = '?';

    /// <summary>Échappe une valeur atomique avant insertion dans un segment (à appeler UNE fois par valeur, jamais sur un composite déjà assemblé).</summary>
    public static string Val(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is SegmentTerminator or ElementSeparator or ComponentSeparator or ReleaseChar)
            {
                sb.Append(ReleaseChar);
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Assemble un élément composite (sous-composants séparés par ':'), en échappant chaque partie.</summary>
    public static string Composite(params string?[] parts) => string.Join(ComponentSeparator, parts.Select(Val));
}

/// <summary>Accumulateur de segments EDIFACT — un segment par ligne logique, terminé par '.</summary>
public class EdifactSegmentWriter
{
    private readonly List<string> _segments = new();

    /// <summary>Ajoute un segment. Les éléments doivent déjà être échappés via <see cref="Edi.Val"/> ou <see cref="Edi.Composite"/>.</summary>
    public EdifactSegmentWriter Add(string tag, params string[] elements)
    {
        var line = elements.Length == 0
            ? tag + Edi.SegmentTerminator
            : tag + Edi.ElementSeparator + string.Join(Edi.ElementSeparator, elements) + Edi.SegmentTerminator;
        _segments.Add(line);
        return this;
    }

    public int Count => _segments.Count;

    public string Build() => string.Join(string.Empty, _segments);
}
