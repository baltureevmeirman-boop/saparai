using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient("gemini", c => c.Timeout = TimeSpan.FromSeconds(90));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await ctx.HttpContext.Response.WriteAsJsonAsync(
            new { error = "Сұраныс тым көп. Біраз күтіп, қайта көріңіз." }, ct);
    };
    o.AddPolicy("ai", ctx => RateLimitPartition.GetFixedWindowLimiter("all",
        key => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 40,
            Window = TimeSpan.FromHours(1)
        }));
});

var app = builder.Build();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/itinerary", async (ItineraryRequest req, IHttpClientFactory factory, IConfiguration cfg) =>
{
    if (string.IsNullOrWhiteSpace(req.City))
        return Results.BadRequest(new { error = "Қаланы енгізіңіз." });
    if (req.Budget < 1000)
        return Results.BadRequest(new { error = "Бюджет кемінде 1 000 ₸ болуы керек." });
    if (req.Days is < 1 or > 14)
        return Results.BadRequest(new { error = "Күн саны 1 мен 14 аралығында болуы керек." });

    var apiKey = cfg["Gemini:ApiKey"];
    var model = cfg["Gemini:Model"] ?? "gemini-flash-latest";
    if (string.IsNullOrWhiteSpace(apiKey))
        return Results.Json(new { error = "Gemini API кілті бапталмаған." }, statusCode: 500);

    var interests = string.IsNullOrWhiteSpace(req.Interests) ? "ерекше талап жоқ" : req.Interests.Trim();
    var prompt = $$"""
        Сен тәжірибелі саяхат жоспарлаушысың. Төмендегі деректер бойынша нақты маршрут жаса.

        Қала: {{req.City.Trim()}}
        Жалпы бюджет: {{req.Budget:0}} теңге (₸), бір адамға, тамақ, көлік және кіру билеттерін қоса
        Күн саны: {{req.Days}}
        Қызығушылықтар: {{interests}}

        Ережелер:
        - Тек нақты бар жерлерді ұсын. Ойдан жер шығарма.
        - Барлық бағалар теңгемен, шамамен берілсін.
        - Күндер бойынша барлық шығынның қосындысы бюджеттен аспасын.
        - Әр күнге 3-5 нүкте қой (уақыты, орны, қысқа сипаттамасы, бағасы).
        - Барлық мәтін қазақ тілінде болсын.
        - Жауапты ТЕК мына JSON пішімінде қайтар, басқа ештеңе жазба:

        {
          "summary": "маршрутқа 1-2 сөйлемдік шолу",
          "totalCost": 0,
          "days": [
            {
              "day": 1,
              "title": "күннің атауы",
              "items": [
                { "time": "09:00", "place": "орын атауы", "description": "не істеуге болады", "cost": 0 }
              ]
            }
          ],
          "tips": ["ақшаны үнемдеуге арналған кеңес"]
        }
        """;

    var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";
    var body = new
    {
        contents = new[] { new { parts = new[] { new { text = prompt } } } },
        generationConfig = new { responseMimeType = "application/json", temperature = 0.7 }
    };

    var http = factory.CreateClient("gemini");
    using var msg = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
    msg.Headers.Add("x-goog-api-key", apiKey);

    try
    {
        using var res = await http.SendAsync(msg);
        var raw = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
            return Results.Json(new { error = $"AI қызметі қате қайтарды ({(int)res.StatusCode})." }, statusCode: 502);

        using var doc = JsonDocument.Parse(raw);
        var text = doc.RootElement.GetProperty("candidates")[0]
                      .GetProperty("content").GetProperty("parts")[0]
                      .GetProperty("text").GetString() ?? "";

        text = text.Trim().Trim('`');
        if (text.StartsWith("json", StringComparison.OrdinalIgnoreCase)) text = text[4..];

        JsonDocument.Parse(text);
        return Results.Content(text, "application/json");
    }
    catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
    {
        return Results.Json(new { error = "AI жауабын оқу мүмкін болмады. Қайта көріңіз." }, statusCode: 502);
    }
    catch (TaskCanceledException)
    {
        return Results.Json(new { error = "AI жауап беруге көп уақыт алды. Қайта көріңіз." }, statusCode: 504);
    }
}).RequireRateLimiting("ai");

app.Run();

record ItineraryRequest(string City, decimal Budget, int Days, string? Interests);