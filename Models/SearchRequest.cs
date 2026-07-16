namespace SearchProject.API.Models;

public class SearchRequest
{
    public string Query { get; set; } = string.Empty;
    public int TopK { get; set; } = 10;
}

public class SearchResponse
{
    public List<SearchResult> Results { get; set; } = new();
}

public class SearchResult
{
    public float Score { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
}