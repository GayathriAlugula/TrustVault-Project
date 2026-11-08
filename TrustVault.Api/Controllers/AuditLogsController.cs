
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TrustVault.Api.Data;

namespace TrustVault.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin")]
public class AuditLogsController : ControllerBase
{
    private readonly AppDbContext _db;

    public AuditLogsController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> GetAuditLogs()
    {
        var logs = await _db.AuditLogs
            .AsNoTracking()
            .OrderByDescending(log => log.Timestamp)
            .Take(200)
            .Select(log => new
            {
                log.Id,
                log.UserId,
                log.DocumentId,
                log.Action,
                log.Status,
                log.Timestamp,
                log.Details
            })
            .ToListAsync();

        return Ok(logs);
    }
}
