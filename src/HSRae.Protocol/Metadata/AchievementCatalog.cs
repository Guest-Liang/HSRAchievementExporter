using System.Globalization;
using System.Text.Json;

namespace HSRae.Protocol.Metadata;

public sealed record AchievementCatalog
{
    private const string ResourceName = "HSRae.Metadata.AchievementInfo.json";

    public required IReadOnlySet<uint> Ids { get; init; }

    public required string LatestVersion { get; init; }

    public int Count => Ids.Count;

    public static AchievementCatalog LoadBundled()
    {
        var assembly = typeof(AchievementCatalog).Assembly;
        using var stream =
            assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"缺少内嵌成就元数据资源：{ResourceName}");
        using var document = JsonDocument.Parse(stream);

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("内嵌 AchievementInfo.json 的根节点不是对象");
        }

        var ids = new HashSet<uint>();
        Version? latestVersion = null;
        var latestVersionText = "unknown";

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (
                !uint.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                || id is < 4_000_000 or > 4_999_999
                || !ids.Add(id)
            )
            {
                throw new InvalidDataException("内嵌 AchievementInfo.json 含无效或重复的星铁成就 ID");
            }

            if (
                property.Value.ValueKind != JsonValueKind.Object
                || !property.Value.TryGetProperty("AchievementID", out var idElement)
                || !idElement.TryGetUInt32(out var embeddedId)
                || embeddedId != id
            )
            {
                throw new InvalidDataException(
                    $"内嵌 AchievementInfo.json 的条目 {property.Name} 缺少匹配的 AchievementID"
                );
            }

            if (
                !property.Value.TryGetProperty("Version", out var versionElement)
                || versionElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(versionElement.GetString())
            )
            {
                throw new InvalidDataException($"内嵌 AchievementInfo.json 的条目 {property.Name} 缺少有效的 Version");
            }

            var versionText = versionElement.GetString()!.Trim();
            if (!Version.TryParse(versionText, out var version))
            {
                throw new InvalidDataException(
                    $"内嵌 AchievementInfo.json 的条目 {property.Name} 含无效版本号 {versionText}"
                );
            }

            if (latestVersion is null || version > latestVersion)
            {
                latestVersion = version;
                latestVersionText = versionText;
            }
        }

        if (ids.Count < 1_000)
        {
            throw new InvalidDataException($"内嵌 AchievementInfo.json 仅有 {ids.Count} 个 ID，疑似不完整");
        }

        return new AchievementCatalog { Ids = ids, LatestVersion = latestVersionText };
    }
}
