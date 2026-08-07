using AISky_Desktop.DataWorker;

namespace AISky_Desktop.Core;

public sealed record LayerDisplayDefinition(
    string Unit,
    double Minimum,
    double Maximum,
    double Scale = 1,
    double Offset = 0);

public static class LayerDisplayCatalog
{
    private static readonly IReadOnlyDictionary<string, LayerDisplayDefinition> Definitions =
        new Dictionary<string, LayerDisplayDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["t2m"] = new("°C", -10, 40),
            ["t10m"] = new("°C", -10, 40),
            ["ts"] = new("°C", -10, 40),
            ["qv2m"] = new("kg/kg", 0, 0.025, 0.001),
            ["qv10m"] = new("kg/kg", 0, 0.025, 0.001),
            ["swgdn"] = new("W/m²", 0, 1000),
            ["swgdnclr"] = new("W/m²", 0, 1000),
            ["swtdn"] = new("W/m²", 0, 1000),
            ["swgnt"] = new("W/m²", 0, 1000),
            ["cldtot"] = new("无", 0, 1, 0.01),
            ["cldlow"] = new("无", 0, 1, 0.01),
            ["cldmid"] = new("无", 0, 1, 0.01),
            ["cldhgh"] = new("无", 0, 1, 0.01),
            ["tautot"] = new("无", 0, 100),
            ["albedo"] = new("无", 0, 1, 0.01),
            ["totexttau"] = new("无", 0, 1),
            ["wind10"] = new("m/s", 0, 30),
            ["wind50"] = new("m/s", 0, 30),
            ["slp"] = new("hPa", 950, 1050),
            ["ps"] = new("hPa", 950, 1050),
            ["tqv"] = new("kg/m²", 0, 100),
            ["duexttau"] = new("无", 0, 0.5),
            ["duextt25"] = new("无", 0, 0.5),
            ["duscatau"] = new("无", 0, 0.5),
            ["duscat25"] = new("无", 0, 0.5),
            ["ducmass"] = new("g/m²", 0, 1, 0.001),
            ["ducmass25"] = new("g/m²", 0, 0.5, 0.001),
            ["dusmass"] = new("μg/m³", 0, 500),
            ["dusmass25"] = new("μg/m³", 0, 500),
            ["duflux"] = new("g/(m·s)", 0, 1.4, 1000),
            ["prectot"] = new("g/(m²·s)", 0, 1, 1d / 86.4d),
            ["pblh"] = new("m", 0, 3000),
            ["ustar"] = new("m/s", 0, 1),
            ["z0m"] = new("m", 0, 10),
            ["gwettop"] = new("无", 0, 1, 0.01),
            ["frsno"] = new("无", 0, 1, 0.01),
            ["lai"] = new("无", 0, 1),
            ["u2m"] = new("m/s", -20, 20),
            ["v2m"] = new("m/s", -20, 20),
            ["u10m"] = new("m/s", -20, 20),
            ["v10m"] = new("m/s", -20, 20),
            ["u50m"] = new("m/s", -20, 20),
            ["v50m"] = new("m/s", -20, 20),
            ["dufluxu"] = new("g/(m·s)", -1, 1),
            ["dufluxv"] = new("g/(m·s)", -1, 1),
            ["dudp"] = new("ng/(m²·s)", 0, 500),
            ["duem"] = new("ng/(m²·s)", 0, 1000),
            ["duwt"] = new("ng/(m²·s)", 0, 1000),
        };

    public static LayerDisplayDefinition Resolve(ForecastLayer layer) =>
        Definitions.TryGetValue(layer.Id, out var definition)
            ? definition
            : new LayerDisplayDefinition(
                string.IsNullOrWhiteSpace(layer.Unit) ? "无" : layer.Unit,
                layer.Range.FirstOrDefault(),
                layer.Range.Count > 1 ? layer.Range[^1] : layer.Range.FirstOrDefault());

    public static List<double> Range(ForecastLayer layer)
    {
        var definition = Resolve(layer);
        return [definition.Minimum, definition.Maximum];
    }
}
