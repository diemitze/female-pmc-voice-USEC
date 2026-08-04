using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SysPath = System.IO.Path;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.DI;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using Range = SemanticVersioning.Range;

namespace FemalePMCVoice;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.20fpsguy.femalevoice";
    public string Name { get; init; } = "Female PMC Voice";
    public string Author { get; init; } = "20fpsguy";
    public SemanticVersioning.Version Version { get; init; } =
        new(typeof(ModMetadata).Assembly.GetName().Version!.ToString(3));
    public Range SptVersion { get; init; } = new("~4.1.0");
    public string License { get; init; } = "CC BY-NC-ND 4.0";
    public bool HasPrepatcher { get; init; }
    public Dictionary<string, Range>? ModDependencies { get; init; } = new()
    {
        { "com.wtt.commonlib", new Range("~3.0.0") }
    };
    public string? Url { get; init; }
    public List<string>? Contributors { get; init; }
    public List<string>? Incompatibilities { get; init; }
}

// 4.1 dropped PostDBModLoader. Its slot was between GameCallbacks and TraderRegistration,
// and crucially before SaveCallbacks: the voice has to exist in the DB before profiles are
// read, or an unknown voice id gets reset to a vanilla one.
[Injectable(TypePriority = OnLoadOrder.GameCallbacks + 2)]
public class FemalePMCVoiceMod(
    WTTServerCommonLib.WTTServerCommonLib wttCommon,
    BotTable botTable,
    ISptLogger<FemalePMCVoiceMod> logger
) : IOnLoad
{
    // Customization item ID (outer key in db/CustomVoices/female_pmc.json).
    private const string VoiceId = "67a1c4f2e8b3d5a09f12c7b4";

    private static readonly string ConfigPath = SysPath.Combine(
        AppContext.BaseDirectory, "user", "mods", "FemalePMCVoice", "config", "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var assembly = Assembly.GetExecutingAssembly();
        await wttCommon.CustomVoiceService.CreateCustomVoices(assembly);

        AddVoiceToBotPool(LoadConfig());
    }

    private void AddVoiceToBotPool(VoiceConfig cfg)
    {
        var share = Math.Clamp(cfg.BotVoiceChancePercent, 0d, 95d) / 100d;
        if (share <= 0d)
        {
            logger.Info("[FemalePMCVoice] bot voice chance is 0 - bots keep vanilla voices.");
            return;
        }

        foreach (var botTypeName in cfg.BotTypes)
        {
            var key = botTypeName.ToLowerInvariant();
            if (!botTable.Types.TryGetValue(key, out var botType))
            {
                logger.Warning($"[FemalePMCVoice] bot type '{key}' not in the database - skipped.");
                continue;
            }

            var voice = botType?.BotAppearance?.Voice;
            if (voice is null)
            {
                logger.Warning($"[FemalePMCVoice] bot type '{key}' has no voice pool - skipped.");
                continue;
            }

            // Weight is relative to whatever else is in the pool, so derive it from the current
            // total instead of hardcoding a number that silently drifts when other mods add voices.
            voice.Remove(new MongoId(VoiceId));
            var othersTotal = voice.Values.Sum();
            if (othersTotal <= 0d)
            {
                logger.Warning($"[FemalePMCVoice] bot type '{key}' voice pool is empty - skipped.");
                continue;
            }

            var weight = Math.Round(othersTotal * share / (1d - share));
            voice[new MongoId(VoiceId)] = weight;

            var actual = weight / (othersTotal + weight) * 100d;
            logger.Success(
                $"[FemalePMCVoice] '{key}': weight {weight} of {othersTotal + weight} -> {actual:F1}% of bots.");
        }
    }

    private VoiceConfig LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<VoiceConfig>(File.ReadAllText(ConfigPath), JsonOpts);
                if (cfg is not null) return cfg;
            }
            logger.Warning($"[FemalePMCVoice] no config at {ConfigPath} - using defaults.");
        }
        catch (Exception e)
        {
            logger.Error($"[FemalePMCVoice] config unreadable, using defaults: {e.Message}");
        }
        return new VoiceConfig();
    }
}

public record VoiceConfig
{
    [JsonPropertyName("botVoiceChancePercent")]
    public double BotVoiceChancePercent { get; init; } = 15d;

    [JsonPropertyName("botTypes")]
    public List<string> BotTypes { get; init; } = ["pmcusec"];
}
