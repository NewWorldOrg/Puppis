ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS driver-build
ARG RID=linux-x64
RUN apt-get update \
    && apt-get install -y --no-install-recommends clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Puppis.Contracts/Puppis.Contracts.csproj src/Puppis.Contracts/
COPY src/Puppis.Driver/Puppis.Driver.csproj src/Puppis.Driver/
RUN dotnet restore src/Puppis.Driver/Puppis.Driver.csproj -r ${RID} -p:PublishAot=true
COPY src/Puppis.Contracts/ src/Puppis.Contracts/
COPY src/Puppis.Driver/ src/Puppis.Driver/
RUN dotnet publish src/Puppis.Driver/Puppis.Driver.csproj -c Release -r ${RID} -p:PublishAot=true -o /out/driver \
    && rm -f /out/driver/*.dbg /out/driver/*.pdb

FROM driver-build AS driver-notices
RUN apt-get update \
    && apt-get install -y --no-install-recommends jq \
    && rm -rf /var/lib/apt/lists/*
COPY docker/notices/ /notices/
RUN /notices/nuget.sh /out/notices driver src/Puppis.Driver/obj/project.assets.json

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS driver-develop
RUN apt-get update \
    && apt-get install -y --no-install-recommends libpcsclite1 \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/runtime-deps:${DOTNET_VERSION} AS runtime-base

FROM runtime-base AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends libpcsclite1 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /opt/puppis
COPY --from=driver-build /out/driver ./

COPY --from=driver-notices /out/notices/ /usr/share/doc/puppis/
COPY LICENSE THIRD-PARTY-NOTICES.md /usr/share/doc/puppis/
RUN --mount=type=bind,from=driver-notices,source=/notices,target=/run/notices \
    --mount=type=bind,from=runtime-base,source=/var/lib/dpkg/status,target=/run/base-dpkg-status \
    /run/notices/check.sh /run/base-dpkg-status

ENV DOTNET_hostBuilder__reloadConfigOnChange=false

ENTRYPOINT ["/opt/puppis/Puppis.Driver"]
