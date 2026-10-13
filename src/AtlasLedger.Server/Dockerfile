# The Atlas Ledger server's container image.  Build from the repository root, which is the build context:
#
#   az acr build --registry crtangentatlasprod --image atlasledger-server:<tag> --file src/AtlasLedger.Server/Dockerfile .
#
# Bellerophon's packages come from nuget.org during the restore; nothing from the Bellerophon repository is needed.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/AtlasLedger.Server/AtlasLedger.Server.csproj --configuration Release --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
# appsettings.json binds 127.0.0.1:5180, which nothing outside the container can reach; --urls overrides it.
ENTRYPOINT ["dotnet", "AtlasLedger.Server.dll", "--urls", "http://+:8080"]
