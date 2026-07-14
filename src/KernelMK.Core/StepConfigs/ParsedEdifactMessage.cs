namespace KernelMK.Core.StepConfigs;

/// <summary>Résultat structuré de l'analyse d'un message EDIFACT reçu (COPARN/CODECO/COARRI/MANIFEST).</summary>
public class ParsedEdifactMessage
{
    public EdifactMessageType MessageType { get; set; }
    public string? MessageTypeRaw { get; set; }
    public string? SenderId { get; set; }
    public string? ReceiverId { get; set; }
    public string? DocumentNumber { get; set; }
    public string? DateTimeRaw { get; set; }
    public string? VesselName { get; set; }
    public string? VoyageNumber { get; set; }
    public string? LocationCode { get; set; }
    public string? FreeText { get; set; }
    public List<EdifactContainerInfo> Containers { get; set; } = new();
    public int SegmentCount { get; set; }
}
