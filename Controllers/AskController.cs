using Microsoft.AspNetCore.Mvc;
using SearchProject.API.Models;
using SearchProject.API.Services;

namespace SearchProject.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AskController : ControllerBase
{
    private readonly QdrantService _qdrantService;
    private readonly GigaChatService _gigaChatService;
    private readonly ILogger<AskController> _logger;

    public AskController(
        QdrantService qdrantService,
        GigaChatService gigaChatService,
        ILogger<AskController> logger)
    {
        _qdrantService = qdrantService;
        _gigaChatService = gigaChatService;
        _logger = logger;
    }

    [HttpPost]
    public async Task<ActionResult<AskResponse>> Ask([FromBody] AskRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return BadRequest("Query cannot be empty");
        }

        try
        {
            var sessionId = GetSessionId();

            var searchResults = SearchController.GetLastSearchResults(sessionId);

            if (searchResults == null || !searchResults.Any())
            {
                _logger.LogWarning($"Нет сохраненных результатов поиска для сессии {sessionId}, выполняем новый поиск");

                searchResults = await _qdrantService.SearchHybridAsync(request.Query, topK: 5);
            }
            else
            {
                _logger.LogInformation($"Используем {searchResults.Count} документов из кеша Search");
            }

            if (!searchResults.Any())
            {
                return Ok(new AskResponse
                {
                    Answer = "Не найдено релевантных документов",
                    Sources = new List<SearchResult>()
                });
            }

            var topDocuments = searchResults.Take(5).ToList();
            var documents = topDocuments.Select(r => r.Text).ToList();

            _logger.LogInformation($"Отправляем в GigaChat {documents.Count} документов");

            var answer = await _gigaChatService.GenerateAnswerAsync(request.Query, documents);

            return Ok(new AskResponse
            {
                Answer = answer,
                Sources = topDocuments
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при генерации ответа");
            return StatusCode(500, "Внутренняя ошибка сервера");
        }
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