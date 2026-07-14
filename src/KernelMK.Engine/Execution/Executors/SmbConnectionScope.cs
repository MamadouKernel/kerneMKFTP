using System.Runtime.InteropServices;

namespace KernelMK.Engine.Execution.Executors;

/// <summary>
/// Établit une session SMB authentifiée avec des identifiants explicites (différents du compte
/// qui exécute le service), via l'API Win32 WNetAddConnection2 — l'équivalent programmatique de
/// "net use \\serveur\partage /user:compte motdepasse". La session est libérée à la fin (Dispose).
/// </summary>
public sealed class SmbConnectionScope : IDisposable
{
    private const int RESOURCETYPE_DISK = 0x00000001;
    private const int CONNECT_TEMPORARY = 0x00000004;

    private const int NO_ERROR = 0;
    private const int ERROR_ALREADY_ASSIGNED = 85;
    private const int ERROR_INVALID_PASSWORD = 86;
    private const int ERROR_SESSION_CREDENTIAL_CONFLICT = 1219;
    private const int ERROR_LOGON_FAILURE = 1326;

    [StructLayout(LayoutKind.Sequential)]
    private struct NETRESOURCE
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpLocalName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpRemoteName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpComment;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NETRESOURCE lpNetResource, string? lpPassword, string? lpUsername, int dwFlags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string lpName, int dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fForce);

    private readonly string? _connectedShareRoot;

    private SmbConnectionScope(string? connectedShareRoot)
    {
        _connectedShareRoot = connectedShareRoot;
    }

    /// <summary>
    /// Ouvre une session authentifiée vers la racine du partage UNC (\\serveur\partage) si des
    /// identifiants sont fournis. Sans identifiants, ne fait rien (l'identité du processus est utilisée,
    /// comme pour un lecteur déjà mappé).
    /// </summary>
    public static SmbConnectionScope Connect(string uncPath, string? username, string? password)
    {
        if (string.IsNullOrEmpty(username) || !uncPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return new SmbConnectionScope(null);
        }

        var shareRoot = GetShareRoot(uncPath);

        var resource = new NETRESOURCE
        {
            dwType = RESOURCETYPE_DISK,
            lpRemoteName = shareRoot
        };

        var result = WNetAddConnection2(ref resource, password, username, CONNECT_TEMPORARY);

        if (result == ERROR_ALREADY_ASSIGNED)
        {
            // Une connexion existe déjà vers ce partage (potentiellement avec un autre compte) ;
            // on la libère puis on retente une fois avec les identifiants demandés.
            WNetCancelConnection2(shareRoot, 0, true);
            result = WNetAddConnection2(ref resource, password, username, CONNECT_TEMPORARY);
        }

        if (result != NO_ERROR)
        {
            var message = result switch
            {
                ERROR_LOGON_FAILURE or ERROR_INVALID_PASSWORD => $"Authentification refusée par le partage {shareRoot} (identifiants incorrects).",
                ERROR_SESSION_CREDENTIAL_CONFLICT => $"Windows n'autorise qu'un seul compte à la fois par serveur SMB ({shareRoot}) : " +
                    "une session avec des identifiants différents est déjà ouverte vers ce serveur depuis cette machine.",
                _ => $"Impossible d'établir la connexion authentifiée vers {shareRoot} (code Win32 {result})."
            };
            throw new IOException(message);
        }

        return new SmbConnectionScope(shareRoot);
    }

    private static string GetShareRoot(string uncPath)
    {
        var parts = uncPath.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new ArgumentException($"Chemin UNC invalide (attendu \\\\serveur\\partage\\...) : {uncPath}");
        }
        return $@"\\{parts[0]}\{parts[1]}";
    }

    public void Dispose()
    {
        if (_connectedShareRoot is not null)
        {
            WNetCancelConnection2(_connectedShareRoot, 0, true);
        }
    }
}
