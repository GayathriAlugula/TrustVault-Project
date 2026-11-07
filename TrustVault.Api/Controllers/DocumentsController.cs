
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TrustVault.Api.Models;

namespace TrustVault.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DocumentsController : ControllerBase
{
    private readonly string _uploadPath;
    private readonly string _dataPath;

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public DocumentsController(IWebHostEnvironment environment)
    {
        _uploadPath = Path.Combine(environment.ContentRootPath, "Uploads");
        _dataPath = Path.Combine(environment.ContentRootPath, "Data", "documents.json");

        Directory.CreateDirectory(_uploadPath);
        Directory.CreateDirectory(Path.GetDirectoryName(_dataPath)!);
    }

    private string? CurrentUserId =>
        User.FindFirstValue(ClaimTypes.NameIdentifier);

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

            return Ok(documents.Where(d => d.OwnerId == userId));
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

            var results = documents.Where(d =>
                d.OwnerId == userId &&
                d.FileName.Contains(
                    query ?? "", StringComparison.OrdinalIgnoreCase));

            return Ok(results);
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
                d.Id == id && d.OwnerId == userId);
        }
        finally
        {
            FileLock.Release();
        }

        if (document == null)
            return NotFound("Document not found.");

        var path = Path.Combine(_uploadPath, document.StoredFileName);

        if (!System.IO.File.Exists(path))
            return NotFound("File not found.");

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

            return Ok(new { message = "Document deleted successfully." });
        }
        finally
        {
            FileLock.Release();
        }
    }
}
