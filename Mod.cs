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
    public string ModGuid { get; init; } = "com.20fpsguy.femalepmcvoice";
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

// Must run before SaveCallbacks: profiles are validated there, and an unknown voice id gets
// reset to a vanilla one.
[Injectable(TypePriority = OnLoadOrder.GameCallbacks + 2)]
public class FemalePMCVoiceMod(
    WTTServerCommonLib.WTTServerCommonLib wttCommon,
    BotTable botTable,
    ISptLogger<FemalePMCVoiceMod> logger
) : IOnLoad
{
    // Outer key in db/CustomVoices/female_pmc.json.
    private const string VoiceId = "67a1c4f2e8b3d5a09f12c7b4";

    // Resolved from the DLL so a renamed mod folder still finds its config.
    private static readonly string ConfigPath = SysPath.Combine(
        SysPath.GetDirectoryName(typeof(FemalePMCVoiceMod).Assembly.Location)!, "config", "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        await wttCommon.CustomVoiceService.CreateCustomVoices(Assembly.GetExecutingAssembly());
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

        foreach (var key in cfg.BotTypes.Select(NormalizeBotType).Distinct())
        {
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

            // Derived from the current total so the share holds when other mods add voices.
            voice.Remove(new MongoId(VoiceId));
            var othersTotal = voice.Values.Sum();
            if (othersTotal <= 0d)
            {
                logger.Warning($"[FemalePMCVoice] bot type '{key}' voice pool is empty - skipped.");
                continue;
            }

            // At least 1, or a tiny chance on a small pool rounds to never.
            var weight = Math.Max(1d, Math.Round(othersTotal * share / (1d - share)));
            voice[new MongoId(VoiceId)] = weight;

            var actual = weight / (othersTotal + weight) * 100d;
            logger.Success(
                $"[FemalePMCVoice] '{key}': weight {weight} of {othersTotal + weight} -> {actual:F1}% of bots.");
        }
    }

    // PMCs are generated from the side template (usec/bear), not pmcusec/pmcbear, so
    // configs from 1.0.5 and earlier pointed at a pool that is never rolled.
    private static string NormalizeBotType(string name)
    {
        var key = name.ToLowerInvariant();
        return key switch
        {
            "pmcusec" => "usec",
            "pmcbear" => "bear",
            _ => key,
        };
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
    public List<string> BotTypes { get; init; } = ["usec"];
}
