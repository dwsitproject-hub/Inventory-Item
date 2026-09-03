FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src
COPY BcInventory.Api.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine
WORKDIR /app
COPY --from=build /app .
# AR-07: run unprivileged, so a flaw in the parser or a dependency does not start as root.
# The data-protection key volume is mounted at this user's home, so it must own it.
RUN addgroup -S bcinv && adduser -S -G bcinv -h /home/bcinv bcinv  && mkdir -p /home/bcinv/.aspnet/DataProtection-Keys  && chown -R bcinv:bcinv /app /home/bcinv
USER bcinv
ENV HOME=/home/bcinv
EXPOSE 8080
ENTRYPOINT ["dotnet", "BcInventory.Api.dll"]
