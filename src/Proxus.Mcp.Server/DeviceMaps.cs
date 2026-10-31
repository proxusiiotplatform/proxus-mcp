using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Proxus.Mcp;

/// <summary>
///     A device's register map in the JSON form of the Modbus Register Maps collection
///     (https://github.com/proxusiiotplatform/modbus-register-maps, built with tools/maps.py build).
///     Addresses are zero-based protocol addresses.
/// </summary>
public sealed class DeviceMap
{
    public string Id { get; set; } = string.Empty;
    public string Vendor { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public List<string> Aliases { get; set; } = [];
    public string Category { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public MapConnection Connection { get; set; } = new();
    public List<MapSource> Sources { get; set; } = [];
    public List<JsonElement> Verified { get; set; } = [];
    public List<MapRegister> Registers { get; set; } = [];

    /// <summary>SHA-256 of the map's register content, as tools/maps.py build computes it; verification reports name it.</summary>
    public string? ContentSha256 { get; set; }

    public string DefaultByteOrder => Connection.DefaultByteOrder ?? "ABCD";
}

public sealed class MapConnection
{
    public List<string> Interfaces { get; set; } = [];
    public int? DefaultUnitId { get; set; }
    public string? DefaultByteOrder { get; set; }
    public List<string> Notes { get; set; } = [];
}

public sealed class MapSource
{
    public string Title { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string Url { get; set; } = string.Empty;
}

public sealed class MapRegister
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Group { get; set; }
    public string Table { get; set; } = "holding";
    public int Address { get; set; }
    public string Type { get; set; } = "uint16";
    public int? Length { get; set; }
    public string? ByteOrder { get; set; }
    public double? Scale { get; set; }
    public string? ScaleFactor { get; set; }
    public string? Unit { get; set; }
    public string Access { get; set; } = "r";
    public Dictionary<string, string>? Values { get; set; }
    public List<string>? AppliesTo { get; set; }
    public string? Notes { get; set; }

    /// <summary>Registers (or bits) the value occupies.</summary>
    public int Count => Type switch
    {
        "string" or "bitfield" or "raw" => Length ?? 1,
        "uint32" or "int32" or "float32" => 2,
        "uint64" or "int64" or "float64" => 4,
        _ => 1
    };

    /// <summary>The address in the notation read_address takes, e.g. hr:3019.</summary>
    public string ToolAddress => Table switch
    {
        "input" => $"ir:{Address}",
        "coil" => $"coil:{Address}",
        "discrete_input" => $"di:{Address}",
        _ => $"hr:{Address}"
    };
}

/// <summary>The register maps this server knows: the ones built into the binary plus any in maps_dir.</summary>
public sealed class DeviceMapLibrary
{
    /// <summary>Embedded at build time when the collection is present (see the project file).</summary>
    private const string EmbeddedResource = "Proxus.Mcp.register-maps.json.gz";

    private readonly Dictionary<string, DeviceMap> _maps = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<DeviceMap> Maps => _maps.Values;

    public int Count => _maps.Count;

    public static DeviceMapLibrary Load(string? mapsDir)
    {
        var library = new DeviceMapLibrary();
        library.LoadEmbedded();
        if (mapsDir is not null && Directory.Exists(mapsDir))
        {
            foreach (var file in Directory.EnumerateFiles(mapsDir, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                if (Path.GetFileName(file) == "index.json")
                    continue;
                library.Add(ReadFile(file));
            }
        }

        return library;
    }

    public static DeviceMap ReadFile(string path)
    {
        using var stream = File.OpenRead(path);
        var map = JsonSerializer.Deserialize(stream, MapJsonContext.Default.DeviceMap)
                  ?? throw new InvalidDataException($"Register map is empty: {path}");
        if (map.Registers.Count == 0)
            throw new InvalidDataException($"Register map has no registers: {path}");
        if (string.IsNullOrEmpty(map.Id))
            map.Id = Path.GetFileNameWithoutExtension(path);
        return map;
    }

    public void Add(DeviceMap map)
    {
        // A map in maps_dir replaces the built-in one with the same id, so a local fix wins.
        _maps[map.Id] = map;
    }

    public DeviceMap? Find(string id)
    {
        return _maps.GetValueOrDefault(id);
    }

    /// <summary>Maps whose vendor, model, aliases, category or id contain every word of the query.</summary>
    public List<DeviceMap> Search(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return _maps.Values
            .Where(map =>
            {
                var haystack = string.Join(' ', [map.Id, map.Vendor, map.Model, map.Category, .. map.Aliases]);
                return words.All(word => haystack.Contains(word, StringComparison.OrdinalIgnoreCase));
            })
            .OrderBy(map => map.Id, StringComparer.Ordinal)
            .ToList();
    }

    private void LoadEmbedded()
    {
        using var resource = typeof(DeviceMapLibrary).Assembly.GetManifestResourceStream(EmbeddedResource);
        if (resource is null)
            return;
        using var gzip = new GZipStream(resource, CompressionMode.Decompress);
        var maps = JsonSerializer.Deserialize(gzip, MapJsonContext.Default.ListDeviceMap) ?? [];
        foreach (var map in maps)
            Add(map);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(DeviceMap))]
[JsonSerializable(typeof(List<DeviceMap>))]
internal sealed partial class MapJsonContext : JsonSerializerContext;
