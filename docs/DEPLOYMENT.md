# Deployment Guide - Real Estate Aggregator

**Verze**: 1.0.0  
**Datum**: 22. února 2026

---

## 📋 Přehled

Tento dokument popisuje, jak nasadit Real Estate Aggregator v různých prostředích.

---

## 🛠️ Prerekvizity

### Obecné
- **Docker** 24.0+ a **Docker Compose** 2.20+
- **Git** pro verzování kódu

### Pro lokální vývoj (bez Dockeru)
- **.NET SDK 9.0+**
- **Python 3.12+**
- **PostgreSQL 15+**
- **Node.js 20+** (pro dev tools)

---

## 🏠 Lokální development

### 1. Clone repository

```bash
git clone <repository-url>
cd RealEstateAggregator
```

### 2. Spuštění s Docker Compose (doporučeno)

```bash
# Spustit celý stack (DB + API + Scraper)
docker-compose up -d

# Zobrazit logy
docker-compose logs -f

# Zastavit stack
docker-compose down

# Zastavit a smazat volumes (DB data)
docker-compose down -v
```

**Přístup k aplikaci**:
- API: http://localhost:5001
- Swagger UI: http://localhost:5001/swagger
- pgAdmin: http://localhost:5050 (profil `tools`)

### 3. Lokální spuštění bez Dockeru

#### A) PostgreSQL

```bash
# Spustit PostgreSQL v Dockeru
docker run --name realestate-db \
  -e POSTGRES_DB=realestate_dev \
  -e POSTGRES_USER=postgres \
  -e POSTGRES_PASSWORD=dev \
  -p 5432:5432 \
  -d postgres:15-alpine

# Nebo nainstalovat lokálně a vytvořit databázi
createdb realestate_dev
```

#### B) .NET Backend

```bash
cd src/RealEstate.Api

# Obnovit závislosti
dotnet restore

# Vytvořit databázové migrace
dotnet ef database update

# Spustit aplikaci
dotnet run

# Aplikace běží na https://localhost:5001
```

#### C) Python Scraper

```bash
cd scraper

# Vytvořit virtuální prostředí
python -m venv venv

# Aktivovat
source venv/bin/activate  # Linux/Mac
# venv\Scripts\activate    # Windows

# Nainstalovat závislosti
pip install -r requirements.txt

# Nastavit environment variables
export DB_HOST=localhost
export DB_NAME=realestate_dev
export DB_USER=postgres
export DB_PASSWORD=dev

# Spustit jednorázově
python -m core.runner

# Nebo spustit scheduler (běží na pozadí)
python -m core.scheduler
```

---

## 🔧 Konfigurace

