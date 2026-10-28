using System.Text.Json.Serialization;
using TarkovAtlas.Models;

namespace TarkovAtlas;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MapManifest))]
[JsonSerializable(typeof(MapDefinition))]
[JsonSerializable(typeof(TraderInfo))]
[JsonSerializable(typeof(MapLabel))]
[JsonSerializable(typeof(ExtractPoint))]
[JsonSerializable(typeof(BossSpawn))]
[JsonSerializable(typeof(SpawnPoint))]
[JsonSerializable(typeof(TransitPoint))]
[JsonSerializable(typeof(CachePoint))]
[JsonSerializable(typeof(GameMarker))]
[JsonSerializable(typeof(List<MapDefinition>))]
[JsonSerializable(typeof(List<TraderInfo>))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(QuestDatabase))]
[JsonSerializable(typeof(Quest))]
[JsonSerializable(typeof(QuestObjective))]
[JsonSerializable(typeof(ObjectiveZone))]
[JsonSerializable(typeof(QuestProgressFile))]
[JsonSerializable(typeof(StoryDatabase))]
[JsonSerializable(typeof(StoryChapter))]
[JsonSerializable(typeof(List<StoryChapter>))]
[JsonSerializable(typeof(Dictionary<string, bool>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<Quest>))]
[JsonSerializable(typeof(MapLayer))]
[JsonSerializable(typeof(List<MapLayer>))]
[JsonSerializable(typeof(HazardZone))]
[JsonSerializable(typeof(List<HazardZone>))]
[JsonSerializable(typeof(Dictionary<string, List<MapLayer>>))]
[JsonSerializable(typeof(Dictionary<string, List<HazardZone>>))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
