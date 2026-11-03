
using System.Text.Json;
using System.Text.Json.Serialization;
using AurSil.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Data.SqlClient;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// 1. Controllers and JSON configuration
builder.Services.AddControllers()
    .AddJsonOptions(o =>
        o.JsonSerializerOptions.Converters.Add(new UtcDateTimeConverter()));

// 2. Swagger configuration
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "AurSil API",
        Version = "v1",
        Description = "AurSil Backend API Documentation"
    });

    // JWT Bearer Authentication
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter your JWT token here."
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// 3. HTTP Clients
builder.Services.AddHttpClient("prices", c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 AurSil/1.0");
});

builder.Services.AddHttpClient("razorpay", c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
});

// 4. Application services
builder.Services.AddSingleton(
    builder.Configuration.GetSection("Smtp").Get<SmtpOptions>()
    ?? new SmtpOptions());

builder.Services.AddSingleton(
    builder.Configuration.GetSection("Pricing").Get<PricingOptions>()
    ?? new PricingOptions());

builder.Services.AddSingleton(
    builder.Configuration.GetSection("Razorpay").Get<RazorpayOptions>()
    ?? new RazorpayOptions());

builder.Services.AddSingleton<EmailSender>();
builder.Services.AddSingleton<OtpService>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<PriceService>();
builder.Services.AddSingleton<RazorpayService>();
builder.Services.AddSingleton<OrderService>();

builder.Services.AddHostedService<PriceUpdater>();

// 5. JWT Authentication
builder.Services.AddAuthentication(
    JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Issuer"],
            IssuerSigningKey = TokenService.SigningKey(
                builder.Configuration),
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });

builder.Services.AddAuthorization();

// 6. CORS
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod()));

// 7. Build application
var app = builder.Build();

// 8. Static files
var projectRoot = Path.GetFullPath(
    Path.Combine(builder.Environment.ContentRootPath, ".."));

app.UseDefaultFiles(new DefaultFilesOptions
{
    FileProvider = new PhysicalFileProvider(projectRoot),
    DefaultFileNames = ["index.html"]
});

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(projectRoot)
});

// 9. Swagger UI (Development only)
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();

    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint(
            "/swagger/v1/swagger.json",
            "AurSil API v1");

        options.RoutePrefix = "swagger";
    });

    app.UseCors();
}

// 10. Authentication and Authorization
app.UseAuthentication();
app.UseAuthorization();

// 11. API health check
app.MapGet("/api/health", () =>
    Results.Ok(new
    {
        ok = true,
        app = "AurSil API",
        status = "running",
        database = "sql-server",
        fallbackMode = true
    }));

// 12. Database health check
if (app.Environment.IsDevelopment())
{
    app.MapGet("/api/health/database",
        async (IConfiguration config) =>
        {
            try
            {
                await using var connection =
                    new SqlConnection(
                        config.GetConnectionString("Aursil"));

                await connection.OpenAsync();

                await using var command = connection.CreateCommand();

                command.CommandText = "SELECT DB_NAME()";

                var databaseName =
                    await command.ExecuteScalarAsync();

                return Results.Ok(new
                {
                    connected = true,
                    database = databaseName
                });
            }
            catch (Exception ex)
            {
                return Results.Problem(
    title: "Database connection failed",
    detail: ex.Message,
    statusCode: 500);
            }
        });
}

// 13. Root route
app.MapGet("/", () =>
    Results.Redirect("/index.html"));

// 14. Controller endpoints
app.MapControllers();

// 15. Start application
app.Run();

// SQL Server returns DateTime without a kind.
// Database values are treated as UTC.
class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        reader.GetDateTime().ToUniversalTime();

    public override void Write(
        Utf8JsonWriter writer,
        DateTime value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(
            DateTime.SpecifyKind(
                value,
                DateTimeKind.Utc
            ).ToString("yyyy-MM-ddTHH:mm:ssZ"));
}
