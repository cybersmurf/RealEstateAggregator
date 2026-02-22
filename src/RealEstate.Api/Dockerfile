# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj files and restore
COPY ["src/RealEstate.Api/RealEstate.Api.csproj", "RealEstate.Api/"]
COPY ["src/RealEstate.Domain/RealEstate.Domain.csproj", "RealEstate.Domain/"]
COPY ["src/RealEstate.Infrastructure/RealEstate.Infrastructure.csproj", "RealEstate.Infrastructure/"]
RUN dotnet restore "RealEstate.Api/RealEstate.Api.csproj"

# Copy everything else and build
COPY src/ .
WORKDIR "/src/RealEstate.Api"
RUN dotnet build "RealEstate.Api.csproj" -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish "RealEstate.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=publish /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "RealEstate.Api.dll"]
