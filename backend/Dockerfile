FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# copy csproj and restore
COPY ./BackendServer.csproj ./
RUN dotnet restore ./BackendServer.csproj

# copy everything and build
COPY . ./
RUN dotnet publish ./BackendServer.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

# Install LibreOffice and font tooling in the runtime image so the container can convert PPT/PPTX -> PDF and use MS fonts.
# This increases image size but is required for server-side conversion without external services.
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
       libreoffice libreoffice-writer libreoffice-impress fonts-dejavu-core \
       fontconfig cabextract xfonts-utils ttf-mscorefonts-installer poppler-utils \
    && rm -rf /var/lib/apt/lists/* \
    && fc-cache -f -v

# Optional: copy user-supplied licensed fonts (e.g., Calibri, Segoe UI) into the image.
# Place font files (.ttf/.otf) in a top-level ./fonts directory in the repo before building.
COPY ./fonts /usr/share/fonts/truetype/custom/
RUN chmod -R 755 /usr/share/fonts/truetype/custom/ || true \
    && fc-cache -f -v || true

# Expose HTTP and HTTPS ports
EXPOSE 5000 5001

ENV ASPNETCORE_URLS=https://+:5001;http://+:5000

ENTRYPOINT ["dotnet", "BackendServer.dll"]
