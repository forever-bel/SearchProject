using System.Text.Json;
using SearchProject.API.Models;

namespace SearchProject.API.Services;

public class VkParserService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<VkParserService> _logger;

    public VkParserService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<VkParserService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<Post>> GetPostsAsync(int count = 100, int offset = 0)
    {
        var token = _configuration["VkSettings:AccessToken"];
        var groupId = _configuration["VkSettings:GroupId"];
        var version = _configuration["VkSettings:ApiVersion"];

        var url = $"https://api.vk.com/method/wall.get?access_token={token}&v={version}&owner_id=-{groupId}&count={Math.Min(count, 100)}&offset={offset}";

        try
        {
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var data = JsonDocument.Parse(json);

            var posts = new List<Post>();

            if (data.RootElement.TryGetProperty("response", out var responseElement) &&
                responseElement.TryGetProperty("items", out var itemsElement))
            {
                foreach (var item in itemsElement.EnumerateArray())
                {
                    var text = item.GetProperty("text").GetString()?.Trim();

                    if (!string.IsNullOrEmpty(text))
                    {
                        posts.Add(new Post
                        {
                            Id = item.GetProperty("id").GetInt32(),
                            Text = text,
                            Date = item.GetProperty("date").GetInt64(),
                            OwnerId = item.GetProperty("owner_id").GetInt32()
                        });
                    }
                }
            }

            return posts;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при парсинге VK");
            return new List<Post>();
        }
    }

    public async Task<List<Post>> ParseManyPostsAsync()
    {
        var allPosts = new List<Post>();
        var offset = 0;
        var batchSize = 100;

        while (offset < 10000)
        {
            _logger.LogInformation($"Загружаем посты с {offset}...");

            var posts = await GetPostsAsync(batchSize, offset);

            if (!posts.Any())
                break;

            allPosts.AddRange(posts);
            offset += batchSize;

            await Task.Delay(500);

            _logger.LogInformation($"Загружено {allPosts.Count} постов");
        }

        var json = JsonSerializer.Serialize(allPosts, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        await File.WriteAllTextAsync("data/posts.json", json);

        _logger.LogInformation($"Загружено {allPosts.Count} постов");
        return allPosts;
    }
}