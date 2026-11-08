namespace TrustVault.Api.Models;

public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string UserId { get; set; } = string.Empty;

    public Guid DocumentId { get; set; }

    public string Action { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public string Details { get; set; } = string.Empty;
}
