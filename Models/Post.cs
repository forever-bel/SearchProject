namespace SearchProject.API.Models;

public class Post
{
    public int Id { get; set; }
    public string Text { get; set; } = string.Empty;
    public long Date { get; set; }
    public int OwnerId { get; set; }
}