# Base images are pinned by digest (the tag is kept for readability); Dependabot updates both.

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /src
# .editorconfig carries the analyzer severities; without it the Recommended set fails the build on rules the project turns off.
COPY global.json nuget.config .editorconfig Directory.Build.props Directory.Packages.props ./
COPY src/NotionMultiMcp/NotionMultiMcp.csproj src/NotionMultiMcp/
RUN dotnet restore src/NotionMultiMcp/NotionMultiMcp.csproj
COPY src/ src/
RUN dotnet publish src/NotionMultiMcp/NotionMultiMcp.csproj -c Release -o /app --no-restore

# ---------- runtime ----------
# "chiseled": no shell, non-root user, small image -> fast cold start after scaling to zero.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:c89a6a61f494f20a5ae302990e814627a8d91b332fd4a8e7c1707c2d4da9555d
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "NotionMultiMcp.dll"]
