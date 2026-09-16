namespace KernelMK.Core.Entities;

/// <summary>
/// Trace persistée d'une alerte proactive (Job manquant, Anomalie de durée) — les événements classiques
/// (Succès/Échec/Timeout) restent visibles via JobExecution et ne sont pas dupliqués ici. Sert uniquement
/// à rendre ces deux nouvelles alertes visibles sur le tableau de bord, en plus de l'envoi email/Teams/webhook.
/// </summary>
public class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? JobId { get; set; }
    public string JobName { get; set; } = string.Empty;
    public NotificationEvent Event { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
