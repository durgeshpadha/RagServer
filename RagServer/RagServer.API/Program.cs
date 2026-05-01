using Microsoft.Extensions.Options;
using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    options.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);
});
builder.Services.Configure<RagOptions>(builder.Configuration.GetSection(RagOptions.SectionName));
builder.Services.AddHttpClient<EmbeddingService>(http =>
{
    http.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient("ollama-generate", http =>
{
    http.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddSingleton<VectorStore>();
builder.Services.AddSingleton<RagEngine>(sp =>
{
    var embed = sp.GetRequiredService<EmbeddingService>();
    var store = sp.GetRequiredService<VectorStore>();
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("ollama-generate");
    var options = sp.GetRequiredService<IOptions<RagOptions>>();
    return new RagEngine(embed, store, http, options);
});

var app = builder.Build();

app.UseExceptionHandler(handler =>
{
    handler.Run(async context =>
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GlobalException");
        var exceptionFeature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
        if (exceptionFeature?.Error != null)
        {
            logger.LogError(exceptionFeature.Error, "Unhandled exception processing request {Path}", context.Request.Path);
        }

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new ErrorResponse("internal_error", "An unexpected error occurred."));
    });
});

app.MapControllers();
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "RagServer.API v1");
    options.RoutePrefix = "swagger";
});
app.UseReDoc(options =>
{
    options.RoutePrefix = "redoc";
    options.SpecUrl = "/swagger/v1/swagger.json";
    options.DocumentTitle = "RagServer.API Docs";
});

app.Run();
