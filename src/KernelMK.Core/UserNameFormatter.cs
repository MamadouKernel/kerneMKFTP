namespace KernelMK.Core;

/// <summary>
/// Utilitaire de formatage et d'affichage compact des identités utilisateurs pour l'interface portuaire.
/// </summary>
public static class UserNameFormatter
{
    /// <summary>
    /// Formate un nom complet pour un affichage compact et équilibré dans l'interface (ex: barre supérieure, barre latérale).
    /// Si le nom comporte plusieurs prénoms ou noms (ex: "Mamadou Lamine Cheikh KONATE"),
    /// simplifie intelligemment en "Premier Prénom + Dernier Nom" ("Mamadou KONATE").
    /// Si le résultat dépasse encore la limite de caractères, applique une troncature propre avec points de suspension.
    /// </summary>
    public static string FormatShortName(string? fullName, int maxChars = 20)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return "Utilisateur";
        var trimmed = fullName.Trim();

        if (trimmed.Length <= maxChars) return trimmed;

        // Si c'est une adresse email, isoler la partie locale avant l'arobase
        if (trimmed.Contains('@'))
        {
            var prefix = trimmed.Split('@')[0];
            return prefix.Length <= maxChars ? prefix : prefix[..(maxChars - 1)] + "…";
        }

        // Si composé de plusieurs mots (ex: prénom composé + noms multiples)
        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 2)
        {
            var simplified = $"{parts[0]} {parts[^1]}";
            if (simplified.Length <= maxChars)
            {
                return simplified;
            }
            return simplified[..(maxChars - 1)].TrimEnd() + "…";
        }

        return trimmed[..(maxChars - 1)].TrimEnd() + "…";
    }

    /// <summary>
    /// Génère les initiales professionnelles :
    /// Première lettre du premier mot + première lettre du dernier mot (ex: "Mamadou Lamine KONATE" => "MK").
    /// </summary>
    public static string GetInitials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "U";
        var trimmed = name.Trim();

        if (trimmed.Contains('@'))
        {
            var prefix = trimmed.Split('@')[0];
            return char.ToUpperInvariant(prefix[0]).ToString();
        }

        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            var first = char.ToUpperInvariant(parts[0][0]);
            var last = char.ToUpperInvariant(parts[^1][0]);
            return $"{first}{last}";
        }

        return char.ToUpperInvariant(trimmed[0]).ToString();
    }
}
