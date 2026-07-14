namespace KernelMK.Core.StepConfigs;

/// <summary>
/// Configuration d'une étape "Message EDIFACT" : génère ou analyse un message COPARN, CODECO,
/// COARRI ou MANIFEST (UN/EDIFACT). Les segments produits/lus (UNB, UNH, BGM, DTM, NAD, LOC, TDT,
/// EQD, CNI, MEA, FTX, UNT, UNZ) suivent la structure standard EDIFACT — la disposition exacte des
/// codes (UN/LOCODE, types de conteneur ISO 6346...) peut nécessiter un ajustement selon le guide
/// d'implémentation propre à chaque partenaire (armateur, terminal, douane).
/// </summary>
public class EdifactStepConfig
{
    public EdifactOperation Operation { get; set; } = EdifactOperation.Generer;
    public EdifactMessageType MessageType { get; set; } = EdifactMessageType.Coparn;

    // --- Génération : fichier de sortie et informations du message ---
    public string? OutputPath { get; set; }

    /// <summary>Identifiant de l'expéditeur (UNB, ex. code SCAC de l'armateur).</summary>
    public string SenderId { get; set; } = string.Empty;
    /// <summary>Identifiant du destinataire (UNB, ex. code du terminal/dépôt).</summary>
    public string ReceiverId { get; set; } = string.Empty;
    /// <summary>Numéro/référence du document (BGM), ex. numéro de booking, de rapport d'escale...</summary>
    public string DocumentNumber { get; set; } = string.Empty;

    /// <summary>Nom du navire (TDT) — utilisé pour COARRI, optionnel pour COPARN/CODECO.</summary>
    public string? VesselName { get; set; }
    /// <summary>Numéro de voyage (TDT).</summary>
    public string? VoyageNumber { get; set; }
    /// <summary>Code lieu UN/LOCODE (LOC) : port ou dépôt concerné.</summary>
    public string? LocationCode { get; set; }

    /// <summary>Texte libre additionnel (FTX).</summary>
    public string? FreeText { get; set; }

    public List<EdifactContainerInfo> Containers { get; set; } = new();

    // --- Analyse : fichier ou contenu source, et où écrire le résultat structuré (JSON) ---
    public string? InputPath { get; set; }
    public string? InputContent { get; set; }
    public string? ParsedOutputPath { get; set; }
}

public class EdifactContainerInfo
{
    /// <summary>Numéro de conteneur (norme ISO 6346, ex. MSCU1234567).</summary>
    public string ContainerNumber { get; set; } = string.Empty;
    /// <summary>Code taille/type ISO 6346 (ex. 22G1 = 20 pieds, general purpose).</summary>
    public string SizeType { get; set; } = string.Empty;
    public EdifactFullEmptyIndicator FullEmpty { get; set; } = EdifactFullEmptyIndicator.Plein;
    public EdifactMovementCode? MovementCode { get; set; }
    public decimal? GrossWeightKg { get; set; }
    /// <summary>Numéros de scellés séparés par des virgules.</summary>
    public string? SealNumbersCsv { get; set; }
    /// <summary>Position d'arrimage à bord (COARRI), ex. "0610804".</summary>
    public string? StowagePosition { get; set; }
}
