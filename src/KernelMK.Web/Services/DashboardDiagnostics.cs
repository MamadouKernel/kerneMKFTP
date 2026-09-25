namespace KernelMK.Web.Services;

public static class DashboardDiagnostics
{
    public static SideClassification ClassifySide(string? errorOutput)
    {
        if (string.IsNullOrWhiteSpace(errorOutput))
            return new(AnomalySide.Indetermine, "Indéterminé", "bg-slate-100 text-slate-600", "❓");

        var err = errorOutput;

        // Faux négatif FTP : le serveur a confirmé "226 Transfer complete" malgré le statut Échec — transfert OK.
        if (err.Contains("226", StringComparison.OrdinalIgnoreCase) &&
            err.Contains("Transfer complete", StringComparison.OrdinalIgnoreCase) &&
            err.Contains("statut : Failed", StringComparison.OrdinalIgnoreCase))
            return new(AnomalySide.FauxPositif, "Faux positif (transfert OK)", "bg-emerald-100 text-emerald-700", "✓");

        // Côté CIT : infrastructure locale (lecteur réseau, dossier local, fichier verrouillé, partage SMB local).
        if (err.Contains("DirectoryNotFoundException", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("FileNotFoundException", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("Could not find a part of the path", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("used by another process", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("being used", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("Authentification refusée par le partage", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("SmbConnectionScope", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("clé privée SSH est configuré", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("incompatible avec l'authentification SMB", StringComparison.OrdinalIgnoreCase))
            return new(AnomalySide.Cit, "Côté CIT (infra locale)", "bg-orange-100 text-orange-700", "🏭");

        // Côté partenaire/armateur : le serveur distant a explicitement rejeté la connexion ou le chemin.
        if (err.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("530", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("SshAuthenticationException", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("550", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("SftpPathNotFoundException", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("No such file", StringComparison.OrdinalIgnoreCase))
            return new(AnomalySide.Armateur, "Côté partenaire", "bg-amber-100 text-amber-700", "🚢");

        // Indéterminé : timeout/réseau/pare-feu — peut venir des deux côtés, nécessite investigation.
        if (err.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("TimeoutException", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("A task was canceled", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("data connection", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("PASV", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("PROT P", StringComparison.OrdinalIgnoreCase))
            return new(AnomalySide.Indetermine, "Réseau (indéterminé)", "bg-slate-100 text-slate-600", "🌐");

        return new(AnomalySide.Indetermine, "Indéterminé", "bg-slate-100 text-slate-600", "❓");
    }

}
