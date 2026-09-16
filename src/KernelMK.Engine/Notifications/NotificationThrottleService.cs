using System.Collections.Concurrent;

namespace KernelMK.Engine.Notifications;

/// <summary>
/// Empêche l'envoi de notifications externes (email/Teams/webhook) en rafale pour un même job/événement :
/// une seule alerte part par fenêtre de temps glissante, même si l'événement se reproduit plusieurs fois
/// d'affilée (ex. un job qui échoue 5 fois de suite dans le même quart d'heure). Enregistré en Singleton
/// pour que l'état survive entre les appels du NotificationDispatcher, lui-même Scoped.
/// </summary>
public class NotificationThrottleService
{
    private readonly ConcurrentDictionary<string, DateTime> _lastSentAt = new();

    /// <summary>
    /// Retourne true si une alerte identique (même clé de regroupement) est déjà partie il y a moins de
    /// <paramref name="groupingWindow"/> et que celle-ci doit donc être supprimée ; false s'il s'agit de la
    /// première occurrence de la fenêtre (l'appelant doit alors envoyer la notification).
    /// </summary>
    public bool ShouldSuppress(string dedupeKey, TimeSpan groupingWindow)
    {
        var now = DateTime.UtcNow;

        var stored = _lastSentAt.AddOrUpdate(
            dedupeKey,
            addValueFactory: _ => now,
            updateValueFactory: (_, lastSent) => now - lastSent < groupingWindow ? lastSent : now);

        return stored != now;
    }
}
