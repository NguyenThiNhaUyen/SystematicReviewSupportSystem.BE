using DotNetEnv;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Npgsql;
using Pgvector.EntityFrameworkCore;
using Shared.Builder;
using Shared.Cache;
using Shared.Middlewares;
using Shared.Models;
using SRSS.IAM.API.DependencyInjection.Extensions;
using SRSS.IAM.Repositories;
using SRSS.IAM.Services.Interceptors;
using SRSS.IAM.Services.NotificationService;

namespace SRSS.IAM.API
{
    public class Program
    {
        public static void Main(string[] args)
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase))
            {
                Env.Load();
            }

            var builder = WebApplication.CreateBuilder(args);
            builder.Configuration.AddEnvironmentVariables();

            var config = builder.Configuration;

            builder.Services.AddControllers().AddXmlSerializerFormatters();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.ConfigureSwaggerForAuthentication();
            builder.Services.ConfigureJWT(config);
            builder.Services.ConfigureGlobalException();

            builder.Services.AddCors(options =>
            {
                options.AddPolicy("AllowFrontend", policy =>
                {
                    policy.WithOrigins("https://slr.hyperdatalab.org")
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        .AllowCredentials();
                });
            });

            builder.Services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                options.KnownNetworks.Clear();
                options.KnownProxies.Clear();
            });

            builder.Logging.ClearProviders();
            builder.Logging.AddConsole();
            builder.Logging.AddDebug();

            var environment = builder.Environment.EnvironmentName;

            var connectionString = config.GetConnectionString("SRSS_IAM_DB");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException("Missing required configuration: ConnectionStrings:SRSS_IAM_DB. Set ConnectionStrings__SRSS_IAM_DB for Docker deployments.");
            }

            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
            dataSourceBuilder.UseVector();
            var dataSource = dataSourceBuilder.Build();

            builder.Services.AddDbContext<AppDbContext>((sp, options) =>
            {
                options.UseNpgsql(dataSource, o =>
                {
                    o.UseVector();
                    o.MigrationsAssembly("SRSS.IAM.Repositories");
                });

                var auditInterceptor = sp.GetRequiredService<AuditInterceptor>();
                options.AddInterceptors(auditInterceptor);
            });

            builder.Services.AddRedisCacheWithHealthCheck(config);
            builder.Services.AddApplicationServices(config);

            builder.Services.Configure<ApiBehaviorOptions>(options =>
            {
                options.InvalidModelStateResponseFactory = context =>
                {
                    var errors = context.ModelState
                        .Where(x => x.Value?.Errors.Count > 0)
                        .SelectMany(x => x.Value!.Errors.Select(e => new ApiError { Code = "INVALID_MODEL_STATE", Message = e.ErrorMessage }))
                        .ToList();

                    var response = new ApiResponse
                    {
                        IsSuccess = false,
                        Message = "Du lieu khong hop le",
                        Errors = errors
                    };

                    return new BadRequestObjectResult(response);
                };
            });

            var app = builder.Build();

            var logger = app.Services.GetRequiredService<ILogger<Program>>();
            logger.LogInformation("Application starting in {Environment} environment", environment);

            app.UseMiddleware<JwtBlacklistMiddleware>();

            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                if (db.Database.GetPendingMigrations().Any())
                {
                    logger.LogInformation("Applying pending database migrations.");
                    db.Database.Migrate();
                    logger.LogInformation("Database migrations applied successfully.");
                }
                else
                {
                    logger.LogInformation("No pending migrations found.");
                }
            }

            app.Seed();

            app.UseSwagger(c =>
            {
                c.PreSerializeFilters.Add((swaggerDoc, httpReq) =>
                {
                    swaggerDoc.Servers = new List<OpenApiServer>
                    {
                        new() { Url = "/", Description = "Direct API Access" },
                    };
                });
            });

            app.UseSwaggerUI();
            app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
            app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
            app.MapGet("/health/db", async (AppDbContext db, ILogger<Program> healthLogger) =>
            {
                try
                {
                    var canConnect = await db.Database.CanConnectAsync();
                    return canConnect
                        ? Results.Ok(new { status = "ok", database = "connected" })
                        : Results.Json(ResponseBuilder.Error("Database is unavailable"), statusCode: StatusCodes.Status503ServiceUnavailable);
                }
                catch (Exception ex)
                {
                    healthLogger.LogError(ex, "Database health check failed.");
                    return Results.Json(ResponseBuilder.Error("Database is unavailable"), statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            });

            app.UseForwardedHeaders();
            app.UseHttpsRedirection();
            app.UseCors("AllowFrontend");
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseExceptionHandler();

            app.MapHub<NotificationHub>("/hubs/notification");
            app.MapControllers();

            app.Run();
        }
    }
}
