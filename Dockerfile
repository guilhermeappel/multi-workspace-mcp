# Base images are pinned by digest (the tag is kept for readability); Dependabot updates both.

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /src
# .editorconfig carries the analyzer severities; without it the Recommended set fails the build on rules the project turns off.
COPY global.json nuget.config .editorconfig Directory.Build.props Directory.Packages.props ./
COPY src/NotionMultiMcp/NotionMultiMcp.csproj src/NotionMultiMcp/
RUN dotnet restore src/NotionMultiMcp/NotionMultiMcp.csproj
COPY src/ src/
RUN dotnet publish src/NotionMultiMcp/NotionMultiMcp.csproj -c Release -o /app --no-restore

# ---------- runtime ----------
# "chiseled": no shell, non-root user, small image -> fast cold start after scaling to zero.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:9651fa59abcdf177c30392cb44a820605ca5d618429ab37acbf6e7c644510b02
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "NotionMultiMcp.dll"]
