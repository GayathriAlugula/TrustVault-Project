
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TrustVault.Api.Models;
using TrustVault.Api.Data;

namespace TrustVault.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DocumentsController : ControllerBase
{
    private readonly string _uploadPath;
    private readonly string _dataPath;
    private readonly UserManager<AppUser> _userManager;
    private readonly AppDbContext _db;

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public DocumentsController(
        IWebHostEnvironment environment,
        UserManager<AppUser> userManager,
        AppDbContext db)
    {
        _userManager = userManager;
        _db = db;

        _uploadPath = Path.Combine(environment.ContentRootPath, "Uploads");
        _dataPath = Path.Combine(environment.ContentRootPath, "Data", "documents.json");

        Directory.CreateDirectory(_uploadPath);
        Directory.CreateDirectory(Path.GetDirectoryName(_dataPath)!);
    }

    private string? CurrentUserId =>
        User.FindFirstValue(ClaimTypes.NameIdentifier);

    private async Task LogAsync(
        string userId,
        Guid documentId,
        string action,
        string status,
        string details = "")
    {
        var log = new AuditLog
        {
            UserId = userId,
            DocumentId = documentId,
            Action = action,
            Status = status,
            Timestamp = DateTime.UtcNow,
            Details = details
        };

        _db.AuditLogs.Add(log);
        await _db.SaveChangesAsync();
    }

    private async Task<List<Document>> ReadDocumentsAsync()
    {
        if (!System.IO.File.Exists(_dataPath))
            return new List<Document>();

        var json = await System.IO.File.ReadAllTextAsync(_dataPath);

        return JsonSerializer.Deserialize<List<Document>>(json, JsonOptions)
               ?? new List<Document>();
    }

    private async Task SaveDocumentsAsync(List<Document> documents)
    {
        var json = JsonSerializer.Serialize(documents, JsonOptions);
        await System.IO.File.WriteAllTextAsync(_dataPath, json);
    }

    private object MapDocument(Document d, string userId)
    {
        var isOwner = d.OwnerId == userId;

        return new
        {
            d.Id,
            d.FileName,
            d.FileSize,
            d.UploadedAt,
            d.OwnerId,
            IsOwner = isOwner,
            Permission = isOwner
                ? "Owner"
                : d.SharedWith?.FirstOrDefault(s => s.UserId == userId)?.Permission,
            SharedWith = isOwner ? d.SharedWith : null
        };
    }

