FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY src/HackerNews.BestStories.Api/HackerNews.BestStories.Api.csproj src/HackerNews.BestStories.Api/
RUN dotnet restore src/HackerNews.BestStories.Api/HackerNews.BestStories.Api.csproj

COPY src/ src/
RUN dotnet publish src/HackerNews.BestStories.Api/HackerNews.BestStories.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "HackerNews.BestStories.Api.dll"]
