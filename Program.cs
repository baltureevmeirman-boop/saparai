using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient("gemini", c => c.Timeout = TimeSpan.FromSeconds(120));

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
            PermitLimit = 200,
            Window = TimeSpan.FromHours(1)
        }));
});

var app = builder.Build();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();

var cache = new ConcurrentDictionary<string, string>();

app.MapGet("/health", () => Results.Ok("ok"));

app.MapPost("/api/itinerary", async (ItineraryRequest req, IHttpClientFactory factory, IConfiguration cfg) =>
{
    if (string.IsNullOrWhiteSpace(req.City))
        return Results.BadRequest(new { error = "Қаланы енгізіңіз." });
    if (req.Budget < 1000)
        return Results.BadRequest(new { error = "Бюджет кемінде 1 000 ₸ болуы керек." });
    if (req.Days is < 1 or > 14)
        return Results.BadRequest(new { error = "Күн саны 1 мен 14 аралығында болуы керек." });

    var apiKey = cfg["Gemini:ApiKey"];
    if (string.IsNullOrWhiteSpace(apiKey))
        return Results.Json(Demo.Build(req.City, req.Budget, req.Days));

    var interests = string.IsNullOrWhiteSpace(req.Interests) ? "ерекше талап жоқ" : req.Interests.Trim();

    var cacheKey = $"{req.City.Trim().ToLowerInvariant()}|{req.Budget:0}|{req.Days}|{interests.ToLowerInvariant()}";
    if (cache.TryGetValue(cacheKey, out var cached))
        return Results.Content(cached, "application/json");

    var primary = cfg["Gemini:Model"] ?? "gemini-flash-lite-latest";
    var models = new[] { primary, "gemini-flash-latest" }.Distinct().ToArray();

    var prompt = $$"""
        Сен саяхат жоспарлаушысың. Қысқа әрі нақты маршрут жаса.

        Қала: {{req.City.Trim()}}
        Жалпы бюджет: {{req.Budget:0}} теңге (₸), бір адамға, тамақ, көлік және билеттерді қоса
        Күн саны: {{req.Days}}
        Қызығушылықтар: {{interests}}

        Ережелер:
        - Тек нақты бар жерлерді ұсын.
        - Бағалар теңгемен, шамамен.
        - Барлық шығынның қосындысы бюджеттен аспасын.
        - Әр күнге 3-4 нүкте. Сипаттама тек 1 қысқа сөйлем.
        - Кеңестер: ең көбі 3 қысқа сөйлем.
        - Барлық мәтін қазақ тілінде.
        - Жауапты ТЕК мына JSON пішімінде қайтар:

        {
          "summary": "1 сөйлемдік шолу",
          "totalCost": 0,
          "days": [
            {
              "day": 1,
              "title": "күннің атауы",
              "items": [
                { "time": "09:00", "place": "орын атауы", "description": "1 сөйлем", "cost": 0 }
              ]
            }
          ],
          "tips": ["кеңес"]
        }
        """;

    var body = new
    {
        contents = new[] { new { parts = new[] { new { text = prompt } } } },
        generationConfig = new
        {
            responseMimeType = "application/json",
            temperature = 0.6,
            maxOutputTokens = 6000
        }
    };

    var http = factory.CreateClient("gemini");

    for (int attempt = 0; attempt < 4; attempt++)
    {
        var model = models[attempt % models.Length];
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var msg = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            msg.Headers.Add("x-goog-api-key", apiKey);

            using var res = await http.SendAsync(msg, cts.Token);
            if (!res.IsSuccessStatusCode)
            {
                await Task.Delay(1000);
                continue;
            }

            var raw = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(raw);
            var text = doc.RootElement.GetProperty("candidates")[0]
                          .GetProperty("content").GetProperty("parts")[0]
                          .GetProperty("text").GetString() ?? "";

            text = text.Trim().Trim('`');
            if (text.StartsWith("json", StringComparison.OrdinalIgnoreCase)) text = text[4..];

            JsonDocument.Parse(text);
            cache[cacheKey] = text;
            return Results.Content(text, "application/json");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException
                                   or TaskCanceledException or OperationCanceledException or HttpRequestException)
        {
            await Task.Delay(800);
        }
    }

    // AI жауап бермесе: қате көрсетпейміз, дайын үлгі жоспар береміз
    return Results.Json(Demo.Build(req.City, req.Budget, req.Days));
}).RequireRateLimiting("ai");

app.Run();

record ItineraryRequest(string City, decimal Budget, int Days, string? Interests);

static class Demo
{
    record Spot(string Place, string Desc, int W);

