namespace KernelMK.Core.Entities;

public enum NotificationChannel
{
    Email,
    Webhook,
    Teams
}

public class NotificationRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public Job? Job { get; set; }

    public NotificationEvent Event { get; set; }
    public NotificationChannel Channel { get; set; } = NotificationChannel.Email;

    public string? RecipientsCsv { get; set; }
    public string? WebhookUrl { get; set; }
    public bool Enabled { get; set; } = true;
}
