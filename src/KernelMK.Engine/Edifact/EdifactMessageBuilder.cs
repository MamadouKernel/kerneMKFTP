using System.Globalization;
using KernelMK.Core;
using KernelMK.Core.StepConfigs;

namespace KernelMK.Engine.Edifact;

/// <summary>
/// Génère un message EDIFACT (COPARN/CODECO/COARRI/MANIFEST) structurellement conforme (enveloppe
/// UNB/UNH/UNT/UNZ standard, segments métier BGM/DTM/NAD/LOC/TDT/EQD/MEA/FTX). Les qualificatifs de
/// code (ex. codes de lieu, codes fonction) utilisent des valeurs par défaut plausibles — à ajuster
/// selon le guide d'implémentation exact du partenaire (armateur, terminal, douane) si nécessaire.
/// </summary>
public static class EdifactMessageBuilder
{
    public static string Build(EdifactStepConfig config, DateTime timestampUtc)
    {
        var interchangeRef = timestampUtc.ToString("yyMMddHHmmss");
        var messageRef = "1";
        var writer = new EdifactSegmentWriter();

        // --- Section en-tête (commune aux 4 types de message) ---
        writer.Add("UNH",
            Edi.Val(messageRef),
            Edi.Composite(MessageTypeCode(config.MessageType), "D", "95B", "UN"));

        writer.Add("BGM",
            Edi.Val(DocumentFunctionCode(config.MessageType)),
            Edi.Val(config.DocumentNumber),
            Edi.Val("9")); // 9 = message original (pas une modification/annulation)

        writer.Add("DTM", Edi.Composite("137", timestampUtc.ToString("yyyyMMddHHmm"), "203"));

        if (!string.IsNullOrWhiteSpace(config.SenderId))
        {
            writer.Add("NAD", Edi.Val("CA"), Edi.Composite(config.SenderId, string.Empty, "160")); // CA = carrier/transporteur
        }
        if (!string.IsNullOrWhiteSpace(config.ReceiverId))
        {
            writer.Add("NAD", Edi.Val("CN"), Edi.Composite(config.ReceiverId, string.Empty, "160")); // CN = destinataire/consignee
        }

        if (!string.IsNullOrWhiteSpace(config.LocationCode))
        {
            writer.Add("LOC", Edi.Val(LocationQualifier(config.MessageType)), Edi.Composite(config.LocationCode, "139", "6"));
        }

        if (!string.IsNullOrWhiteSpace(config.VesselName) || !string.IsNullOrWhiteSpace(config.VoyageNumber))
        {
            writer.Add("TDT",
                Edi.Val("20"), // 20 = transport principal
                Edi.Val(config.VoyageNumber ?? string.Empty),
                Edi.Val("1"), // mode de transport 1 = maritime
                string.Empty,
                string.Empty,
                Edi.Composite(string.Empty, string.Empty, string.Empty, config.VesselName ?? string.Empty));
        }

        writer.Add("UNS", Edi.Val("D")); // séparation en-tête / détail

        // --- Section détail : une ligne par conteneur ---
        var containerIndex = 0;
        foreach (var container in config.Containers)
        {
            containerIndex++;
            writer.Add("CNI", Edi.Val(containerIndex.ToString()), Edi.Val(config.DocumentNumber));

            writer.Add("EQD",
                Edi.Val("CN"),
                Edi.Val(container.ContainerNumber),
                Edi.Composite(container.SizeType, "102", "5"),
                string.Empty,
                Edi.Val(container.FullEmpty == EdifactFullEmptyIndicator.Plein ? "4" : "5"));

            if (container.MovementCode is not null)
            {
                writer.Add("FTX", Edi.Val("AAA"), string.Empty, string.Empty,
                    Edi.Composite("MOUVEMENT", container.MovementCode.ToString()));
            }

            if (container.GrossWeightKg is not null)
            {
                writer.Add("MEA", Edi.Val("WT"), Edi.Val("G"), Edi.Composite("KGM", container.GrossWeightKg.Value.ToString("0.###", CultureInfo.InvariantCulture)));
            }

            if (!string.IsNullOrWhiteSpace(container.SealNumbersCsv))
            {
                foreach (var seal in container.SealNumbersCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    writer.Add("SEL", Edi.Val(seal));
                }
            }

            if (!string.IsNullOrWhiteSpace(container.StowagePosition))
            {
                writer.Add("LOC", Edi.Val("147"), Edi.Composite(container.StowagePosition, string.Empty, string.Empty));
            }
        }

        if (!string.IsNullOrWhiteSpace(config.FreeText))
        {
            writer.Add("FTX", Edi.Val("GEN"), string.Empty, string.Empty, Edi.Composite(config.FreeText));
        }

        // Le nombre de segments UNT compte tous les segments du message, UNH et UNT inclus.
        var segmentCountForUnt = writer.Count + 1;
        writer.Add("UNT", Edi.Val(segmentCountForUnt.ToString()), Edi.Val(messageRef));

        var body = writer.Build();

        var una = "UNA:+.? '";
        var unb = $"UNB+UNOC:3+{Edi.Val(config.SenderId)}+{Edi.Val(config.ReceiverId)}+" +
                  $"{timestampUtc:yyMMdd}:{timestampUtc:HHmm}+{Edi.Val(interchangeRef)}'";
        var unz = $"UNZ+1+{Edi.Val(interchangeRef)}'";

        return una + unb + body + unz;
    }

    private static string MessageTypeCode(EdifactMessageType type) => type switch
    {
        EdifactMessageType.Coparn => "COPARN",
        EdifactMessageType.Codeco => "CODECO",
        EdifactMessageType.Coarri => "COARRI",
        EdifactMessageType.Manifest => "CUSCAR", // désignation EDIFACT standard du manifeste de cargaison douanier
        _ => throw new NotSupportedException($"Type de message EDIFACT non supporté : {type}")
    };

    private static string DocumentFunctionCode(EdifactMessageType type) => type switch
    {
        EdifactMessageType.Coparn => "610", // avis de réservation de conteneur
        EdifactMessageType.Codeco => "613", // ordre de mouvement de conteneur
        EdifactMessageType.Coarri => "615", // rapport de déchargement/chargement
        EdifactMessageType.Manifest => "785", // manifeste de cargaison
        _ => "1"
    };

    private static string LocationQualifier(EdifactMessageType type) => type switch
    {
        EdifactMessageType.Coparn => "88",  // port/lieu de chargement prévu
        EdifactMessageType.Codeco => "165", // dépôt/terminal
        EdifactMessageType.Coarri => "9",   // port d'escale
        EdifactMessageType.Manifest => "147", // port de déchargement
        _ => "9"
    };
}
