using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Events;

namespace CS2DropKnife;

public class CS2DropKnife : BasePlugin
{
    public override string ModuleName => "CS2 Drop Knife";

    public override string ModuleVersion => "4.1.0";

    private List<int> player_slot_ids = new List<int>();

    private List<int> ct_players = new List<int>();
    private List<int> t_players = new List<int>();

    private DropRules ?_settings;

    public override void Load(bool hotReload)
    {
        base.Load(hotReload);

        Console.WriteLine("[CS2DropKnife] Registering listeners.");
        RegisterListener<Listeners.OnMapStart>(OnMapStartHandler);

        _settings = new DropRules(ModuleDirectory);
        _settings.LoadSettings();
        
        Server.ExecuteCommand("mp_drop_knife_enable 1");
    }

    
    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        player_slot_ids.Clear();

        foreach(var player in Utilities.GetPlayers())
        {
            if (player == null || !player.IsValid || player.IsHLTV || player.IsBot)
            {
                continue;
            }

            player_slot_ids.Add(player.Slot);

            if (player.Team == CsTeam.CounterTerrorist)
            {
                ct_players.Add(player.Slot);
            }
            if (player.Team == CsTeam.Terrorist)
            {
                t_players.Add(player.Slot);
            }
            
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (player == null || !player.IsValid || player.IsHLTV || player.IsBot)
        {
            return HookResult.Continue;
        }

        player_slot_ids.Add(player.Slot);

        // Assume that players connenct before match starts. Allow drop knives to all players in the server.
        ct_players.Add(player.Slot);
        t_players.Add(player.Slot);

        return HookResult.Continue;
    }

    public void OnMapStartHandler(string map)
    {
        Server.ExecuteCommand("mp_drop_knife_enable 1");
    }

    [ConsoleCommand("css_drop", "Drop knives.")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnDropCommand(CCSPlayerController player, CommandInfo commandInfo)
    {
        DropKnife(player);
    }

    [ConsoleCommand("css_takeknife", "Drop knives.")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnTakeKnifeCommand(CCSPlayerController player, CommandInfo commandInfo)
    {
        DropKnife(player);
    }


    public void DropKnife(CCSPlayerController player)
    {
        // Player might not be alive.
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV || !player.PawnIsAlive || player.Pawn?.Value == null)
        {
            return;
        }

        // Check if the player is allowed to drop knives in this round.
        if (!player_slot_ids.Contains(player.Slot))
        {
            return;
        }

        // Optional: Only allow dropping knife at freeze time
        if (_settings == null || _settings.FreezeTimeOnly)
        {
            var gameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")?.FirstOrDefault()?.GameRules;
            if (gameRules != null && !gameRules.FreezePeriod)
            {
                return;
            }
        }

        // Find all teammates
        List<int> teammates = new List<int>();
        int self_slot = player.Slot;
        if (player.Team == CsTeam.CounterTerrorist)
        {
            foreach (int teammate in ct_players)
            {
                if (teammate != self_slot)
                {
                    teammates.Add(teammate);
                }
            }
        }
        if (player.Team == CsTeam.Terrorist)
        {
            foreach (int teammate in t_players)
            {
                if (teammate != self_slot)
                {
                    teammates.Add(teammate);
                }
            }
        }

        // First find the held knife
        string knife_designer_name = "";
        int held_knife_index = -1;

        var weapons = player.PlayerPawn.Value?.WeaponServices?.MyWeapons!;
        if (weapons == null)
        {
            return;
        }
        foreach (var weapon in weapons)
        {
            if (weapon != null && weapon.IsValid && weapon.Value != null)
            {
                if (weapon.Value.DesignerName.Contains("knife") || weapon.Value.DesignerName.Contains("bayonet"))
                {
                    knife_designer_name = weapon.Value.DesignerName;
                    held_knife_index = (int)weapon.Value.Index;
                    break;
                }
            }
        }

        if (held_knife_index != -1)
        {
            // Drop knives
            List<nint> knife_pointers = new List<nint>();
            for (int i = 0; i < teammates.Count; i++)
            {
                nint pointer = player.GiveNamedItem(knife_designer_name);
                knife_pointers.Add(pointer);
            }

            // Optional: Then find dropped knives and teleport
            if (_settings == null || _settings.DirectSend)
            {
                if (knife_pointers.Count >= teammates.Count)
                {
                    for (int i = 0; i < teammates.Count; i++)
                    {
                        CBasePlayerWeapon? knife = new(knife_pointers[i]);
                        if (knife == null || !knife.IsValid)
                        {
                            continue;
                        }

                        var teammate = Utilities.GetPlayerFromSlot(teammates[i]);
                        if (teammate != null && teammate.IsValid && !teammate.IsBot && !teammate.IsHLTV
                            && teammate.PawnIsAlive && teammate.Pawn != null && teammate.Pawn.IsValid && teammate.Pawn.Value != null)
                        {
                            knife.Teleport(teammate.Pawn.Value.AbsOrigin);
                        }
                    }
                }
            }
        }

        // Optional: Remove the player's chance to drop knives in current round
        if (_settings == null || _settings!.OncePerRound)
        {
            player_slot_ids.Remove(player.Slot);
        }

        return;
    }


    // Optional: This might cause performance issues
    [GameEventHandler]
    public HookResult OnPlayerChat(EventPlayerChat @event, GameEventInfo info)
    {
        if (_settings != null && _settings.ChatFiltering)
        {
            int player_slot = @event.Userid;

            try
            {
                CCSPlayerController player = Utilities.GetPlayerFromSlot(player_slot)!;
                if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)
                {
                    return HookResult.Continue;
                }

                string chat_message = @event.Text;

                if (chat_message.StartsWith("!drop") 
                || chat_message.StartsWith("/drop")
                || chat_message.StartsWith(".drop")
                || chat_message.Equals("!d")
                || chat_message.Equals("/d")
                || chat_message.Equals(".d")
                || chat_message.StartsWith("!takeknife")
                || chat_message.StartsWith("/takeknife")
                || chat_message.StartsWith(".takeknife"))
                {
                    DropKnife(player);
                }
            }
            catch (System.Exception)
            {
                return HookResult.Continue;
            }
        }

        return HookResult.Continue;
    }
}
