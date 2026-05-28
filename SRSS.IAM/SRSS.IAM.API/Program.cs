using DotNetEnv;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.OpenApi.Models;
using Npgsql;
using Pgvector.EntityFrameworkCore;
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

            // Thêm Environment Variables vào Configuration
            builder.Configuration.AddEnvironmentVariables();

            var config = builder.Configuration;
            // Add services to the container.
            //DotNetEnv.Env.Load();

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

            // Configure logging (Console + Debug)
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole();
            builder.Logging.AddDebug();

            var environment = builder.Environment.EnvironmentName;
            builder.Logging.AddConsole();



            // Database connection
            var connectionString = config.GetConnectionString("SRSS_IAM_DB")
                ?? throw new InvalidOperationException("ConnectionStrings:SRSS_IAM_DB is required");
            Console.WriteLine("=== DEBUG CONNECTION STRING ===");
            Console.WriteLine(connectionString);
            // builder.Services.AddDbContext<AppDbContext>(options =>
            //     options.UseNpgsql(connectionString));

            // 1. Khởi tạo NpgsqlDataSourceBuilder và cấu hình UseVector() cho ADO.NET
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
            dataSourceBuilder.UseVector();
            var dataSource = dataSourceBuilder.Build();

            // 2. Sử dụng dataSource này cho DbContext và gọi thêm UseVector() + MigrationsAssembly
            builder.Services.AddDbContext<AppDbContext>((sp, options) =>
            {
                options.UseNpgsql(dataSource, o =>
                {
                    o.UseVector();
                    // BẮT BUỘC: Khai báo cho EF Core biết thư mục Migrations nằm ở project Repositories
                    o.MigrationsAssembly("SRSS.IAM.Repositories");
                });

                var auditInterceptor = sp.GetRequiredService<AuditInterceptor>();
                options.AddInterceptors(auditInterceptor);
            });

            // Redis connection
            builder.Services.AddRedisCacheWithHealthCheck(config);


            builder.Services.AddApplicationServices(config);

            builder.Services.Configure<ApiBehaviorOptions>(options =>
            {
                options.InvalidModelStateResponseFactory = context =>
                {
                    var errors = context.ModelState
                        .Where(x => x.Value.Errors.Count > 0)
                        .SelectMany(x => x.Value.Errors.Select(e => new ApiError { Code = "INVALID_MODEL_STATE", Message = e.ErrorMessage }))
                        .ToList();

                    var response = new ApiResponse
                    {
                        IsSuccess = false,
                        Message = "Dữ liệu không hợp lệ",
                        Errors = errors
                    };

                    return new BadRequestObjectResult(response);
                };
            });


            var app = builder.Build();

            // 🔥 LOG MÔI TRƯỜNG VÀ CONNECTION INFO
            var logger = app.Services.GetRequiredService<ILogger<Program>>();
            logger.LogInformation("🚀 Application starting in {Environment} environment", environment);
            logger.LogInformation("📦 PostgreSQL connection string: {Connection}", connectionString);
            logger.LogInformation("🔗 Redis connection: {Redis}", config.GetConnectionString("Redis"));

            app.UseMiddleware<JwtBlacklistMiddleware>();

            // Apply pending migrations automatically
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                if (db.Database.GetPendingMigrations().Any())
                {
                    logger.LogInformation("🛠 Applying pending migrations...");
                    db.Database.Migrate();
                    logger.LogInformation("✅ Database migrations applied successfully.");
                }
                else
                {
                    logger.LogInformation("✅ No pending migrations found.");
                }
            }
            app.Seed();
            app.UseSwagger(c =>
            {
                c.PreSerializeFilters.Add((swaggerDoc, httpReq) =>
                {
                    var publicBaseUrl = config["Swagger:PublicBaseUrl"];
                    swaggerDoc.Servers = new List<OpenApiServer>
                    {
                        new OpenApiServer { Url = $"/", Description = "Direct API Access" },
                    };
                });
            });

            app.UseSwaggerUI();
            app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

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
