using System.Text;
using KernelMK.Core;
using KernelMK.Core.StepConfigs;

namespace KernelMK.Engine.Edifact;

/// <summary>Analyse un message EDIFACT (COPARN/CODECO/COARRI/MANIFEST) et en extrait les données structurées.</summary>
public static class EdifactMessageParser
{
    public static ParsedEdifactMessage Parse(string raw)
    {
        var segments = Tokenize(raw);
        var result = new ParsedEdifactMessage();
        EdifactContainerInfo? current = null;

        foreach (var (tag, elements) in segments)
        {
            switch (tag)
            {
                case "UNB":
                    if (elements.Length > 1) result.SenderId = Comp(elements[1]).FirstOrDefault();
                    if (elements.Length > 2) result.ReceiverId = Comp(elements[2]).FirstOrDefault();
                    break;

                case "UNH":
                    if (elements.Length > 1)
                    {
                        var comps = Comp(elements[1]);
                        result.MessageTypeRaw = comps.FirstOrDefault();
                        result.MessageType = ParseMessageType(result.MessageTypeRaw);
                    }
                    break;

                case "BGM":
                    if (elements.Length > 1) result.DocumentNumber = Elem(elements[1]);
                    break;

                case "DTM":
                    if (elements.Length > 0)
                    {
                        var comps = Comp(elements[0]);
                        if (comps.Length > 1) result.DateTimeRaw = comps[1];
                    }
                    break;

                case "LOC":
                    // Le premier LOC rencontré est le lieu d'en-tête ; les suivants (position d'arrimage
                    // par conteneur) sont rattachés au conteneur courant.
                    if (elements.Length > 1)
                    {
                        var comps = Comp(elements[1]);
                        var code = comps.FirstOrDefault();
                        if (result.LocationCode is null)
                        {
                            result.LocationCode = code;
                        }
                        else if (current is not null)
                        {
                            current.StowagePosition = code;
                        }
                    }
                    break;

                case "TDT":
                    if (elements.Length > 1) result.VoyageNumber = Elem(elements[1]);
                    if (elements.Length > 5)
                    {
                        var comps = Comp(elements[5]);
                        result.VesselName = comps.Length > 3 ? comps[3] : comps.LastOrDefault(c => !string.IsNullOrEmpty(c));
                    }
                    break;

                case "CNI":
                    current = new EdifactContainerInfo();
                    result.Containers.Add(current);
                    break;

                case "EQD":
                    current ??= AddImplicitContainer(result);
                    if (elements.Length > 1) current.ContainerNumber = Elem(elements[1]);
                    if (elements.Length > 2) current.SizeType = Comp(elements[2]).FirstOrDefault() ?? "";
                    if (elements.Length > 4) current.FullEmpty = Elem(elements[4]) == "4" ? EdifactFullEmptyIndicator.Plein : EdifactFullEmptyIndicator.Vide;
                    break;

                case "MEA":
                    if (current is not null && elements.Length > 2)
                    {
                        var comps = Comp(elements[2]);
                        if (comps.Length > 1 && decimal.TryParse(comps[1], System.Globalization.CultureInfo.InvariantCulture, out var weight))
                        {
                            current.GrossWeightKg = weight;
                        }
                    }
                    break;

                case "SEL":
                    if (current is not null && elements.Length > 0)
                    {
                        var seal = Elem(elements[0]);
                        current.SealNumbersCsv = string.IsNullOrEmpty(current.SealNumbersCsv) ? seal : current.SealNumbersCsv + "," + seal;
                    }
                    break;

                case "FTX":
                    if (elements.Length > 3)
                    {
                        var comps = Comp(elements[3]);
                        if (comps.Length >= 2 && comps[0] == "MOUVEMENT" && current is not null
                            && Enum.TryParse<EdifactMovementCode>(comps[1], out var movement))
                        {
                            current.MovementCode = movement;
                        }
                        else
                        {
                            result.FreeText = string.Join(" ", comps);
                        }
                    }
                    break;

                case "UNT":
                    if (elements.Length > 0 && int.TryParse(Elem(elements[0]), out var segmentCount))
                    {
                        result.SegmentCount = segmentCount;
                    }
                    break;
            }
        }

        return result;
    }

    private static EdifactContainerInfo AddImplicitContainer(ParsedEdifactMessage result)
    {
        var c = new EdifactContainerInfo();
        result.Containers.Add(c);
        return c;
    }

    private static EdifactMessageType ParseMessageType(string? raw) => raw?.ToUpperInvariant() switch
    {
        "COPARN" => EdifactMessageType.Coparn,
        "CODECO" => EdifactMessageType.Codeco,
        "COARRI" => EdifactMessageType.Coarri,
        "CUSCAR" or "MANIFEST" => EdifactMessageType.Manifest,
        _ => EdifactMessageType.Coparn
    };

    // --- Tokenisation respectant le caractère d'échappement '?' ---

    private static List<(string Tag, string[] RawElements)> Tokenize(string raw)
    {
        var content = raw.TrimStart();
        if (content.StartsWith("UNA", StringComparison.Ordinal))
        {
            content = content.Length > 9 ? content[9..] : string.Empty;
        }

        var result = new List<(string, string[])>();
        foreach (var segmentText in SplitRespectingEscape(content, Edi.SegmentTerminator))
        {
            if (string.IsNullOrWhiteSpace(segmentText)) continue;
            var parts = SplitRespectingEscape(segmentText, Edi.ElementSeparator);
            if (parts.Length == 0) continue;
            var tag = Elem(parts[0]);
            var elements = parts.Skip(1).ToArray();
            result.Add((tag, elements));
        }
        return result;
    }

    /// <summary>Extrait la valeur scalaire (non composite) d'un élément brut.</summary>
    private static string Elem(string raw) => Unescape(raw);

    /// <summary>Découpe un élément composite en sous-composants, chacun désescapé.</summary>
    private static string[] Comp(string raw) => SplitRespectingEscape(raw, Edi.ComponentSeparator).Select(Unescape).ToArray();

    private static string[] SplitRespectingEscape(string s, char delimiter)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == Edi.ReleaseChar && i + 1 < s.Length)
            {
                current.Append(s[i]).Append(s[i + 1]);
                i++;
            }
            else if (s[i] == delimiter)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(s[i]);
            }
        }
        parts.Add(current.ToString());
        return parts.ToArray();
    }

    private static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == Edi.ReleaseChar && i + 1 < s.Length)
            {
                sb.Append(s[i + 1]);
                i++;
            }
            else
            {
                sb.Append(s[i]);
            }
        }
        return sb.ToString();
    }
}
