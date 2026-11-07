FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["TrustVault.Api.csproj", "./"]
RUN dotnet restore "TrustVault.Api.csproj"

COPY . .
RUN dotnet publish "TrustVault.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://0.0.0.0:10000
EXPOSE 10000

ENTRYPOINT ["sh", "-c", "dotnet TrustVault.Api.dll --urls http://0.0.0.0:${PORT:-10000}"]