### appsettings.json (.NET)

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=realestate_dev;Username=postgres;Password=dev"
  },
  "GoogleDrive": {
    "FolderId": "your-google-drive-folder-id",
    "CredentialsPath": "credentials.json"
  },
  "OneDrive": {
    "ClientId": "your-client-id",
    "ClientSecret": "your-client-secret",
    "TenantId": "your-tenant-id",
    "FolderPath": "/RealEstateAnalyses"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

### Environment Variables

Pro produkci použijte environment variables místo hardcoded credentials:

```bash
# .NET
export ConnectionStrings__DefaultConnection="Host=prod-db;Database=realestate;Username=app;Password=xxx"
export GoogleDrive__FolderId="xxx"

# Python
export DB_HOST=prod-db
export DB_NAME=realestate
export DB_USER=app
export DB_PASSWORD=xxx
```

---

## ☁️ Cloud Deployment

### Azure App Service

#### 1. Vytvořit Azure Resources

```bash
# Resource Group
az group create --name rg-realestate --location westeurope

# PostgreSQL
az postgres flexible-server create \
  --name realestate-db \
  --resource-group rg-realestate \
  --location westeurope \
  --admin-user pgadmin \
  --admin-password <strong-password> \
  --sku-name Standard_B1ms \
  --tier Burstable

# Database
az postgres flexible-server db create \
  --resource-group rg-realestate \
  --server-name realestate-db \
  --database-name realestate

# App Service Plan
az appservice plan create \
  --name plan-realestate \
  --resource-group rg-realestate \
  --sku B1 \
  --is-linux

# Web App (.NET)
az webapp create \
  --name app-realestate-api \
  --resource-group rg-realestate \
  --plan plan-realestate \
  --runtime "DOTNET|9.0"
```

#### 2. Nasadit aplikaci

```bash
# Build a publikace
cd src/RealEstate.Api
dotnet publish -c Release -o ./publish

# Zip a deploy
cd publish
zip -r ../app.zip .
cd ..
az webapp deployment source config-zip \
  --resource-group rg-realestate \
  --name app-realestate-api \
  --src app.zip
```

#### 3. Nastavit connection string

```bash
az webapp config connection-string set \
  --resource-group rg-realestate \
  --name app-realestate-api \
  --connection-string-type PostgreSQL \
  --settings DefaultConnection="Host=realestate-db.postgres.database.azure.com;Database=realestate;Username=pgadmin;Password=xxx;SSL Mode=Require"
```

#### 4. Python Scraper jako Azure Container Instance

```bash
# Build Docker image
cd scraper
docker build -t realestate-scraper .

# Push do Azure Container Registry
az acr create --resource-group rg-realestate --name acrrealestate --sku Basic
az acr login --name acrrealestate
docker tag realestate-scraper acrrealestate.azurecr.io/scraper:latest
docker push acrrealestate.azurecr.io/scraper:latest

# Deploy jako Container Instance
az container create \
  --resource-group rg-realestate \
  --name scraper-instance \
  --image acrrealestate.azurecr.io/scraper:latest \
  --registry-username <acr-username> \
  --registry-password <acr-password> \
  --environment-variables \
    DB_HOST=realestate-db.postgres.database.azure.com \
    DB_NAME=realestate \
    DB_USER=pgadmin \
    DB_PASSWORD=<password> \
  --restart-policy Always
```

---

### AWS (ECS + RDS)

#### 1. RDS PostgreSQL

```bash
# Vytvořit PostgreSQL RDS instance
aws rds create-db-instance \
  --db-instance-identifier realestate-db \
  --db-instance-class db.t3.micro \
  --engine postgres \
  --engine-version 15.5 \
  --master-username pgadmin \
  --master-user-password <password> \
  --allocated-storage 20 \
  --vpc-security-group-ids <sg-id> \
  --db-subnet-group-name <subnet-group> \
  --publicly-accessible
```

#### 2. Deploy na ECS Fargate

**Dockerfile** pro API je v `src/RealEstate.Api/Dockerfile`:

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["src/RealEstate.Api/RealEstate.Api.csproj", "RealEstate.Api/"]
COPY ["src/RealEstate.Domain/RealEstate.Domain.csproj", "RealEstate.Domain/"]
COPY ["src/RealEstate.Infrastructure/RealEstate.Infrastructure.csproj", "RealEstate.Infrastructure/"]
COPY ["src/RealEstate.Background/RealEstate.Background.csproj", "RealEstate.Background/"]
RUN dotnet restore "RealEstate.Api/RealEstate.Api.csproj"
COPY src/ .
WORKDIR "/src/RealEstate.Api"
RUN dotnet build "RealEstate.Api.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "RealEstate.Api.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "RealEstate.Api.dll"]
```

Build a push:

```bash
# ECR login
aws ecr get-login-password --region eu-west-1 | docker login --username AWS --password-stdin <account-id>.dkr.ecr.eu-west-1.amazonaws.com

# Build
docker build -t realestate-api -f src/RealEstate.Api/Dockerfile .
docker tag realestate-api:latest <account-id>.dkr.ecr.eu-west-1.amazonaws.com/realestate-api:latest
docker push <account-id>.dkr.ecr.eu-west-1.amazonaws.com/realestate-api:latest

# Deploy přes ECS CLI nebo Console
```

---

## 🗄️ Database Migrations

### .NET (Entity Framework Core)

```bash
# Vytvořit novou migraci
cd src/RealEstate.Api
dotnet ef migrations add <MigrationName> --project ../RealEstate.Infrastructure

# Aplikovat migrace na databázi
dotnet ef database update

# Rollback
dotnet ef database update <PreviousMigrationName>

# Vygenerovat SQL script
dotnet ef migrations script -o migration.sql
```

### Python (Alembic)

```bash
cd scraper

# Vytvořit migraci
alembic revision --autogenerate -m "Migration message"

# Aplikovat migrace
alembic upgrade head

# Rollback
alembic downgrade -1
```

---

## 📊 Monitoring & Logging

### Application Insights (Azure)

```bash
# Přidat NuGet balíček
dotnet add package Microsoft.ApplicationInsights.AspNetCore

# V Program.cs
builder.Services.AddApplicationInsightsTelemetry();
```

### CloudWatch (AWS)

Pro ECS:

```json
{
  "logConfiguration": {
    "logDriver": "awslogs",
    "options": {
      "awslogs-group": "/ecs/realestate",
      "awslogs-region": "eu-west-1",
      "awslogs-stream-prefix": "api"
    }
  }
}
```

### Structured Logging

Python:

```python
import logging
from pythonjsonlogger import jsonlogger

