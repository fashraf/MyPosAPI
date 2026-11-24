# MyPosAPI image: the API plus the SQL migrations it needs (migrate.sh reads them from /app/db-migrations).
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Shared/ Shared/
COPY src/ src/
RUN dotnet publish src/PosApi/PosApi.csproj -c Release -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out .
COPY db-migrations/ /app/db-migrations/
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "PosApi.dll"]
