using Qdrant.Client;
using Qdrant.Client.Grpc;
using SearchProject.API.Models;
using Value = Qdrant.Client.Grpc.Value;

namespace SearchProject.API.Services;

public class QdrantService
{
    private readonly QdrantClient _client;
    private readonly string _collectionName;
    private readonly ILogger<QdrantService> _logger;
    private readonly EmbedderService _embedder;

    public QdrantService(
        IConfiguration configuration,
        ILogger<QdrantService> logger,
        EmbedderService embedder)
    {
        var host = configuration["QdrantSettings:Host"] ?? "localhost";
        _collectionName = configuration["QdrantSettings:CollectionName"] ?? "my_search";

        _client = new QdrantClient(host: host, https: false);
        _logger = logger;
        _embedder = embedder;

        _logger.LogInformation($"Qdrant клиент создан");
    }

    public async Task CreateCollectionAsync(int dimension)
    {
        try
        {
            var collections = await _client.ListCollectionsAsync();

            if (collections.Contains(_collectionName))
            {
                _logger.LogInformation($"Коллекция {_collectionName} уже существует");
                return;
            }

            await _client.CreateCollectionAsync(
                _collectionName,
                new VectorParams
                {
                    Size = (ulong)dimension,
                    Distance = Distance.Cosine
                }
            );

            _logger.LogInformation($"Коллекция {_collectionName} создана");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при создании коллекции");
            throw;
        }
    }

    public async Task AddDocumentsAsync(List<Post> posts)
    {
        _logger.LogInformation($"Загрузка {posts.Count} постов...");

        var points = new List<PointStruct>();
        var processed = 0;

        foreach (var post in posts)
        {
            var cleanText = CleanText(post.Text);
            if (cleanText.Length < 10) continue;

            var vector = _embedder.EmbedText(cleanText);

            var point = new PointStruct();
            point.Id = Guid.NewGuid();
            point.Vectors = vector;

            point.Payload["text"] = new Value { StringValue = cleanText };
            point.Payload["source"] = new Value { StringValue = post.Id.ToString() };
            point.Payload["date"] = new Value { StringValue = post.Date.ToString() };

            points.Add(point);
            processed++;

            if (processed % 100 == 0)
                _logger.LogInformation($"Обработано {processed} постов");
        }

        _logger.LogInformation($"Подготовлено {points.Count} постов");

        var batchSize = 100;
        for (int i = 0; i < points.Count; i += batchSize)
        {
            var batch = points.Skip(i).Take(batchSize).ToList();
            await _client.UpsertAsync(_collectionName, batch);
            _logger.LogInformation($"Загружено {Math.Min(i + batchSize, points.Count)} постов");
        }

        _logger.LogInformation($"Загрузка завершена!");
    }

    public async Task<List<SearchResult>> SearchAsync(string query, int topK = 10)
    {
        if (topK <= 0) topK = 10;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        _logger.LogInformation($"Векторный поиск: '{query}'");

        var queryVector = _embedder.EmbedText(query);

        var searchResult = await _client.SearchAsync(
            _collectionName,
            queryVector,
            limit: (ulong)topK
        );

        var results = new List<SearchResult>();

        foreach (var r in searchResult)
        {
            results.Add(new SearchResult
            {
                Score = r.Score,
                Text = r.Payload.ContainsKey("text") ? r.Payload["text"]?.StringValue ?? "" : "",
                Source = r.Payload.ContainsKey("source") ? r.Payload["source"]?.StringValue ?? "unknown" : "unknown",
                Id = r.Id?.ToString() ?? "",
                Date = r.Payload.ContainsKey("date") ? r.Payload["date"]?.StringValue ?? "" : ""
            });
        }

        stopwatch.Stop();

        _logger.LogInformation($"✅ Векторный поиск: {results.Count} результатов, время: {stopwatch.ElapsedMilliseconds} мс");
        if (results.Any())
        {
            var avgScore = results.Average(r => r.Score);
            var maxScore = results.Max(r => r.Score);
            var minScore = results.Min(r => r.Score);
            _logger.LogInformation($"   Score: средний {avgScore:F4}, макс {maxScore:F4}, мин {minScore:F4}");
        }

        return results;
    }

