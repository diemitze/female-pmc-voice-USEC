using System.Collections;
using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Servers;
using Range = SemanticVersioning.Range;

namespace FemalePMCVoice;

public record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "com.20fpsguy.femalevoice";
    public override string Name { get; init; } = "Female PMC Voice";
    public override string Author { get; init; } = "20fpsguy";
    public override SemanticVersioning.Version Version { get; init; } =
        new(typeof(ModMetadata).Assembly.GetName().Version!.ToString(3));
    public override Range SptVersion { get; init; } = new("~4.0.13");
    public override string License { get; init; } = "CC BY-NC-ND 4.0";
    public override bool? IsBundleMod { get; init; } = true;
    public override Dictionary<string, Range>? ModDependencies { get; init; } = new()
    {
        { "com.wtt.commonlib", new Range("~2.0.0") }
    };
    public override string? Url { get; init; }
    public override List<string>? Contributors { get; init; }
    public override List<string>? Incompatibilities { get; init; }
}

[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 2)]
public class FemalePMCVoiceMod(
    WTTServerCommonLib.WTTServerCommonLib wttCommon,
    DatabaseServer databaseServer
) : IOnLoad
{
    // Customization item ID from db/CustomVoices/female_pmc.json (the outer JSON key).
    private const string VoiceId = "67a1c4f2e8b3d5a09f12c7b4";

    // Bot type names matching the files under SPT_Data/database/bots/types/ (no .json).
    private static readonly string[] BotTypes = ["pmcusec"];

    // Weight in the USEC voice pool (~7500 per stock entry). ~1.25% spawn chance.
    private const int BotVoiceWeight = 476;

    public async Task OnLoad()
    {
        var assembly = Assembly.GetExecutingAssembly();
        await wttCommon.CustomVoiceService.CreateCustomVoices(assembly);

        AddVoiceToBotPool();
    }

    // Bots/BotType/Appearance have no public indexer and don't match the JSON keys,
    // so we reach the Voice dictionary by reflection.
    private void AddVoiceToBotPool()
    {
        try
        {
            var botsObj = (object?)databaseServer.GetTables()?.Bots;
            if (botsObj is null) return;

            // Find the internal dictionary field on the Bots wrapper class.
            var internalDict = FindDictionary(botsObj);
            if (internalDict is null) return;

            foreach (var botTypeName in BotTypes)
            {
                if (!internalDict.Contains(botTypeName)) continue;

                var botObj = internalDict[botTypeName];
                if (botObj is null) continue;

                // Navigate: BotType → Appearance → Voice (Dictionary<string, int>)
                var appearance = GetProp(botObj, "Appearance");
                if (appearance is null) continue;

                var voice = GetProp(appearance, "Voice") as IDictionary;
                if (voice is null) continue;

                voice[VoiceId] = BotVoiceWeight;
            }
        }
        catch (Exception ex)
        {
            // Non-fatal — voice just won't appear on bots.
            Console.WriteLine($"[FemalePMCVoice] AddVoiceToBotPool failed: {ex.Message}");
        }
    }

    private static IDictionary? FindDictionary(object obj)
    {
        // The bot-type data lives in a private dictionary field on the wrapper.
        foreach (var field in obj.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var val = field.GetValue(obj);
            if (val is IDictionary dict && dict.Count > 0)
                return dict;
        }
        return null;
    }

    private static object? GetProp(object obj, string name) =>
        obj.GetType().GetProperty(name)?.GetValue(obj);
}
