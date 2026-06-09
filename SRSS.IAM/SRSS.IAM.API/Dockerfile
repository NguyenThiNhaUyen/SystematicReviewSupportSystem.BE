# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081


# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["SRSS.IAM/SRSS.IAM.API/SRSS.IAM.API.csproj", "SRSS.IAM/SRSS.IAM.API/"]
COPY ["SRSS.IAM/SRSS.IAM.Services/SRSS.IAM.Services.csproj", "SRSS.IAM/SRSS.IAM.Services/"]
COPY ["SRSS.IAM/SRSS.IAM.Repositories/SRSS.IAM.Repositories.csproj", "SRSS.IAM/SRSS.IAM.Repositories/"]
COPY ["Shared/Shared/Shared.csproj", "Shared/Shared/"]
RUN dotnet restore "./SRSS.IAM/SRSS.IAM.API/SRSS.IAM.API.csproj"
COPY . .
WORKDIR "/src/SRSS.IAM/SRSS.IAM.API"
RUN dotnet build "./SRSS.IAM.API.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./SRSS.IAM.API.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --chown=app:app --from=publish /app/publish .
ENTRYPOINT ["dotnet", "SRSS.IAM.API.dll"]
