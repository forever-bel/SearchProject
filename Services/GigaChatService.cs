using System.Text;
using System.Text.Json;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace SearchProject.API.Services;

public class GigaChatService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GigaChatService> _logger;
    private string? _accessToken;
    private DateTime _tokenExpiry;

    public GigaChatService(
        IConfiguration configuration,
        ILogger<GigaChatService> logger)
    {
        _configuration = configuration;
        _logger = logger;

        var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback =
            (sender, cert, chain, sslPolicyErrors) => true;

        _httpClient = new HttpClient(handler);
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    private async Task<string> GetAccessTokenAsync()
    {
        if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiry)
        {
            return _accessToken;
        }

        _logger.LogInformation("Получаем новый токен GigaChat...");

        var clientId = _configuration["GigaChatSettings:ClientId"];
        var clientSecret = _configuration["GigaChatSettings:ClientSecret"];
        var scope = _configuration["GigaChatSettings:Scope"] ?? "GIGACHAT_API_PERS";
        var authUrl = _configuration["GigaChatSettings:AuthUrl"]
            ?? "https://ngw.devices.sberbank.ru:9443/api/v2/oauth";

        var credentials = clientSecret;

        var request = new HttpRequestMessage(HttpMethod.Post, authUrl);
        request.Headers.Add("Authorization", $"Basic {credentials}");
        request.Headers.Add("RqUID", Guid.NewGuid().ToString());
        request.Headers.Add("Accept", "application/json");

        var body = new Dictionary<string, string>
        {
            ["scope"] = scope
        };
        request.Content = new FormUrlEncodedContent(body);

        try
        {
            var response = await _httpClient.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            _logger.LogInformation($"Статус: {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"Ошибка авторизации: {json}");
                throw new Exception($"Ошибка авторизации GigaChat: {response.StatusCode}, {json}");
            }

            var result = JsonDocument.Parse(json);
            _accessToken = result.RootElement
                .GetProperty("access_token")
                .GetString();

            var expiresAtMs = result.RootElement
                .GetProperty("expires_at")
                .GetInt64();

            _tokenExpiry = DateTimeOffset.FromUnixTimeSeconds(expiresAtMs / 1000).UtcDateTime;

            _logger.LogInformation($"Токен получен, истекает: {_tokenExpiry}");
            return _accessToken!;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при получении токена");
            throw;
        }
    }

    public async Task<string> GenerateAnswerAsync(string query, List<string> documents)
    {
        try
        {
            var token = await GetAccessTokenAsync();
            var model = _configuration["GigaChatSettings:Model"] ?? "GigaChat";
            var apiUrl = _configuration["GigaChatSettings:ApiUrl"]
                ?? "https://gigachat.devices.sberbank.ru/api/v1";

            var prompt = $@"Ответь на вопрос, используя ТОЛЬКО информацию из предоставленных документов.
                Если в документах нет ответа на вопрос, скажи: 'Я не нашел информации по вашему вопросу'. НЕ ПРИДУМЫВАЙ ответ.

                Документы:
                {string.Join("\n\n---\n\n", documents.Select((d, i) => $"Документ {i + 1}:\n{d}"))}

                Вопрос пользователя: {query}

                Ответ:";

            var requestBody = new
            {
                model = model,
                messages = new[]
                {
                    new { role = "user", content = prompt }
                },
                temperature = 0.3,
                max_tokens = 1000,
                stream = false
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, $"{apiUrl}/chat/completions");
            request.Headers.Add("Authorization", $"Bearer {token}");
            request.Headers.Add("Accept", "application/json");
            request.Content = content;

            _logger.LogInformation($"Отправка запроса к GigaChat...");

            var response = await _httpClient.SendAsync(request);
            var responseJson = await response.Content.ReadAsStringAsync();

            _logger.LogInformation($"Статус: {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"Ошибка GigaChat: {response.StatusCode}, {responseJson}");
                return $"Извините, произошла ошибка: {response.StatusCode}";
            }

            var result = JsonDocument.Parse(responseJson);

            var answer = result.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return answer ?? "Не удалось получить ответ";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при запросе к GigaChat");
            return "Извините, произошла ошибка при генерации ответа";
        }
    }
}