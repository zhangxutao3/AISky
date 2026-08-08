namespace AISky_Desktop.Core;

public sealed record ColorPaletteDefinition(
    string Id,
    string Name,
    IReadOnlyList<string> Colors);

public static class ColorPaletteCatalog
{
    public static IReadOnlyList<ColorPaletteDefinition> All { get; } =
    [
        new("viridis", "Viridis", ["#440154", "#482878", "#3E4989", "#31688E", "#26828E", "#1F9E89", "#35B779", "#6DCD59", "#B4DE2C", "#FDE725"]),
        new("turbo", "Turbo", ["#30123B", "#4145AB", "#4675ED", "#39A2FC", "#1BCFD4", "#24E27A", "#80EA50", "#C9E43C", "#F9BA38", "#F57D15", "#D93806", "#7A0403"]),
        new("plasma", "Plasma", ["#0D0887", "#46039F", "#7201A8", "#9C179E", "#BD3786", "#D8576B", "#ED7953", "#FB9F3A", "#F0F921"]),
        new("inferno", "Inferno", ["#000004", "#1B0C41", "#4A0C6B", "#781C6D", "#A52C60", "#CF4446", "#ED6925", "#FB9B06", "#F7D13D", "#FCFFA4"]),
        new("magma", "Magma", ["#000004", "#180F3D", "#440F76", "#721F81", "#9E2F7F", "#CD4071", "#F1605D", "#FD9668", "#FEC98D", "#FCFDBF"]),
        new("cividis", "Cividis", ["#00204C", "#193B6A", "#3C4F70", "#5C6373", "#7B7774", "#9D8B71", "#C1A66B", "#E7C45F", "#FFEA46"]),
        new("coolwarm", "Coolwarm", ["#3B4CC0", "#6788EE", "#9ABBFF", "#C9D7F0", "#E1E1E1", "#F6C4AD", "#EE8468", "#D1493F", "#B40426"]),
        new("rdbu", "RdBu", ["#67001F", "#B2182B", "#D6604D", "#F4A582", "#FDDBC7", "#F7F7F7", "#D1E5F0", "#92C5DE", "#4393C3", "#2166AC", "#053061"]),
        new("spectral", "Spectral", ["#9E0142", "#D53E4F", "#F46D43", "#FDAE61", "#FEE08B", "#FFFFBF", "#E6F598", "#ABDDA4", "#66C2A5", "#3288BD", "#5E4FA2"]),
        new("ylgnbu", "YlGnBu", ["#FFFFD9", "#EDF8B1", "#C7E9B4", "#7FCDBB", "#41B6C4", "#1D91C0", "#225EA8", "#253494", "#081D58"]),
        new("ylorrd", "YlOrRd", ["#FFFFCC", "#FFEDA0", "#FED976", "#FEB24C", "#FD8D3C", "#FC4E2A", "#E31A1C", "#BD0026", "#800026"]),
        new("blues", "Blues", ["#F7FBFF", "#DEEBF7", "#C6DBEF", "#9ECAE1", "#6BAED6", "#4292C6", "#2171B5", "#08519C", "#08306B"]),
        new("greens", "Greens", ["#F7FCF5", "#E5F5E0", "#C7E9C0", "#A1D99B", "#74C476", "#41AB5D", "#238B45", "#006D2C", "#00441B"]),
        new("terrain", "Terrain", ["#333399", "#1689C4", "#27B8A5", "#72C86D", "#C9D65B", "#A9844F", "#D7B98E", "#F2E8D5", "#FFFFFF"]),
        new("ocean", "Ocean", ["#001030", "#003A70", "#0077A8", "#16B9C9", "#73DDD1", "#D8F5E8"]),
        new("rainbow", "经典彩虹", ["#6E40AA", "#4C6EDB", "#2F9CE1", "#22C7B8", "#6DDB73", "#C7E94C", "#F9D642", "#F69A32", "#E44B3B", "#B6225B"]),
        new("greys", "灰度", ["#FFFFFF", "#E5E5E5", "#C6C6C6", "#9E9E9E", "#737373", "#4A4A4A", "#1F1F1F"]),
        new("cubehelix", "Cubehelix", ["#000000", "#1A1530", "#163D4E", "#1F6B5C", "#6A8B52", "#B7A569", "#D9C8A1", "#FFFFFF"]),
        new("twilight", "Twilight", ["#E2D9E2", "#9E9BCC", "#5C64A9", "#2F3B73", "#1E1E2F", "#63324B", "#A75D62", "#D39A8B", "#E2D9E2"]),
        new("hot", "Hot", ["#0B0000", "#5B0000", "#A80000", "#F23A00", "#FF8500", "#FFD000", "#FFFF69", "#FFFFFF"]),
        new("afmhot", "AFM Hot", ["#000000", "#4A0000", "#920000", "#D63800", "#FF7900", "#FFBE28", "#FFFF86", "#FFFFFF"]),
        new("copper", "Copper", ["#000000", "#2D1C12", "#5D3823", "#8C5333", "#B86D42", "#DC8856", "#FFAB72"]),
        new("bone", "Bone", ["#000000", "#24303A", "#485B65", "#718187", "#9BA6A8", "#CDD1C7", "#FFFFFF"]),
        new("spring", "Spring", ["#FF00FF", "#FF2DD2", "#FF5AA5", "#FF8877", "#FFB54A", "#FFE21D", "#FFFF00"]),
        new("summer", "Summer", ["#008066", "#28936B", "#50A771", "#78BA76", "#A0CE7C", "#C8E181", "#FFFF66"]),
        new("autumn", "Autumn", ["#FF0000", "#FF2A00", "#FF5500", "#FF8000", "#FFAA00", "#FFD500", "#FFFF00"]),
        new("winter", "Winter", ["#0000FF", "#002AD5", "#0055AA", "#008080", "#00AA55", "#00D52A", "#00FF00"]),
        new("cool", "Cool", ["#00FFFF", "#2AD5FF", "#55AAFF", "#8080FF", "#AA55FF", "#D52AFF", "#FF00FF"]),
        new("wistia", "Wistia", ["#E4FF7A", "#F3F35C", "#FFD342", "#FFB12E", "#FB8A1D", "#ED5D0C", "#C93B00"]),
        new("gnbu", "GnBu", ["#F7FCF0", "#E0F3DB", "#CCEBC5", "#A8DDB5", "#7BCCC4", "#4EB3D3", "#2B8CBE", "#0868AC", "#084081"]),
        new("pubugn", "PuBuGn", ["#FFF7FB", "#ECE2F0", "#D0D1E6", "#A6BDDB", "#67A9CF", "#3690C0", "#02818A", "#016C59", "#014636"]),
        new("bupu", "BuPu", ["#F7FCFD", "#E0ECF4", "#BFD3E6", "#9EBCDA", "#8C96C6", "#8C6BB1", "#88419D", "#810F7C", "#4D004B"]),
        new("oranges", "Oranges", ["#FFF5EB", "#FEE6CE", "#FDD0A2", "#FDAE6B", "#FD8D3C", "#F16913", "#D94801", "#A63603", "#7F2704"]),
        new("reds", "Reds", ["#FFF5F0", "#FEE0D2", "#FCBBA1", "#FC9272", "#FB6A4A", "#EF3B2C", "#CB181D", "#A50F15", "#67000D"]),
        new("purples", "Purples", ["#FCFBFD", "#EFEDF5", "#DADAEB", "#BCBDDC", "#9E9AC8", "#807DBA", "#6A51A3", "#54278F", "#3F007D"]),
        new("purd", "PuRd", ["#F7F4F9", "#E7E1EF", "#D4B9DA", "#C994C7", "#DF65B0", "#E7298A", "#CE1256", "#980043", "#67001F"]),
        new("rdpu", "RdPu", ["#FFF7F3", "#FDE0DD", "#FCC5C0", "#FA9FB5", "#F768A1", "#DD3497", "#AE017E", "#7A0177", "#49006A"]),
        new("piyg", "PiYG", ["#8E0152", "#C51B7D", "#DE77AE", "#F1B6DA", "#FDE0EF", "#F7F7F7", "#E6F5D0", "#B8E186", "#7FBC41", "#4D9221", "#276419"]),
        new("prgn", "PRGn", ["#40004B", "#762A83", "#9970AB", "#C2A5CF", "#E7D4E8", "#F7F7F7", "#D9F0D3", "#A6DBA0", "#5AAE61", "#1B7837", "#00441B"]),
        new("brbg", "BrBG", ["#543005", "#8C510A", "#BF812D", "#DFC27D", "#F6E8C3", "#F5F5F5", "#C7EAE5", "#80CDC1", "#35978F", "#01665E", "#003C30"]),
        new("puor", "PuOr", ["#7F3B08", "#B35806", "#E08214", "#FDB863", "#FEE0B6", "#F7F7F7", "#D8DAEB", "#B2ABD2", "#8073AC", "#542788", "#2D004B"]),
        new("rdgy", "RdGy", ["#67001F", "#B2182B", "#D6604D", "#F4A582", "#FDDBC7", "#FFFFFF", "#E0E0E0", "#BABABA", "#878787", "#4D4D4D", "#1A1A1A"]),
        new("seismic", "Seismic", ["#00004C", "#0000B3", "#004CFF", "#66B3FF", "#D9ECFF", "#FFFFFF", "#FFD9D9", "#FF8080", "#FF1A1A", "#B30000", "#4C0000"]),
        new("bwr", "Blue–White–Red", ["#0000FF", "#3F3FFF", "#7F7FFF", "#BFBFFF", "#FFFFFF", "#FFBFBF", "#FF7F7F", "#FF3F3F", "#FF0000"]),
        new("hsv", "HSV 环形", ["#FF0000", "#FFFF00", "#00FF00", "#00FFFF", "#0000FF", "#FF00FF", "#FF0000"]),
        new("weather-temp", "气象温度", ["#2A3F9D", "#3677C4", "#48B7D3", "#B8E1D0", "#F2E7B4", "#F4B65D", "#E56A3A", "#B92D52", "#6C164E"]),
        new("weather-precip", "气象降水", ["#F8FCFF", "#CDEEFF", "#7FD5EA", "#35B8C7", "#1A9B78", "#65B34C", "#C9D83E", "#F7C536", "#F28A2B", "#D84339", "#7A1F7A"]),
        new("radar", "雷达反射率", ["#E8F4FF", "#59C8FF", "#1874E8", "#1DBA52", "#73D13D", "#FADB14", "#FA8C16", "#F5222D", "#A8071A", "#722ED1"]),
        new("wind", "风速增强", ["#E8FAFF", "#91DDE8", "#45BFC2", "#38A169", "#A0C93D", "#F2C94C", "#F2994A", "#EB5757", "#9B2C68", "#4A235A"]),
        new("sst", "海表温度", ["#24106F", "#263DA8", "#1C78B7", "#22A7A1", "#6DC47B", "#C6D95A", "#F5CF4A", "#F28A3C", "#D8493E", "#8D1D4F"]),
        new("batlow", "Batlow", ["#011959", "#174577", "#2F6F7E", "#4D9177", "#7DAE6A", "#B7C765", "#E3D76B", "#F4C77E", "#F29E8B", "#E36A8D", "#C33B83"]),
        new("roma", "Roma", ["#7E1700", "#B3421B", "#D87842", "#EDAF78", "#F5DFB4", "#E8E8D5", "#B9DBD1", "#7EC1C4", "#3E96B2", "#1A6591", "#023858"]),
    ];

    public static ColorPaletteDefinition? Find(string? id) =>
        All.FirstOrDefault(item =>
            string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
}
