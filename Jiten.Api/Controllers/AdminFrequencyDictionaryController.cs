using Jiten.Api.Services.Mining;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jiten.Api.Controllers;

/// <summary>Imported Yomitan frequency dictionaries, for sorting mining sentences by the ranks Yomitan puts on cards.</summary>
[ApiController]
[Route("api/admin/frequency-dictionaries")]
[Authorize("RequiresAdmin")]
public class AdminFrequencyDictionaryController(FrequencyDictionaryService dictionaries) : ControllerBase
{
    private const long MaxZipBytes = 200 * 1024 * 1024;

    [HttpGet]
    public Task<List<FrequencyDictionaryDto>> List(CancellationToken ct) => dictionaries.ListAsync(ct);

    /// <summary>Upload a Yomitan frequency dictionary zip; one with the same title is replaced.</summary>
    [HttpPost]
    [RequestSizeLimit(MaxZipBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxZipBytes)]
    public async Task<ActionResult<FrequencyDictionaryDto>> Import(IFormFile file, CancellationToken ct)
    {
        try
        {
            await using var stream = file.OpenReadStream();
            return Ok(await dictionaries.ImportAsync(stream, ct));
        }
        catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException)
        {
            return BadRequest(new { message = $"Couldn't read the dictionary: {e.Message}" });
        }
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct) => await dictionaries.DeleteAsync(id, ct) ? NoContent() : NotFound();
}
