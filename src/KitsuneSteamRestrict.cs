using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Timers;

using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API.Modules.Admin;

namespace KitsuneSteamRestrict;

public class PluginConfig : BasePluginConfig
{
    [JsonPropertyName("SteamWebAPI")]
    public string SteamWebAPI { get; set; } = "";

    [JsonPropertyName("MinimumCS2LevelPrime")]
    public int MinimumCS2LevelPrime { get; set; } = -1;

    [JsonPropertyName("MinimumCS2LevelNonPrime")]
    public int MinimumCS2LevelNonPrime { get; set; } = -1;

    [JsonPropertyName("MinimumHourPrime")]
    public int MinimumHourPrime { get; set; } = -1;

    [JsonPropertyName("MinimumHourNonPrime")]
    public int MinimumHourNonPrime { get; set; } = -1;

    [JsonPropertyName("MinimumLevelPrime")]
    public int MinimumLevelPrime { get; set; } = -1;

    [JsonPropertyName("MinimumLevelNonPrime")]
    public int MinimumLevelNonPrime { get; set; } = -1;

    [JsonPropertyName("MinimumSteamAccountAgeInDays")]
    public int MinimumSteamAccountAgeInDays { get; set; } = -1;

    [JsonPropertyName("BlockPrivateProfile")]
    public bool BlockPrivateProfile { get; set; } = false;

    [JsonPropertyName("BlockTradeBanned")]
    public bool BlockTradeBanned { get; set; } = false;

    [JsonPropertyName("BlockVACBanned")]
    public bool BlockVACBanned { get; set; } = false;

    [JsonPropertyName("SteamGroupID")]
    public string SteamGroupID { get; set; } = "";

    [JsonPropertyName("BlockGameBanned")]
    public bool BlockGameBanned { get; set; } = false;
}

[MinimumApiVersion(198)]
public class SteamRestrictPlugin : BasePlugin, IPluginConfig<PluginConfig>
{
    public override string ModuleName => "Steam Restrict";
    public override string ModuleVersion => "1.2.0";
    public override string ModuleAuthor => "K4ryuu, Cruze @ KitsuneLab";
    public override string ModuleDescription => "Restrict certain players from connecting to your server.";

    public readonly HttpClient Client = new HttpClient();
    private bool g_bSteamAPIActivated = false;

    private CounterStrikeSharp.API.Modules.Timers.Timer?[] g_hAuthorize = new CounterStrikeSharp.API.Modules.Timers.Timer?[65];

    public PluginConfig Config { get; set; } = new();

