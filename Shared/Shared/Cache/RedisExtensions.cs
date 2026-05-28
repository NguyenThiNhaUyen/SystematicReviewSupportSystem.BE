using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Shared.Cache
{
    public static class RedisExtensions
    {
        private const string DefaultRedisConnectionString = "redis:6379,allowAdmin=true,connectTimeout=10000,syncTimeout=10000,asyncTimeout=10000,connectRetry=5,abortConnect=false,keepAlive=60";

        private static string GetRedisConnectionString(IConfiguration configuration, string connectionStringKey)
        {
            var host = configuration["Redis:Host"];
            var port = configuration["Redis:Port"] ?? "6379";

            return configuration.GetConnectionString("Redis")
                ?? configuration[connectionStringKey]
                ?? configuration["Redis:ConnectionString"]
                ?? (string.IsNullOrWhiteSpace(host) ? null : $"{host}:{port}")
                ?? DefaultRedisConnectionString;
        }

        public static IServiceCollection AddRedisCache(
            this IServiceCollection services,
            IConfiguration configuration,
            string connectionStringKey = "ConnectionStrings:Redis")
        {
            var startupConnectionString = GetRedisConnectionString(configuration, connectionStringKey);
            Console.WriteLine("=== DEBUG REDIS CONNECTION STRING ===");
            Console.WriteLine(startupConnectionString);

            // Register ConnectionMultiplexer as Singleton
            services.AddSingleton<IConnectionMultiplexer>(sp =>
            {
                var connectionString = GetRedisConnectionString(configuration, connectionStringKey);
                
                var configurationOptions = ConfigurationOptions.Parse(connectionString);
                configurationOptions.AbortOnConnectFail = false;
                configurationOptions.ConnectRetry = 5;
                configurationOptions.ConnectTimeout = 10000;
                
                return ConnectionMultiplexer.Connect(configurationOptions);
            });

            // Register Redis Cache Service
            services.AddScoped<IRedisCacheService, RedisCacheService>();

            return services;
        }

        public static IServiceCollection AddRedisCacheWithHealthCheck(
            this IServiceCollection services,
            IConfiguration configuration,
            string connectionStringKey = "ConnectionStrings:Redis")
        {
            services.AddRedisCache(configuration, connectionStringKey);

            // Add health check
            services.AddHealthChecks()
                .AddRedis(
                    GetRedisConnectionString(configuration, connectionStringKey),
                    name: "redis",
                    tags: new[] { "ready", "redis" });

            return services;
        }
    }
}
