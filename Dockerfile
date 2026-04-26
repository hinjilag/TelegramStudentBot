FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY TelegramStudentBot.csproj ./
RUN dotnet restore TelegramStudentBot.csproj

COPY . ./
RUN dotnet publish TelegramStudentBot.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish ./

ENTRYPOINT ["dotnet", "TelegramStudentBot.dll"]
