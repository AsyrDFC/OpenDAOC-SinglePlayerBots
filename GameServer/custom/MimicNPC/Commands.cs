using DOL.AI;
using DOL.AI.Brain;
using DOL.GS.Commands;
using DOL.GS.PacketHandler;
using log4net;
using log4net.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using static DOL.Database.DbArtifactBonus;

namespace DOL.GS.Scripts
{
    #region Admin/GM/Debug/Cheats

    [CmdAttribute(
    "&m",
    ePrivLevel.Player,
    "/m - Create a mimic of a certain class at your position or ground target.")]
    public class SummonCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public void OnCommand(GameClient client, string[] args)
        {
            if (args.Length > 0)
            {
                byte level;

                if (args.Length > 2)
                {
                    level = byte.Parse(args[2]);

                    if (level < 1 || level > 50)
                        level = 1;
                }
                else
                    level = client.Player.Level;

                Point3D position = new Point3D(client.Player.X, client.Player.Y, client.Player.Z);

                if (client.Player.GroundTarget != null)
                {
                    Point2D playerPos = new Point2D(client.Player.X, client.Player.Y);

                    if (client.Player.GroundTarget.GetDistance(playerPos) < 5000)
                        position = new Point3D(client.Player.GroundTarget);
                }

                if (position != null)
                {
                    string capitalize = char.ToUpper(args[1][0]) + args[1].Substring(1);
                    eMimicClasses mclass = (eMimicClasses)Enum.Parse(typeof(eMimicClasses), capitalize);

                    MimicNPC mimic = MimicManager.GetMimic(mclass, level);
                    MimicManager.AddMimicToWorld(mimic, position, client.Player.CurrentRegionID);
                }
            }
        }
    }

    [CmdAttribute(
       "&mgroup",
       ePrivLevel.Player,
       "/mgroup - To summon a group of mimics from a realm. Args: realm, amount, level")]
    public class SummonMimicGroupCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public void OnCommand(GameClient client, string[] args)
        {
            if (args.Length >= 2)
            {
                args[1] = args[1].ToLower();

                byte groupSize = 8;
                if (args.Length >= 3)
                {
                    groupSize = byte.Parse(args[2]);

                    if (groupSize < 1 || groupSize > 8)
                        groupSize = 8;
                }

                byte level;
                if (args.Length >= 4)
                {
                    level = byte.Parse(args[3]);

                    if (level < 1 || level > 50)
                        level = 1;
                }
                else
                    level = client.Player.Level;

                Point3D position = new Point3D(client.Player.X, client.Player.Y, client.Player.Z);

                if (client.Player.GroundTarget != null)
                {
                    Point2D playerPos = new Point2D(client.Player.X, client.Player.Y);

                    if (client.Player.GroundTarget.GetDistance(playerPos) < 5000)
                        position = new Point3D(client.Player.GroundTarget);
                }

                if (position != null)
                {
                    List<GameLiving> groupMembers = new List<GameLiving>();
                    MimicNPC mimic;

                    switch (args[1])
                    {
                        case "albion":
                            {
                                for (int i = 0; i < groupSize; i++)
                                {
                                    int randomX = Util.Random(-100, 100);
                                    int randomY = Util.Random(-100, 100);

                                    position.X += randomX;
                                    position.Y += randomY;

                                    mimic = MimicManager.GetMimic(MimicManager.GetRandomMimicClass(eRealm.Albion), level);
                                    MimicManager.AddMimicToWorld(mimic, position, client.Player.CurrentRegionID);

                                    if (mimic != null)
                                        groupMembers.Add(mimic);
                                }

                                break;
                            }

                        case "hibernia":
                            {
                                for (int i = 0; i < groupSize; i++)
                                {
                                    int randomX = Util.Random(-100, 100);
                                    int randomY = Util.Random(-100, 100);

                                    position.X += randomX;
                                    position.Y += randomY;

                                    mimic = MimicManager.GetMimic(MimicManager.GetRandomMimicClass(eRealm.Hibernia), level);
                                    MimicManager.AddMimicToWorld(mimic, position, client.Player.CurrentRegionID);

                                    if (mimic != null)
                                        groupMembers.Add(mimic);
                                }

                                break;
                            }

                        case "midgard":
                            {
                                for (int i = 0; i < groupSize; i++)
                                {
                                    int randomX = Util.Random(-100, 100);
                                    int randomY = Util.Random(-100, 100);

                                    position.X += randomX;
                                    position.Y += randomY;

                                    mimic = MimicManager.GetMimic(MimicManager.GetRandomMimicClass(eRealm.Midgard), level);
                                    MimicManager.AddMimicToWorld(mimic, position, client.Player.CurrentRegionID);

                                    if (mimic != null)
                                        groupMembers.Add(mimic);
                                }

                                break;
                            }

                        default: break;
                    }

                    if (groupMembers.Count > 0)
                    {
                        if (groupMembers[0].Group == null)
                        {
                            groupMembers[0].Group = new Group(groupMembers[0]);
                            groupMembers[0].Group.AddMember(groupMembers[0]);
                        }

                        foreach (GameLiving living in groupMembers)
                        {
                            if (living.Group == null)
                            {
                                groupMembers[0].Group.AddMember(living);

                                MimicBrain brain = ((MimicNPC)living).Brain as MimicBrain;
                                brain.PvPMode = true;
                                brain.Roam = true;
                                brain.FSM.SetCurrentState(eFSMStateType.WAKING_UP);
                            }
                        }
                    }
                }
            }
        }
    }

    [CmdAttribute(
       "&mpvp",
       ePrivLevel.Player,
       "/mpvp (true/false) - Set PvP mode on selected mimic.")]
    public class MimicPvPModeCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            if (client.Player != null && client.Player.TargetObject is not MimicNPC)
                return;

            MimicNPC mimic = client.Player.TargetObject as MimicNPC;

            if (args.Length > 1)
            {
                args[1] = args[1].ToLower();

                switch (args[1])
                {
                    case "true":
                        mimic.MimicBrain.PvPMode = true;
                        break;

                    case "false":
                        mimic.MimicBrain.PvPMode = false;
                        break;
                }
            }
        }
    }
    [CmdAttribute(
    "&mleader",
    ePrivLevel.Player,
    "/mleader - Toggles the player's leader status in a mimic group.")]
    public class MimicLeaderCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            if (client.Player != null && client.Player.TargetObject is MimicNPC)
                return;

            // Toggle the LeadsMimic property
            client.Player.LeadsMimic = !client.Player.LeadsMimic;
            client.Out.SendMessage($"Leading mimic = {client.Player.LeadsMimic}", eChatType.CT_System, eChatLoc.CL_SystemWindow);
        }
    }

    [CmdAttribute(
   "&mpc",
   ePrivLevel.Player,
   "/mpc (true/false) - Set prevent combat on selected mimic.")]
    public class MimicCombatPreventCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            if (client.Player != null && client.Player.TargetObject is not MimicNPC)
                return;

            MimicNPC mimic = client.Player.TargetObject as MimicNPC;

            if (args.Length > 1)
            {
                args[1] = args[1].ToLower();

                switch (args[1])
                {
                    case "true":
                        mimic.MimicBrain.PreventCombat = true;
                        break;

                    case "false":
                        mimic.MimicBrain.PreventCombat = false;
                        break;
                }
            }
        }
    }

    [CmdAttribute(
      "&mbattle",
      ePrivLevel.Player,
      "/mbattle [Region] (Start/Stop/Clear>)",
      "Regions: Thid. Cathal. Start - Start spawning. Stop - Stop spawning. Clear - Stop and remove mimics.")]
    public class MimicBattleCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            if (args.Length > 2)
            {
                args[1] = args[1].ToLower();
                args[2] = args[2].ToLower();

                switch (args[1])
                {
                    case "thid":
                        switch (args[2])
                        {
                            case "start": MimicBattlegrounds.ThidBattleground.Start(); break;
                            case "stop": MimicBattlegrounds.ThidBattleground.Stop(); break;
                            case "clear": MimicBattlegrounds.ThidBattleground.Clear(); break;
                        }
                        break;

                    case "cathal":
                        if (MimicBattlegrounds.CathalBattleground != null)
                        {
                            switch (args[2])
                            {
                                case "start": MimicBattlegrounds.CathalBattleground.Start(); break;
                                case "stop": MimicBattlegrounds.CathalBattleground.Stop(); break;
                                case "clear": MimicBattlegrounds.CathalBattleground.Clear(); break;
                            }
                        }
                        else
                        {
                            // Handle the null case, maybe log an error or notify the user
                        }
                        break;
                }
            }
        }
    }

    [CmdAttribute(
      "&msummon",
      ePrivLevel.Player,
      "/msummon - Summons all mimics in your group.")]
    public class MimimcSummonCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            if (client.Player.Group == null)
                return;

            foreach (GameLiving groupMember in client.Player.Group.GetMembersInTheGroup())
            {
                if (groupMember != client.Player)
                    groupMember.MoveTo(client.Player.CurrentRegionID, client.Player.X, client.Player.Y, client.Player.Z, client.Player.Heading);
            }
        }
    }

    #endregion Admin/GM/Debug/Cheats

    #region MimicGroup

    [CmdAttribute(
       "&mlfg",
       ePrivLevel.Player,
       "/mlfg - Get a list of Mimics that are looking for a group.")]
    public class LocCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            GamePlayer player = client.Player;

            if (player == null)
                return;

            var entries = MimicLFGManager.GetLFG(player.Realm, player.Level);
            string message;

            if (args.Length < 2)
            {
                message = BuildMessage(entries);
            }
            else
            {
                int index = int.Parse(args[1]) - 1;

                if (index < 0 || index > entries.Count - 1)
                    message = BuildMessage(entries, true);
                else
                {
                    MimicLFGManager.MimicLFGEntry entry = entries[index];

                    int baseChance = 90;

                    if (MimicConfig.LEVEL_BIAS)
                    {
                        int biasAmount = 5;
                        int levelDifference = player.Level - entry.Level;

                        if (Math.Abs(levelDifference) > 1)
                            baseChance += levelDifference * biasAmount;

                        baseChance = Math.Clamp(baseChance, 5, 95);
                    }

                    if (Util.Chance(baseChance) && !entry.RefusedGroup)
                    {
                        if (player.Group == null)
                        {
                            player.Group = new Group(player);
                            player.Group.AddMember(player);
                        }

                        if (player.Group.GetMembersInTheGroup().Count < ServerProperties.Properties.GROUP_MAX_MEMBER)
                        {
                            MimicNPC mimic = MimicManager.GetMimic(entry.MimicClass, entry.Level, entry.Name, entry.Gender);
                            MimicManager.AddMimicToWorld(mimic, new Point3D(player.X, player.Y, player.Z), player.CurrentRegionID);

                            player.Group.AddMember(mimic);

                            MimicLFGManager.Remove(player.Realm, entry);

                            // Send a refreshed list with new indexes to avoid using wrong indexes while leaving the dialogue open
                            entries = MimicLFGManager.GetLFG(player.Realm, player.Level);

                            message = BuildMessage(entries);
                        }
                        else
                            message = BuildMessage(entries, true);
                    }
                    else
                    {
                        if (entry.RefusedGroup)
                            player.Out.SendMessage(entry.Name + " sends, \"Sorry, I've already said no.\"", eChatType.CT_Send, eChatLoc.CL_SystemWindow);
                        else
                            player.Out.SendMessage(entry.Name + " sends, \"No thanks, looking for a different group!\"", eChatType.CT_Send, eChatLoc.CL_SystemWindow);

                        entry.RefusedGroup = true;
                        return;
                    }
                }
            }

            player.Out.SendMessage(message, eChatType.CT_System, eChatLoc.CL_PopupWindow);
        }

        private string BuildMessage(List<MimicLFGManager.MimicLFGEntry> entries, bool invalid = false)
        {
            string message = "--------------------------------\n";

            if (invalid)
                message += "Invalid number selection or group is full\n";
            else if (entries.Any())
            {
                int index = 1;
                foreach (var entry in entries)
                    message += index++.ToString() + ". " + entry.Name + " " + Enum.GetName(typeof(eMimicClasses), entry.MimicClass) + " " + entry.Level + "\n";
            }
            else
                message += "No Mimics available.\n";

            return message;
        }
    }

    [CmdAttribute(
        "&mrole",
        ePrivLevel.Player,
        "/mrole (leader/tank/assist/cc/puller) - Set the role of a group member.")]
    public class MainRoleCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            GamePlayer player = client.Player;
            GameLiving target = player.TargetObject as GameLiving;

            if (player.Group == null || target == null)
                return;

            if (args.Length > 1)
            {
                args[1] = args[1].ToLower();

                bool success = false;

                switch (args[1])
                {
                    case "leader": success = player.Group.MimicGroup.SetLeader(target); break;
                    case "tank": success = player.Group.MimicGroup.SetMainTank(target); break;
                    case "assist": success = player.Group.MimicGroup.SetMainAssist(target); break;
                    case "cc": success = player.Group.MimicGroup.SetMainCC(target); break;
                    case "puller": success = player.Group.MimicGroup.SetMainPuller(target); break;
                }

                if (!success)
                    player.Out.SendMessage("Failed to set " + args[1], eChatType.CT_Say, eChatLoc.CL_SystemWindow);
            }
        }
    }

    [CmdAttribute(
        "&mcamp",
        ePrivLevel.Player,
        "/mcamp (set/remove/aggrorange/filter)- Set where the group camp point is, remove the camp point, the range the group will aggro, and the con level the puller will pull.")]
    public class CampCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            GamePlayer player = client.Player;
            Point3D target = client.Player.GroundTarget;

            if (player.Group == null)
                return;

            if (args.Length > 1)
            {
                args[1] = args[1].ToLower();

                switch (args[1])
                {
                    case "set":
                        {
                            if (target == null || player.GetDistance(player.GroundTarget) > 2000)
                            {
                                player.Out.SendMessage("Ground target is too far away.", eChatType.CT_Say, eChatLoc.CL_SystemWindow);
                                return;
                            }

                            player.Group.MimicGroup.SetCampPoint(target);

                            player.Out.SendMessage("Set camp spot.", eChatType.CT_Say, eChatLoc.CL_SystemWindow);

                            foreach (GameLiving groupMember in player.Group.GetMembersInTheGroup())
                                if (groupMember is MimicNPC mimic)
                                    mimic.Brain.FSM.SetCurrentState(eFSMStateType.CAMP);
                        }
                        break;

                    case "remove":
                        {
                            if (player.Group.MimicGroup.CampPoint != null)
                            {
                                player.Group.MimicGroup.SetCampPoint(null);
                                player.Out.SendMessage("Removed camp spot.", eChatType.CT_Say, eChatLoc.CL_SystemWindow);
                            }
                            else
                                player.Out.SendMessage("No camp spot to remove.", eChatType.CT_Say, eChatLoc.CL_SystemWindow);

                            foreach (GameLiving groupMember in player.Group.GetMembersInTheGroup())
                            {
                                if (groupMember is MimicNPC mimic)
                                {
                                    mimic.Brain.FSM.SetCurrentState(eFSMStateType.FOLLOW_THE_LEADER);
                                    mimic.MimicBrain.AggroRange = 3600;
                                }
                            }
                        }
                        break;

                    case "aggrorange":
                        {
                            if (args.Length > 2)
                            {
                                int range = int.Parse(args[2]);

                                if (range < 0 || range > int.MaxValue)
                                    range = 550;

                                foreach (GameLiving groupMember in player.Group.GetMembersInTheGroup())
                                {
                                    if (groupMember is MimicNPC mimic)
                                    {
                                        FSMState mimicState = mimic.Brain.FSM.GetState(eFSMStateType.CAMP);

                                        ((MimicState_Camp)mimicState).AggroRange = range;
                                    }
                                }

                                player.Out.SendMessage("Camp aggro range is " + range, eChatType.CT_System, eChatLoc.CL_SystemWindow);
                            }
                        }
                        break;

                    case "filter":
                        {
                            if (args.Length > 2)
                            {
                                args[2] = args[2].ToLower();

                                switch (args[2])
                                {
                                    case "purple": player.Group.MimicGroup.ConLevelFilter = 3; break;
                                    case "red": player.Group.MimicGroup.ConLevelFilter = 2; break;
                                    case "orange": player.Group.MimicGroup.ConLevelFilter = 1; break;
                                    case "yellow": player.Group.MimicGroup.ConLevelFilter = 0; break;
                                    case "blue": player.Group.MimicGroup.ConLevelFilter = -1; break;
                                    case "green": player.Group.MimicGroup.ConLevelFilter = -2; break;
                                }
                            }
                        }
                        break;
                }
            }
        }
    }

    [CmdAttribute(
        "&mpullfrom",
        ePrivLevel.Player,
        "/mpullfrom (set/remove) - Set where the group puller should try to pull from.")]
    public class PullFromCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            GamePlayer player = client.Player;
            Point3D target = client.Player.GroundTarget;

            if (player.Group == null)
                return;

            if (args.Length > 1)
            {
                args[1] = args[1].ToLower();

                switch (args[1])
                {
                    case "set":
                        {
                            if (target == null || !player.GroundTargetInView)
                                return;

                            player.Group.MimicGroup.SetPullPoint(target);

                            player.Out.SendMessage("Set position to pull from.", eChatType.CT_Say, eChatLoc.CL_SystemWindow);
                        }
                        break;

                    case "remove":
                        {
                            player.Group.MimicGroup.SetPullPoint(null);

                            player.Out.SendMessage("Removed position to pull from.", eChatType.CT_Say, eChatLoc.CL_SystemWindow);
                        }
                        break;
                }
            }
        }
    }

    #endregion MimicGroup

    [CmdAttribute(
      "&mbstats",
      ePrivLevel.Player,
      "/mbstats [Battleground] - Get stats on a battleground.",
      "[Battleground] - Thid - Cathal")]
    public class MimicBattleStatsCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            if (args.Length > 1)
            {
                args[1] = args[1].ToLower();

                switch (args[1])
                {
                    case "thid": MimicBattlegrounds.ThidBattleground.BattlegroundStats(client.Player); break;
                    case "cathal": MimicBattlegrounds.CathalBattleground.BattlegroundStats(client.Player); break;
                }
            }
        }
    }
}