    public void OnConfigParsed(PluginConfig config)
    {
        Config = config;

        if (string.IsNullOrEmpty(config.SteamWebAPI))
        {
            Logger.LogError("This plugin won't work because Web API is empty.");
        }
    }

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnGameServerSteamAPIActivated>(() => { g_bSteamAPIActivated = true; });
        RegisterListener<Listeners.OnClientConnect>((int slot, string name, string ipAddress) => { g_hAuthorize[slot]?.Kill(); });
        RegisterListener<Listeners.OnClientDisconnect>((int slot) => { g_hAuthorize[slot]?.Kill(); });
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull, HookMode.Post);

        if (hotReload)
        {
            g_bSteamAPIActivated = true;

            foreach (var player in Utilities.GetPlayers().Where(m => m.Connected == PlayerConnectedState.PlayerConnected && !m.IsHLTV && !m.IsBot && m.SteamID.ToString().Length == 17))
            {
                OnPlayerConnectFull(player);
            }
        }
    }

    public HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        CCSPlayerController player = @event.Userid;

        if (player == null)
            return HookResult.Continue;

        OnPlayerConnectFull(player);

        return HookResult.Continue;
    }

    private void OnPlayerConnectFull(CCSPlayerController player)
    {
        if (string.IsNullOrEmpty(Config.SteamWebAPI))
            return;

        if (player.IsBot || player.IsHLTV)
            return;

        if (player.AuthorizedSteamID == null)
        {
            g_hAuthorize[player.Slot] = AddTimer(1.0f, () =>
            {
                if (player.AuthorizedSteamID != null)
                {
                    g_hAuthorize[player.Slot]?.Kill();
                    OnPlayerConnectFull(player);
                    return;
                }
            }, TimerFlags.REPEAT);
            return;
        }

        if (!g_bSteamAPIActivated)
            return;

        nint handle = player.Handle;
        ulong authorizedSteamID = player.AuthorizedSteamID.SteamId64;

        _ = CheckUserViolations(handle, authorizedSteamID);
    }

    private async Task CheckUserViolations(nint handle, ulong authorizedSteamID)
    {
        SteamService steamService = new SteamService(this);
        await steamService.FetchSteamUserInfoAsync(handle, authorizedSteamID);

        SteamUserInfo? userInfo = steamService.UserInfo;

        Server.NextWorldUpdate(() =>
        {
            CCSPlayerController? player = Utilities.GetPlayerFromSteamId(authorizedSteamID);

            if (player?.IsValid == true && userInfo != null)
            {
                Logger.LogInformation($"{player.PlayerName} info:");
                Logger.LogInformation($"CS2Playtime: {userInfo.CS2Playtime}");
                Logger.LogInformation($"CS2Level: {userInfo.CS2Level}");
                Logger.LogInformation($"SteamLevel: {userInfo.SteamLevel}");
                if ((DateTime.Now - userInfo.SteamAccountAge).TotalSeconds > 30)
                    Logger.LogInformation($"Steam Account Creation Date: {userInfo.SteamAccountAge:dd-MM-yyyy}");
                else
                    Logger.LogInformation($"Steam Account Creation Date: N/A");
                Logger.LogInformation($"HasPrime: {userInfo.HasPrime}");
                Logger.LogInformation($"HasPrivateProfile: {userInfo.IsPrivate}");
                Logger.LogInformation($"IsTradeBanned: {userInfo.IsTradeBanned}");
                Logger.LogInformation($"IsGameBanned: {userInfo.IsGameBanned}");
                Logger.LogInformation($"IsInSteamGroup: {userInfo.IsInSteamGroup}");

                if (IsRestrictionViolated(player, userInfo))
                {
                    Server.ExecuteCommand($"kickid {player.UserId} \"You have been kicked for not meeting the minimum requirements.\"");
                }
            }
        });
    }

    private bool IsRestrictionViolated(CCSPlayerController player, SteamUserInfo userInfo)
    {
        if (AdminManager.PlayerHasPermissions(player, "@css/bypasspremiumcheck"))
            return false;

        bool isPrime = userInfo.HasPrime;
        var configChecks = new[]
        {
            (isPrime, Config.MinimumHourPrime, userInfo.CS2Playtime),
            (isPrime, Config.MinimumLevelPrime, userInfo.SteamLevel),
            (isPrime, Config.MinimumCS2LevelPrime, userInfo.CS2Level),
            (!isPrime, Config.MinimumHourNonPrime, userInfo.CS2Playtime),
            (!isPrime, Config.MinimumLevelNonPrime, userInfo.SteamLevel),
            (!isPrime, Config.MinimumCS2LevelNonPrime, userInfo.CS2Level),
            (!isPrime, Config.MinimumSteamAccountAgeInDays, (DateTime.Now - userInfo.SteamAccountAge).TotalDays),
            (Config.BlockPrivateProfile, 1, userInfo.IsPrivate ? 0 : 1),
            (Config.BlockTradeBanned, 1, userInfo.IsTradeBanned ? 0 : 1),
            (Config.BlockGameBanned, 1, userInfo.IsGameBanned ? 0 : 1),
            (!string.IsNullOrEmpty(Config.SteamGroupID), 1, userInfo.IsInSteamGroup ? 0 : 1),
            (Config.BlockVACBanned, 1, userInfo.IsVACBanned ? 0 : 1),
        };

        return configChecks.Any(check => check.Item1 && check.Item2 != -1 && check.Item3 < check.Item2);
    }
}
