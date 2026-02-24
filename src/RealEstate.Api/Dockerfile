# Build stage
# NOTE: src/ is copied BEFORE restore to avoid MSBuild glob cache bug (SDK 10.0.103)
# Two-step build (restore → build → publish --no-build) bypasses **/*.cs literal-glob CSC error.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ .
RUN dotnet restore "RealEstate.Api/RealEstate.Api.csproj"
RUN dotnet build "RealEstate.Api/RealEstate.Api.csproj" -c Release --no-restore
RUN dotnet publish "RealEstate.Api/RealEstate.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false --no-build

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "RealEstate.Api.dll"]
