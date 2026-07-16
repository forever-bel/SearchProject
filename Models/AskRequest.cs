namespace SearchProject.API.Models;

public class AskRequest
{
    public string Query { get; set; } = string.Empty;
}

public class AskResponse
{
    public string Answer { get; set; } = string.Empty;
    public List<SearchResult> Sources { get; set; } = new();
}