    static readonly Dictionary<string, Spot[]> Spots = new()
    {
        ["астана"] = new[]
        {
            new Spot("Бәйтерек монументі", "Астананың символына шығып, қала панорамасын көру.", 2),
            new Spot("Хан Шатыр сауда-ойын орталығы", "Ішіндегі жағажай мен дүкендерді аралау.", 2),
            new Spot("Есіл жағалауындағы серуен", "Жағалау бойымен жаяу серуен және демалыс.", 0),
            new Spot("Нұр-Астана мешіті", "Қаладағы ең көрнекті мешіттердің бірі.", 0),
            new Spot("Қазақстан Ұлттық музейі", "Ел тарихы мен мәдениетімен танысу.", 1),
            new Spot("Ботаникалық бақ", "Табиғат аясында тыныш серуен.", 0),
        },
        ["алматы"] = new[]
        {
            new Spot("Көктөбе тауы", "Аспалы жолмен шығып, қаланы биіктен көру.", 2),
            new Spot("Медеу мен Шымбұлақ", "Таулы аймақта серуен және таза ауа.", 3),
            new Spot("Панфиловшылар саябағы", "Саябақ пен Вознесен соборын көру.", 0),
            new Spot("Жібек жолы көшесі (Арбат)", "Жаяу жүргіншілер көшесінде серуен.", 0),
            new Spot("Үлкен Алматы көлі", "Таулардағы көрікті көлге саяхат.", 2),
            new Spot("Орталық мемлекеттік музей", "Қазақстан тарихымен танысу.", 1),
        },
        ["ақтау"] = new[]
        {
            new Spot("Каспий теңізінің жағалауы", "Теңіз жағасында серуен және күнбатыс.", 0),
            new Spot("Ақтау жағажайы", "Теңіз жағасында демалу.", 1),
            new Spot("Маңғыстау өлкетану музейі", "Өңірдің тарихы мен табиғатымен танысу.", 1),
            new Spot("Бозжыра алқабына экскурсия", "Ерекше табиғи жартастар мен пейзаж.", 4),
            new Spot("Қала орталығы мен жағалау аллеясы", "Қаланы аралап, кафеде отыру.", 1),
        },
        ["шымкент"] = new[]
        {
            new Spot("Шымкент дендропаркі", "Көгалдандырылған саябақта серуен.", 0),
            new Spot("Шымкент хайуанаттар бағы", "Жануарларды көріп, демалу.", 1),
            new Spot("Өлкетану музейі", "Оңтүстік өңірдің тарихы.", 1),
            new Spot("Орталық базар", "Жергілікті өнімдер мен кәдесыйлар.", 2),
            new Spot("Тәуелсіздік алаңы мен орталық көшелер", "Қаланың орталығын аралау.", 0),
        },
        ["түркістан"] = new[]
        {
            new Spot("Қожа Ахмет Яссауи кесенесі", "Қазақстанның ең танымал тарихи ескерткіші.", 1),
            new Spot("Хилует жер асты мешіті", "Ежелгі жер асты мешітін көру.", 1),
            new Spot("Түркістан қорық-музейі", "Қала тарихы мен көне ескерткіштер.", 1),
            new Spot("Рабиға Сұлтан Бегім кесенесі", "Тарихи кесенені аралау.", 1),
            new Spot("Көне қала мен жаяу серуен аймағы", "Қаланың жаңа орталығында серуен.", 0),
        },
    };

    static Spot[] Generic(string name) => new[]
    {
        new Spot($"{name} орталығы мен негізгі көшелері", "Қала орталығын аралап, көрікті жерлермен танысу.", 0),
        new Spot("Өлкетану музейі", "Өңірдің тарихы мен мәдениеті.", 1),
        new Spot("Орталық саябақ", "Серуен және демалыс.", 0),
        new Spot("Орталық базар", "Жергілікті өнімдер мен кәдесыйлар.", 2),
        new Spot("Қала маңындағы табиғат аймағы", "Табиғат аясында серуен.", 2),
    };

    public static object Build(string city, decimal budget, int days)
    {
        var name = city.Trim();
        var spots = Spots.TryGetValue(name.ToLowerInvariant(), out var s) ? s : Generic(name);
        var meal = new Spot("Ұлттық тағам түскі асы", "Жергілікті асханада дәстүрлі тамақ.", 3);
        string[] times = { "10:00", "13:00", "16:00", "19:00" };

        var plan = new List<List<Spot>>();
        int idx = 0;
        for (int d = 0; d < days; d++)
        {
            plan.Add(new List<Spot>
            {
                spots[idx++ % spots.Length],
                meal,
                spots[idx++ % spots.Length],
                spots[idx++ % spots.Length],
            });
        }

        double totalW = plan.Sum(l => l.Sum(x => x.W));
        if (totalW <= 0) totalW = 1;
        double pool = (double)budget * 0.8;

        int total = 0;
        var dayObjs = new List<object>();
        for (int d = 0; d < plan.Count; d++)
        {
            var items = new List<object>();
            for (int i = 0; i < plan[d].Count; i++)
            {
                var sp = plan[d][i];
                int cost = (int)(Math.Round(pool * sp.W / totalW / 500.0) * 500);
                total += cost;
                items.Add(new { time = times[i], place = sp.Place, description = sp.Desc, cost });
            }
            dayObjs.Add(new { day = d + 1, title = d == 0 ? "Қаламен танысу" : "Көрікті жерлер", items });
        }

        return new
        {
            summary = $"Ескерту: AI қазір бос емес, сондықтан {name} қаласы үшін дайын үлгі жоспар көрсетілді.",
            totalCost = total,
            days = dayObjs,
            tips = new[]
            {
                "Қонақүйді алдын ала брондасаңыз, арзанырақ болады.",
                "Қала ішінде қоғамдық көлікті пайдаланыңыз.",
                "Түскі асты орталықтан сәл алыс жерде ішсеңіз, арзан болады.",
            }
        };
    }
}