logger = logging.getLogger()
logHandler = logging.FileHandler("logs/scraper.log")
formatter = jsonlogger.JsonFormatter()
logHandler.setFormatter(formatter)
logger.addHandler(logHandler)
```

---

## 🔒 Bezpečnost

### 1. Secrets Management

**Azure Key Vault**:

```bash
# Vytvořit Key Vault
az keyvault create --name kv-realestate --resource-group rg-realestate --location westeurope

# Uložit secret
az keyvault secret set --vault-name kv-realestate --name "DbPassword" --value "xxx"

# V .NET
builder.Configuration.AddAzureKeyVault(new Uri("https://kv-realestate.vault.azure.net/"), new DefaultAzureCredential());
```

**AWS Secrets Manager**:

```bash
aws secretsmanager create-secret --name realestate/db-password --secret-string "xxx"
```

### 2. API Authentication

Pro MVP není autentizace, ale pro produkci:

```csharp
// Program.cs
builder.Services.AddAuthentication("ApiKey")
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>("ApiKey", null);
```

---

## 🧪 Health Checks

### .NET

```csharp
// Program.cs
builder.Services.AddHealthChecks()
    .AddNpgSql(connectionString)
    .AddCheck("self", () => HealthCheckResult.Healthy());

app.MapHealthChecks("/health");
```

### Python

Jednoduchý Flask endpoint:

```python
from flask import Flask, jsonify
app = Flask(__name__)

@app.route('/health')
def health():
    return jsonify({"status": "healthy", "service": "scraper"})
```

---

## 🔄 CI/CD Pipeline

### GitHub Actions

`.github/workflows/dotnet.yml`:

```yaml
name: .NET CI/CD

on:
  push:
    branches: [ main, develop ]
  pull_request:
    branches: [ main ]

jobs:
  build:
    runs-on: ubuntu-latest
    
    steps:
    - uses: actions/checkout@v3
    
    - name: Setup .NET
      uses: actions/setup-dotnet@v3
      with:
        dotnet-version: '9.0.x'
    
    - name: Restore dependencies
      run: dotnet restore
    
    - name: Build
      run: dotnet build --no-restore -c Release
    
    - name: Test
      run: dotnet test --no-build --verbosity normal
    
    - name: Publish
      run: dotnet publish src/RealEstate.Api/RealEstate.Api.csproj -c Release -o ./publish
    
    - name: Deploy to Azure
      if: github.ref == 'refs/heads/main'
      uses: azure/webapps-deploy@v2
      with:
        app-name: 'app-realestate-api'
        publish-profile: ${{ secrets.AZURE_WEBAPP_PUBLISH_PROFILE }}
        package: ./publish
```

---

## 📈 Scaling

### Horizontální škálování

**Azure**:
```bash
az appservice plan update --name plan-realestate --resource-group rg-realestate --number-of-workers 3
```

**AWS ECS**:
```json
{
  "desiredCount": 3,
  "deploymentConfiguration": {
    "maximumPercent": 200,
    "minimumHealthyPercent": 100
  }
}
```

### Database Connection Pooling

**.NET**:
```json
"ConnectionStrings": {
  "DefaultConnection": "Host=db;Database=x;Username=x;Password=x;Pooling=true;MinPoolSize=5;MaxPoolSize=100"
}
```

**Python**:
```yaml
database:
  min_connections: 10
  max_connections: 50
```

---

## 🛟 Troubleshooting

### Časté problémy

**1. Database connection refused**
```bash
# Zkontrolovat, zda PostgreSQL běží
docker ps | grep postgres

# Zkontrolovat logy
docker logs realestate-db

# Test připojení
psql -h localhost -U postgres -d realestate_dev
```

**2. EF Core migrace selhává**
```bash
# Zkontrolovat connection string
dotnet ef database update --verbose

# Vymazat migrace a začít znovu
dotnet ef migrations remove
dotnet ef migrations add Initial
```

**3. Scraper nenachází inzeráty**
```bash
# Zkontrolovat logs
tail -f scraper/logs/scraper.log

# Spustit manuálně s verbose logging
cd scraper
python -m core.runner --verbose
```

---

## 📞 Podpora

Pro otázky kontaktujte vlastníka projektu nebo otevřete issue na GitHubu.

---

**Konec Deployment Guide** • Verze 1.0 • 22. února 2026
