using SearchProject.API.Services;
using SearchProject.API.Models;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddHttpClient<VkParserService>();
builder.Services.AddSingleton<EmbedderService>();
builder.Services.AddSingleton<QdrantService>();
builder.Services.AddScoped<VkParserService>();
builder.Services.AddScoped<GigaChatService>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll",
        policy =>
        {
            policy.AllowAnyOrigin()
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var embedder = scope.ServiceProvider.GetRequiredService<EmbedderService>();
    var qdrant = scope.ServiceProvider.GetRequiredService<QdrantService>();
    await qdrant.CreateCollectionAsync(embedder.Dimension);
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("AllowAll");
app.UseAuthorization();
app.MapControllers();

app.MapPost("/api/parse", async (VkParserService parser) =>
{
    var posts = await parser.ParseManyPostsAsync();
    return Results.Ok(new { count = posts.Count });
});

app.MapPost("/api/index", async (QdrantService qdrant) =>
{
    var dataPath = Path.Combine(Directory.GetCurrentDirectory(), "data", "posts.json");
    if (!File.Exists(dataPath))
    {
        return Results.BadRequest("Файл posts.json не найден. Сначала запустите /api/parse");
    }

    var json = await File.ReadAllTextAsync(dataPath);
    var posts = JsonSerializer.Deserialize<List<Post>>(json);

    if (posts == null || !posts.Any())
    {
        return Results.BadRequest("Нет постов для индексации");
    }

    await qdrant.AddDocumentsAsync(posts);

    return Results.Ok(new { indexed = posts.Count, message = "Индексация завершена" });
});

app.Run();