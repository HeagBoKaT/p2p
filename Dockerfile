# Проверка компиляции WPF-проекта в изоляции от хоста (см. CLAUDE.md).
# Только build/publish: контейнер без desktop-сессии не может запустить сам GUI —
# для реального прогона нужна Windows-машина с интерактивным логином.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY p2p.csproj .
RUN dotnet restore p2p.csproj

COPY . .
RUN dotnet build p2p.csproj -c Release --no-restore

# Раскомментировать для проверки однофайловой публикации (без запуска — только сборка артефакта):
# RUN dotnet publish p2p.csproj -c Release -o /out --no-restore -p:RuntimeIdentifier=win-x64
