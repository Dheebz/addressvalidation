FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
ARG PROJECT
RUN dotnet publish "src/${PROJECT}/${PROJECT}.csproj" -c Release -o /app --nologo
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ARG PROJECT
ENV APP_PROJECT=${PROJECT}
COPY --from=build /app .
ENTRYPOINT ["sh", "-c", "dotnet ${APP_PROJECT}.dll"]
