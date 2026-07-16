using Microsoft.AspNetCore.Mvc;
using SearchProject.API.Models;
using SearchProject.API.Services;

namespace SearchProject.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SearchController : ControllerBase
{
    private readonly QdrantService _qdrantService;
    private readonly ILogger<SearchController> _logger;

    private static readonly Dictionary<string, List<SearchResult>> _lastSearchResults = new();
    private static readonly Dictionary<string, DateTime> _cacheExpiry = new();

    public SearchController(QdrantService qdrantService, ILogger<SearchController> logger)
    {
        _qdrantService = qdrantService;
        _logger = logger;
    }

    [HttpPost]
    public async Task<ActionResult<SearchResponse>> Search([FromBody] SearchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return BadRequest("Query cannot be empty");
        }

        if (request.TopK <= 0)
        {
            request.TopK = 10;
        }

        try
        {
            _logger.LogInformation($"Поиск: '{request.Query}'");

            var results = await _qdrantService.SearchHybridAsync(request.Query, request.TopK);

            var sessionId = GetSessionId();
            _lastSearchResults[sessionId] = results;
            _cacheExpiry[sessionId] = DateTime.UtcNow.AddMinutes(10);

            _logger.LogInformation($"Найдено {results.Count} результатов, сохранено для сессии {sessionId}");

            return Ok(new SearchResponse
            {
                Results = results
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при поиске");
            return StatusCode(500, "Внутренняя ошибка сервера");
        }
    }

    public static List<SearchResult>? GetLastSearchResults(string sessionId)
    {
        if (_lastSearchResults.TryGetValue(sessionId, out var results) &&
            _cacheExpiry.TryGetValue(sessionId, out var expiry) &&
            DateTime.UtcNow < expiry)
        {
            return results;
        }
        return null;
    }

    private string GetSessionId()
    {
        if (Request.Headers.TryGetValue("X-Session-Id", out var sessionHeader))
        {
            return sessionHeader.ToString();
        }

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var userAgent = Request.Headers["User-Agent"].ToString();
        return $"{ip}_{userAgent.GetHashCode()}";
    }
}