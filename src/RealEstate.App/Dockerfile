# NOTE: src/ is copied BEFORE restore to avoid MSBuild glob cache bug (SDK 10.0.103)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ .
RUN dotnet restore "RealEstate.App/RealEstate.App.csproj"
RUN dotnet publish "RealEstate.App/RealEstate.App.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "RealEstate.App.dll"]
