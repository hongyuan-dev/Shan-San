# ===== BUILD =====
FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build
WORKDIR /app

# Copia tutto il progetto
COPY . .

# Pubblica in Release
RUN dotnet publish -c Release -o out

# ===== RUNTIME =====
FROM mcr.microsoft.com/dotnet/runtime:7.0
WORKDIR /app

# Copia output build
COPY --from=build /app/out .

# Avvio bot
ENTRYPOINT ["dotnet", "MyDiscordBot.dll"]