    [HttpPost("upload")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> Upload(IFormFile file)
    {
        var ownerId = CurrentUserId;

        if (string.IsNullOrEmpty(ownerId))
            return Unauthorized();

        if (file == null || file.Length == 0 || file.Length > 10 * 1024 * 1024)
            return BadRequest("Invalid file or file exceeds 10 MB.");

        if (!Path.GetExtension(file.FileName).Equals(
                ".pdf", StringComparison.OrdinalIgnoreCase))
            return BadRequest("Only PDF files are allowed.");

        await using var input = file.OpenReadStream();
        var header = new byte[5];
        var bytesRead = await input.ReadAsync(header);

        if (bytesRead != 5 ||
            System.Text.Encoding.ASCII.GetString(header) != "%PDF-")
            return BadRequest("Invalid PDF file.");

        input.Position = 0;

        var id = Guid.NewGuid();
        var storedFileName = $"{id:N}.pdf";
        var filePath = Path.Combine(_uploadPath, storedFileName);

        try
        {
            await using (var output = new FileStream(
                filePath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, true))
            {
                await input.CopyToAsync(output);
            }

            var document = new Document
            {
                Id = id,
                FileName = Path.GetFileName(file.FileName),
                StoredFileName = storedFileName,
                ContentType = "application/pdf",
                FileSize = file.Length,
                UploadedAt = DateTime.UtcNow,
                OwnerId = ownerId
            };

            await FileLock.WaitAsync();
            try
            {
                var documents = await ReadDocumentsAsync();
                documents.Add(document);
                await SaveDocumentsAsync(documents);
            }
            finally
            {
                FileLock.Release();
            }

            await LogAsync(ownerId, id, "Upload", "Success", document.FileName);

            return Ok(new
            {
                message = "Document uploaded successfully.",
                document.Id,
                document.FileName,
                document.OwnerId
            });
        }
        catch
        {
            if (System.IO.File.Exists(filePath))
                System.IO.File.Delete(filePath);

            throw;
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetDocuments()
    {
        var userId = CurrentUserId;

        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        await FileLock.WaitAsync();
        try
        {
            var documents = await ReadDocumentsAsync();

            var results = documents
                .Where(d => d.OwnerId == userId ||
                    (d.SharedWith?.Any(s => s.UserId == userId) ?? false))
                .Select(d => MapDocument(d, userId))
                .ToList();

            await LogAsync(userId, Guid.Empty, "ListDocuments", "Success");

            return Ok(results);
        }
        finally
        {
            FileLock.Release();
        }
    }

    [HttpGet("search")]
    public async Task<IActionResult> SearchDocuments([FromQuery] string query)
    {
        var userId = CurrentUserId;

        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        await FileLock.WaitAsync();
        try
        {
            var documents = await ReadDocumentsAsync();

            var results = documents
                .Where(d =>
                    (d.OwnerId == userId ||
                     (d.SharedWith?.Any(s => s.UserId == userId) ?? false)) &&
                    d.FileName.Contains(
                        query ?? "", StringComparison.OrdinalIgnoreCase))
                .Select(d => MapDocument(d, userId))
                .ToList();

            await LogAsync(userId, Guid.Empty, "Search", "Success", query ?? "");

            return Ok(results);
        }
        finally
        {
            FileLock.Release();
        }
    }

    [HttpPost("{id:guid}/share")]
    public async Task<IActionResult> ShareDocument(
        Guid id,
        [FromBody] ShareDocumentRequest request)
    {
        var ownerId = CurrentUserId;

        if (string.IsNullOrEmpty(ownerId))
            return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.Email))
            return BadRequest("Email is required.");

        var permission = request.Permission?.Trim();

        if (permission == null ||
            (!permission.Equals("Read", StringComparison.OrdinalIgnoreCase) &&
             !permission.Equals("Edit", StringComparison.OrdinalIgnoreCase)))
            return BadRequest("Permission must be Read or Edit.");

        var targetUser = await _userManager.FindByEmailAsync(request.Email.Trim());

        if (targetUser == null)
            return NotFound("User with this email was not found.");

        if (targetUser.Id == ownerId)
            return BadRequest("You cannot share a document with yourself.");

        await FileLock.WaitAsync();
        try
        {
            var documents = await ReadDocumentsAsync();

            var document = documents.FirstOrDefault(d =>
                d.Id == id && d.OwnerId == ownerId);

            if (document == null)
                return NotFound("Document not found or you are not the owner.");

            document.SharedWith ??= new List<DocumentShare>();

            var existingShare = document.SharedWith.FirstOrDefault(
                s => s.UserId == targetUser.Id);

            if (existingShare == null)
            {
                document.SharedWith.Add(new DocumentShare
                {
                    UserId = targetUser.Id,
                    Email = targetUser.Email ?? request.Email.Trim(),
                    Permission = permission.Equals(
                        "Edit", StringComparison.OrdinalIgnoreCase)
                        ? "Edit"
                        : "Read"
                });
            }
            else
            {
                existingShare.Permission = permission.Equals(
                    "Edit", StringComparison.OrdinalIgnoreCase)
                    ? "Edit"
                    : "Read";
            }

            await SaveDocumentsAsync(documents);

            await LogAsync(
                ownerId, id, "Share", "Success",
                $"Shared with {targetUser.Email}, Permission: {permission}");

            return Ok(new
            {
                message = "Document shared successfully.",
                email = targetUser.Email,
                permission = permission
            });
        }
        finally
        {
            FileLock.Release();
        }
    }

    [HttpDelete("{id:guid}/share/{sharedUserId}")]
    public async Task<IActionResult> RevokeShare(
        Guid id,
        string sharedUserId)
    {
        var ownerId = CurrentUserId;

        if (string.IsNullOrEmpty(ownerId))
            return Unauthorized();

        await FileLock.WaitAsync();
        try
        {
            var documents = await ReadDocumentsAsync();

            var document = documents.FirstOrDefault(d =>
                d.Id == id && d.OwnerId == ownerId);

            if (document == null)
                return NotFound("Document not found or you are not the owner.");

            var share = document.SharedWith?.FirstOrDefault(
                s => s.UserId == sharedUserId);

            if (share == null)
                return NotFound("Share not found.");

            document.SharedWith!.Remove(share);

            await SaveDocumentsAsync(documents);

            await LogAsync(
                ownerId, id, "Revoke", "Success",
                $"Access revoked for {share.Email}");

            return Ok(new { message = "Access revoked successfully." });
        }
        finally
        {
            FileLock.Release();
        }
    }

    [HttpGet("{id:guid}/download")]
    [HttpGet("download/{id:guid}")]
    public async Task<IActionResult> DownloadDocument(Guid id)
    {
        var userId = CurrentUserId;

        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        Document? document;

        await FileLock.WaitAsync();
        try
        {
            var documents = await ReadDocumentsAsync();

            document = documents.FirstOrDefault(d =>
                d.Id == id &&
                (d.OwnerId == userId ||
                 (d.SharedWith?.Any(s =>
                     s.UserId == userId &&
                     (s.Permission == "Read" || s.Permission == "Edit")) ?? false)));
        }
        finally
        {
            FileLock.Release();
        }

        if (document == null)
        {
            await LogAsync(userId, id, "Download", "Denied",
                "Document not found or access denied.");

            return NotFound("Document not found or access denied.");
        }

        var path = Path.Combine(_uploadPath, document.StoredFileName);

        if (!System.IO.File.Exists(path))
        {
            await LogAsync(userId, id, "Download", "Failed", "File not found.");
            return NotFound("File not found.");
        }

        await LogAsync(userId, id, "Download", "Success", document.FileName);

        return PhysicalFile(path, document.ContentType, document.FileName);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteDocument(Guid id)
    {
        var userId = CurrentUserId;

        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        await FileLock.WaitAsync();
        try
        {
            var documents = await ReadDocumentsAsync();

            var document = documents.FirstOrDefault(d =>
                d.Id == id && d.OwnerId == userId);

            if (document == null)
                return NotFound("Document not found.");

            documents.Remove(document);
            await SaveDocumentsAsync(documents);

            var path = Path.Combine(_uploadPath, document.StoredFileName);

            if (System.IO.File.Exists(path))
                System.IO.File.Delete(path);

            await LogAsync(userId, id, "Delete", "Success", document.FileName);

            return Ok(new { message = "Document deleted successfully." });
        }
        finally
        {
            FileLock.Release();
        }
    }
}

public class ShareDocumentRequest
{
    public string Email { get; set; } = "";
    public string Permission { get; set; } = "Read";
}
