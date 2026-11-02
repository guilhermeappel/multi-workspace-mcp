# Base images are pinned by digest (the tag is kept for readability); Dependabot updates both.

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2 AS build
WORKDIR /src
# .editorconfig carries the analyzer severities; without it the Recommended set fails the build on rules the project turns off.
COPY global.json nuget.config .editorconfig Directory.Build.props Directory.Packages.props ./
COPY src/NotionMultiMcp/NotionMultiMcp.csproj src/NotionMultiMcp/
RUN dotnet restore src/NotionMultiMcp/NotionMultiMcp.csproj
COPY src/ src/
RUN dotnet publish src/NotionMultiMcp/NotionMultiMcp.csproj -c Release -o /app --no-restore

# ---------- runtime ----------
# "chiseled": no shell, non-root user, small image -> fast cold start after scaling to zero.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:5165aeeb688df15e869a56a7584bb94bf16a14e0b3ba77b49fcdbe350f6b8020
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "NotionMultiMcp.dll"]
