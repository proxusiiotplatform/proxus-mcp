# Packs the Native AOT binaries a release already built; nothing is compiled here.
# The release workflow unpacks linux-x64 into dist/amd64 and linux-arm64 into dist/arm64.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled
ARG TARGETARCH
# The MCP Registry checks that the image belongs to the server named in server.json.
LABEL io.modelcontextprotocol.server.name="io.github.proxusiiotplatform/proxus-mcp"
COPY dist/${TARGETARCH}/proxus-mcp /usr/local/bin/proxus-mcp
WORKDIR /config
# MCP talks over stdin/stdout: docker run -i --rm -v "$PWD:/config" ghcr.io/proxusiiotplatform/proxus-mcp
ENTRYPOINT ["/usr/local/bin/proxus-mcp"]
CMD ["--config", "/config/proxus-mcp.json"]