    public async Task<List<SearchResult>> SearchByKeywordsAsync(string query, int topK = 10)
    {
        if (topK <= 0) topK = 10;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        _logger.LogInformation($"Поиск по ключевым словам: '{query}'");

        var results = new List<SearchResult>();

        try
        {
            var stopWords = new HashSet<string>
            {
                "и", "в", "на", "с", "по", "к", "у", "а", "но", "за", "о", "от", "из", "для"
            };

            var keywords = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2 && !stopWords.Contains(w.ToLower()))
                .Select(w => w.ToLower())
                .ToList();

            if (!keywords.Any())
            {
                _logger.LogWarning("Нет ключевых слов для поиска");
                return results;
            }

            _logger.LogInformation($"Ключевые слова: {string.Join(", ", keywords)}");

            var scrollResult = await _client.ScrollAsync(_collectionName);

            _logger.LogInformation($"Просканировано {scrollResult.Result.Count} постов");

            foreach (var point in scrollResult.Result)
            {
                var text = point.Payload.ContainsKey("text")
                    ? point.Payload["text"]?.StringValue ?? ""
                    : "";

                if (string.IsNullOrEmpty(text))
                    continue;

                var textLower = text.ToLower();
                var matchCount = keywords.Count(k => textLower.Contains(k));
                var exactPhraseMatch = textLower.Contains(query.ToLower());

                if (matchCount > 0 || exactPhraseMatch)
                {
                    var score = 0.3f + (matchCount * 0.15f);

                    if (exactPhraseMatch)
                        score += 0.2f;

                    if (matchCount == keywords.Count)
                        score += 0.1f;

                    score = Math.Min(score, 1.0f);

                    results.Add(new SearchResult
                    {
                        Score = score,
                        Text = text,
                        Source = point.Payload.ContainsKey("source") ? point.Payload["source"]?.StringValue ?? "unknown" : "unknown",
                        Id = point.Id?.ToString() ?? "",
                        Date = point.Payload.ContainsKey("date") ? point.Payload["date"]?.StringValue ?? "" : ""
                    });
                }
            }

            results = results
                .OrderByDescending(r => r.Score)
                .Take(topK)
                .ToList();

            _logger.LogInformation($"Найдено {results.Count} совпадений по ключевым словам");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при поиске по ключевым словам");
        }

        stopwatch.Stop();

        _logger.LogInformation($"✅ Поиск по ключевым словам: {results.Count} результатов, время: {stopwatch.ElapsedMilliseconds} мс");
        if (results.Any())
        {
            var avgScore = results.Average(r => r.Score);
            var maxScore = results.Max(r => r.Score);
            var minScore = results.Min(r => r.Score);
            _logger.LogInformation($"   Score: средний {avgScore:F4}, макс {maxScore:F4}, мин {minScore:F4}");
        }

        return results;
    }

    public async Task<List<SearchResult>> SearchHybridAsync(string query, int topK = 10)
    {
        if (topK <= 0) topK = 10;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        _logger.LogInformation($"Гибридный поиск: '{query}'");

        var vectorResults = await SearchAsync(query, topK * 3);
        var keywordResults = await SearchByKeywordsAsync(query, topK * 3);

        var merged = new Dictionary<string, SearchResult>();

        if (vectorResults.Any())
        {
            var maxScore = vectorResults.Max(r => r.Score);
            var minScore = vectorResults.Min(r => r.Score);
            var range = maxScore - minScore;

            foreach (var r in vectorResults)
            {
                var normalizedScore = range > 0 ? (r.Score - minScore) / range : 0.5f;
                r.Score = normalizedScore * 0.7f;
            }
        }

        foreach (var r in keywordResults)
        {
            r.Score = r.Score * 0.3f;
        }

        foreach (var r in vectorResults)
        {
            if (!merged.ContainsKey(r.Id))
                merged[r.Id] = r;
            else
                merged[r.Id].Score += r.Score;
        }

        foreach (var r in keywordResults)
        {
            if (!merged.ContainsKey(r.Id))
                merged[r.Id] = r;
            else
                merged[r.Id].Score += r.Score;
        }

        var finalResults = merged.Values
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .ToList();

        stopwatch.Stop();

        _logger.LogInformation($"✅ Гибридный поиск: {finalResults.Count} результатов, время: {stopwatch.ElapsedMilliseconds} мс");
        if (finalResults.Any())
        {
            var avgScore = finalResults.Average(r => r.Score);
            var maxScore = finalResults.Max(r => r.Score);
            var minScore = finalResults.Min(r => r.Score);
            _logger.LogInformation($"   Score: средний {avgScore:F4}, макс {maxScore:F4}, мин {minScore:F4}");
        }

        return finalResults;
    }

    private string CleanText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var clean = System.Text.RegularExpressions.Regex.Replace(text, @"#\S+", "");
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"https?://\S+", "");
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\s+", " ");

        return clean.Trim();
    }
}