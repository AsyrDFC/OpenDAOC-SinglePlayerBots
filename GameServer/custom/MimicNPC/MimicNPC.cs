using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using DOL.AI;
using DOL.AI.Brain;
using DOL.Database;
using DOL.Events;
using DOL.GS.API;
using DOL.GS.Behaviour.Actions;
using DOL.GS.Effects;
using DOL.GS.Housing;
using DOL.GS.Keeps;
using DOL.GS.PacketHandler;
using DOL.GS.PacketHandler.Client.v168;
using DOL.GS.PlayerClass;
using DOL.GS.PlayerTitles;
using DOL.GS.PropertyCalc;
using DOL.GS.Quests;
using DOL.GS.Realm;
using DOL.GS.RealmAbilities;
using DOL.GS.ServerProperties;
using DOL.GS.ServerRules;
using DOL.GS.SkillHandler;
using DOL.GS.Spells;
using DOL.GS.Styles;
using DOL.GS.Utils;
using DOL.Language;
using JNogueira.Discord.Webhook.Client;
using log4net;
using static DOL.GS.AttackData;

namespace DOL.GS.Scripts
{
    public class MimicNPC : GameNPC
    {
        public GamePlayer Dev { private get; set; }
        public void DevOut(string message)
        {
            Dev.Out.SendMessage(message, eChatType.CT_Say, eChatLoc.CL_ChatWindow);
        }

        public bool CanUseSideStyles { get { return StylesSide != null && StylesSide.Count > 0; } }
        public bool CanUseBackStyles { get { return StylesBack != null && StylesBack.Count > 0; } }
        public bool CanUseFrontStyle { get { return StylesFront != null && StylesFront.Count > 0; } }
        public bool CanUseAnytimeStyles { get { return StylesAnytime != null && StylesAnytime.Count > 0; } }
        public bool CanUsePositionalStyles { get { return CanUseSideStyles || CanUseBackStyles; } }
        public bool CanCastInstantCrowdControlSpells { get { return InstantCrowdControlSpells != null && InstantCrowdControlSpells.Count > 0; } }
        public bool CanCastCrowdControlSpells { get { return CrowdControlSpells != null && CrowdControlSpells.Count > 0; } }
        public bool CanCastBolts { get { return BoltSpells != null && BoltSpells.Count > 0; } }

        public Style TauntStyle
        {
            get { return m_tauntStyle; }
            set { m_tauntStyle = value; }
        }

        private Style m_tauntStyle;

        public List<Style> TauntStyles { get; set; } = null;

        public override int InteractDistance => WorldMgr.VISIBILITY_DISTANCE;

        /// <summary>
		/// Instant Crowd Controll spell list and accessor
		/// </summary>
		public List<Spell> InstantCrowdControlSpells { get; set; } = null;

        /// <summary>
		/// Crowd Controll spell list and accessor
		/// </summary>
        public List<Spell> CrowdControlSpells { get; set; } = null;

        /// <summary>
		/// Bolt spell list and accessor
		/// </summary>
        public List<Spell> BoltSpells { get; set; } = null;

        public MimicSpec MimicSpec = new MimicSpec();
        public int Kills;

        private MimicBrain m_mimicBrain;
        public MimicBrain MimicBrain
        {
            get { return m_mimicBrain; }
            set { m_mimicBrain = value; }
        }

        private int m_leftOverSpecPoints = 0;

        public MimicNPC(ICharacterClass cClass, byte level, eGender gender = eGender.Neutral)
        {
            Inventory = new MimicNPCInventory();
            MaxSpeedBase = GamePlayer.PLAYER_BASE_SPEED;

            SetCharacterClass(cClass.ID);
            SetRaceAndName();
            SetLevel(level);

            MimicBrain = new MimicBrain();
            MimicBrain.MimicBody = this;
            SetOwnBrain(MimicBrain);

            Endurance = MaxEndurance;
            Mana = MaxMana;
            RespawnInterval = -1;

            m_combatTimer = new ECSGameTimer(this, new ECSGameTimer.ECSTimerCallback(_ =>
            {
                return 0;
            }));

            Dev = ClientService.GetPlayers()[0];
            //Brent Added -- here we are getting the path to set the path id 
            // PathID = LetsChooseAPath();
        }

        public void GetTauntStyles()
        {
            if (CanUseAnytimeStyles && m_styles != null && m_styles.Count > 0)
            {
                foreach (Style style in m_styles)
                {
                    if (style.Procs?.FirstOrDefault(x => x.Item1.SpellType == eSpellType.StyleTaunt) != null)
                    {
                        if (TauntStyles == null)
                            TauntStyles = new List<Style>(1);

                        TauntStyles.Add(style);
                    }
                }
            }
        }

        public override bool Interact(GamePlayer player)
        {
            if (!base.Interact(player))
                return false;

            player.Out.SendMessage("[State] [Prevent Combat] [Brain]\n " +
                "[Group] - [Leader] - [MainPuller] - [MainCC] - [MainTank] - [MainAssist]\n " +
                "[Spells] - [Inst Harmful] - [Harmful Spells] - [Inst Misc] - [Misc Spells] - [Inst Heal] - [Heal Spells]\n " +
                "[Effects] - [Styles] - [Abilities]\n " +
                "[Spec] - [Stats]\n " +
                "[Hood] [Weapon] [Helm] [Torso] [Legs] [Arms] [Hands] [Boots] [Jewelry]\n " +
                "[Delete]", eChatType.CT_Say, eChatLoc.CL_PopupWindow);
            return true;
        }

        public override bool WhisperReceive(GameLiving source, string str)
        {
            if (!base.WhisperReceive(source, str))
                return false;

            GamePlayer player = source as GamePlayer;

            if (player == null)
                return false;

            switch (str)
            {
                case "Brain":
                    MimicBrain newBrain = new MimicBrain();
                    newBrain.MimicBody = this;
                    SetOwnBrain(newBrain);
                    break;

                case "State":
                    {
                        string message = Brain.FSM.GetCurrentState().ToString();
                        SendReply(player, message);
                    }
                    break;

                case "Prevent Combat": MimicBrain.PreventCombat = !MimicBrain.PreventCombat; break;

                case "Group":
                    {
                        // If there's an existing group with less than 8 members, add the player to it
                        if (Group != null && Group.GetMembersInTheGroup().Count < 8)
                        {
                            Group.AddMember(player);
                        }
                        if (player.Group == null)
                        {
                            player.Group = new Group(player);
                            player.Group.AddMember(player);
                        }
                        else
                        {
                            if (player.Group.GetMembersInTheGroup().Contains(this) || player.Group.MemberCount > 8)
                                break;
                        }

                        player.Group.AddMember(this);
                        MimicBrain.FSM.SetCurrentState(eFSMStateType.FOLLOW_THE_LEADER);
                    }
                    break;

                case "Leader": Group?.MimicGroup.SetLeader(this); break;
                case "MainPuller": Group?.MimicGroup.SetMainPuller(this); break;
                case "MainCC": Group?.MimicGroup.SetMainCC(this); break;
                case "MainTank": Group?.MimicGroup.SetMainTank(this); break;
                case "MainAssist": Group?.MimicGroup.SetMainAssist(this); break;

                case "Spells":
                    {
                        string message = string.Empty;

                        foreach (Spell spell in Spells)
                        {
                            message += spell.Name + " " + spell.Level + " " + spell.SpellType + "\n";
                        }
                        SendReply(player, message);

                        break;
                    }

                case "Inst Harmful":
                    {
                        string message = string.Empty;

                        if (CanCastInstantHarmfulSpells)
                        {
                            foreach (Spell spell in InstantHarmfulSpells)
                            {
                                message += spell.Name + " " + spell.Level + " " + spell.SpellType + "\n";
                            }
                            SendReply(player, message);
                        }

                        break;
                    }

                case "Harmful Spells":
                    {
                        string message = string.Empty;

                        if (CanCastHarmfulSpells)
                        {
                            foreach (Spell spell in HarmfulSpells)
                            {
                                message += spell.Name + " " + spell.Level + " " + spell.SpellType + "\n";
                            }
                            SendReply(player, message);
                        }

                        break;
                    }

                case "Inst Misc":
                    {
                        string message = string.Empty;

                        if (CanCastInstantMiscSpells)
                        {
                            foreach (Spell spell in InstantMiscSpells)
                            {
                                message += spell.Name + " " + spell.Level + " " + spell.SpellType + "\n";
                            }
                            SendReply(player, message);
                        }

                        break;
                    }

                case "Misc Spells":
                    {
                        string message = string.Empty;

                        if (CanCastMiscSpells)
                        {
                            foreach (Spell spell in MiscSpells)
                            {
                                message += spell.Name + " " + spell.Level + " " + spell.SpellType + "\n";
                            }
                            SendReply(player, message);
                        }

                        break;
                    }

                case "Inst Heal":
                    {
                        string message = string.Empty;

                        if (CanCastInstantHealSpells)
                        {
                            foreach (Spell spell in InstantHealSpells)
                            {
                                message += spell.Name + " " + spell.Level + " " + spell.SpellType + "\n";
                            }
                            SendReply(player, message);
                        }

                        break;
                    }

                case "Heal Spells":
                    {
                        string message = string.Empty;

                        if (CanCastHealSpells)
                        {
                            foreach (Spell spell in HealSpells)
                            {
                                message += spell.Name + " " + spell.Level + " " + spell.SpellType + "\n";
                            }
                            SendReply(player, message);
                        }

                        break;
                    }

                case "Styles":
                    {
                        string message = string.Empty;

                        foreach (Style style in Styles)
                        {
                            message += style.Name + " " + style.Level + "\n";
                        }
                        SendReply(player, message);

                        break;
                    }

                case "Abilities":
                    {
                        string message = string.Empty;

                        foreach (Ability ability in GetAllAbilities())
                        {
                            message += ability.Name + " " + ability.Level + "\n";
                        }
                        SendReply(player, message);

                        break;
                    }

                case "Stats":
                    SendReply(player, string.Format("Level: {0}\n Str: {1}\n Con: {2}\n Dex: {3}\n Qui: {4}\n Int: {5}\n Pie: {6}\n Emp: {7}\n Cha: {8}\n" +
                        " HP: {9}\n AF: {10}\n, End: {11}\n Power: {12}\n",
                        Level,
                        Strength,
                        Constitution,
                        Dexterity,
                        Quickness,
                        Intelligence,
                        Piety,
                        Empathy,
                        Charisma,
                        Health,
                        EffectiveOverallAF,
                        Endurance,
                        Mana));
                    break;

                case "Spec":
                    {
                        string message = string.Empty;

                        var specs = GetSpecList();

                        foreach (Specialization spec in specs)
                        {
                            message += spec.Name + ": " + spec.Level + " \n";
                        }

                        SendReply(player, message + DisplayedWeaponSkill);

                        break;
                    }

                case "Hood":
                    IsCloakHoodUp = !IsCloakHoodUp; break;

                case "Helm":
                    {
                        DbInventoryItem item = Inventory.GetItem(eInventorySlot.HeadArmor);

                        if (item != null)
                        {
                            Inventory.RemoveItem(item);

                            if (!(player.Inventory.AddItem(eInventorySlot.FirstEmptyBackpack, item)))
                            {
                                player.SendPrivateMessage(player, "No room in your backpack you twat.");
                                Inventory.AddItem((eInventorySlot)item.SlotPosition, item);
                            }

                            if (Inventory.AllItems.Contains(item))
                                log.Debug("Still has that shit.");
                        }

                        BroadcastLivingEquipmentUpdate();
                        break;
                    }

                case "Torso":
                    {
                        DbInventoryItem item = Inventory.GetItem(eInventorySlot.TorsoArmor);

                        if (item != null)
                        {
                            Inventory.RemoveItem(item);

                            if (!(player.Inventory.AddItem(eInventorySlot.FirstEmptyBackpack, item)))
                            {
                                player.SendPrivateMessage(player, "No room in your backpack you twat.");
                                Inventory.AddItem((eInventorySlot)item.SlotPosition, item);
                            }
                        }

                        BroadcastLivingEquipmentUpdate();
                        break;
                    }

                case "Legs":
                    {
                        DbInventoryItem item = Inventory.GetItem(eInventorySlot.LegsArmor);

                        if (item != null)
                        {
                            Inventory.RemoveItem(item);

                            if (!(player.Inventory.AddItem(eInventorySlot.FirstEmptyBackpack, item)))
                            {
                                player.SendPrivateMessage(player, "No room in your backpack you twat.");
                                Inventory.AddItem((eInventorySlot)item.SlotPosition, item);
                            }
                        }

                        BroadcastLivingEquipmentUpdate();
                        break;
                    }

                case "Arms":
                    {
                        DbInventoryItem item = Inventory.GetItem(eInventorySlot.ArmsArmor);

                        if (item != null)
                        {
                            Inventory.RemoveItem(item);

                            if (!(player.Inventory.AddItem(eInventorySlot.FirstEmptyBackpack, item)))
                            {
                                player.SendPrivateMessage(player, "No room in your backpack you twat.");
                                Inventory.AddItem((eInventorySlot)item.SlotPosition, item);
                            }
                        }

                        BroadcastLivingEquipmentUpdate();
                        break;
                    }

                case "Hands":
                    {
                        DbInventoryItem item = Inventory.GetItem(eInventorySlot.HandsArmor);

                        if (item != null)
                        {
                            Inventory.RemoveItem(item);

                            if (!(player.Inventory.AddItem(eInventorySlot.FirstEmptyBackpack, item)))
                            {
                                player.SendPrivateMessage(player, "No room in your backpack you twat.");
                                Inventory.AddItem((eInventorySlot)item.SlotPosition, item);
                            }
                        }

                        BroadcastLivingEquipmentUpdate();
                        break;
                    }

                case "Boots":
                    {
                        DbInventoryItem item = Inventory.GetItem(eInventorySlot.FeetArmor);

                        if (item != null)
                        {
                            Inventory.RemoveItem(item);

                            if (!(player.Inventory.AddItem(eInventorySlot.FirstEmptyBackpack, item)))
                            {
                                player.SendPrivateMessage(player, "No room in your backpack you twat.");
                                Inventory.AddItem((eInventorySlot)item.SlotPosition, item);
                            }
                        }

                        BroadcastLivingEquipmentUpdate();
                        break;
                    }

                case "Weapon":
                    {
                        DbInventoryItem item = Inventory.GetItem(Inventory.FindFirstFullSlot(eInventorySlot.DistanceWeapon, eInventorySlot.RightHandWeapon));

                        if (item != null)
                        {
                            Inventory.RemoveItem(item);

                            if (!(player.Inventory.AddItem(eInventorySlot.FirstEmptyBackpack, item)))
                            {
                                player.SendPrivateMessage(player, "No room in your backpack you twat.");
                                Inventory.AddItem((eInventorySlot)item.SlotPosition, item);
                            }
                        }

                        BroadcastLivingEquipmentUpdate();
                        break;
                    }

                case "Jewelry":
                    {
                        for (int i = Slot.JEWELRY; i <= Slot.RIGHTRING; i++)
                        {
                            if (i is Slot.TORSO or Slot.LEGS or Slot.ARMS or Slot.FOREARMS or Slot.SHIELD)
                                continue;

                            DbInventoryItem item = Inventory.GetItem((eInventorySlot)i);
                            player.Inventory.AddItem(eInventorySlot.FirstEmptyBackpack, item);
                        }
                    }
                    break;

                case "Delete":
                    {
                        if (ControlledBrain != null)
                        {
                            if (ControlledBrain.Body != null)
                                ControlledBrain.Body.Delete();
                        }

                        Delete();

                        break;
                    }

                default: break;
            }

            return true;
        }

        private void SendReply(GamePlayer player, string msg)
        {
            player.Out.SendMessage(msg, eChatType.CT_System, eChatLoc.CL_PopupWindow);
        }

        public override bool SayReceive(GameLiving source, string str)
        {
            if (source == null || str == null)
                return false;

            if (Group != null && Group.GetMembersInTheGroup().Contains(source))
            {
                str = str.ToLower();

                switch (str)
                {
                    case "stay": Brain.FSM.SetCurrentState(eFSMStateType.IDLE); break;
                    case "follow": Brain.FSM.SetCurrentState(eFSMStateType.FOLLOW_THE_LEADER); break;
                    case "camp": Brain.FSM.SetCurrentState(eFSMStateType.CAMP); break;
                    case "reset": Brain.FSM.SetCurrentState(eFSMStateType.WAKING_UP); break;
                }
            }

            return base.SayReceive(source, str);
        }

        // TODO: Will need a different method for when leveling up after being created or adjust this.
        public void SpendSpecPoints(byte fromLevel = 2, bool mimicCreation = true)
        {
            MimicSpec.SpecLines = MimicSpec.SpecLines.OrderByDescending(ratio => ratio.levelRatio).ToList();

            int leftOverSpecPoints = m_leftOverSpecPoints;
            bool spentPoints = true;
            bool spendLeftOverPoints = false;

            // For each level, get the points available. In the while loop, spend points according to the ratio in SpecLines until spentPoints is false.
            // Then set spendLeftOverPoints true and spend any left over points until spentPoints is false again. Exit the while loop, increase level, and repeat.
            for (byte i = fromLevel; i <= Level; i++)
            {
                spentPoints = true;
                spendLeftOverPoints = false;

                int totalSpecPointsThisLevel = GetSpecPointsForLevel(i, mimicCreation) + leftOverSpecPoints;

                while (spentPoints)
                {
                    spentPoints = false;

                    foreach (SpecLine specLine in MimicSpec.SpecLines)
                    {
                        // Indicates a dump stat for lvl 50
                        if (specLine.levelRatio <= 0 && Level < 50)
                            continue;

                        Specialization spec = GetSpecializationByName(specLine.SpecName);

                        if (spec != null)
                        {
                            if (spec.Level < specLine.SpecCap && spec.Level < i)
                            {
                                int specRatio = (int)(i * specLine.levelRatio);

                                if (!spendLeftOverPoints && spec.Level >= specRatio)
                                    continue;

                                int totalCost = spec.Level + 1;

                                if (totalSpecPointsThisLevel >= totalCost)
                                {
                                    totalSpecPointsThisLevel -= totalCost;
                                    spec.Level++;
                                    spentPoints = true;
                                }
                            }
                        }
                    }

                    if (!spentPoints && !spendLeftOverPoints)
                    {
                        spendLeftOverPoints = true;
                        spentPoints = true;
                    }
                }

                m_leftOverSpecPoints = leftOverSpecPoints = totalSpecPointsThisLevel;
            }
        }

        private int GetSpecPointsForLevel(int level, bool mimicCreation)
        {
            //    // calc spec points player have (autotrain is not anymore processed here - 1.87 livelike)
            //    int usedpoints = 0;
            //        foreach (Specialization spec in GetSpecList().Where(e => e.Trainable))
            //        {
            //            usedpoints += (spec.Level* (spec.Level + 1) - 2) / 2;
            //            usedpoints -= GetAutoTrainPoints(spec, 0);
            //}

            int specpoints = 0;

            if (IsLevelSecondStage)
                return 0;

            if (mimicCreation)
                if (level > 40)
                    specpoints += CharacterClass.SpecPointsMultiplier * (level - 1) / 20;

            if (level > 5)
                specpoints += CharacterClass.SpecPointsMultiplier * level / 10;
            else if (level >= 2)
                specpoints = level;

            return specpoints;
        }

        public void SetSpells()
        {
            IList<Specialization> specs = GetSpecList();

            List<Skill> skills;
            List<Spell> spells = new List<Spell>();

            foreach (Specialization spec in specs)
            {
                var dict = GetLinesSpellsForLiving(this, spec.Level, spec);

                if (dict != null && dict.Count > 0)
                {
                    foreach (KeyValuePair<SpellLine, List<Skill>> kvp in dict)
                    {
                        dict.TryGetValue(kvp.Key, out skills);

                        foreach (Skill skill in skills)
                        {
                            if (skill is Spell)
                            {
                                Spell spell = skill as Spell;

                                spells.Add(spell);
                            }
                        }
                    }
                }
            }

            List<Spell> result = GetHighestLevelSpells(spells);

            //foreach (Spell spell in result)
            //    log.Info(spell.Name + " " + spell.Level);

            if (result.Count > 0)
                Spells = result;
        }

        public void SetCasterSpells()
        {
            IList<Specialization> specs = GetSpecList();
            List<Spell> spells = new List<Spell>();

            foreach (Specialization spec in specs)
            {
                var dict = GetAllUsableListSpells();

                if (dict != null && dict.Count > 0)
                {
                    foreach (Tuple<SpellLine, List<Skill>> tuple in dict)
                    {
                        switch (tuple.Item1.Name)
                        {
                            case "Banelord":
                            case "Battlemaster":
                            case "Convoker":
                            case "Perfecter:":
                            case "Sojourner":
                            case "Spymaster":
                            case "Stormlord":
                            case "Warlord":
                                continue;
                        }

                        if (tuple.Item2.Count > 0)
                        {
                            foreach (Skill skill in tuple.Item2)
                            {
                                if (skill is Spell)
                                {
                                    Spell spell = skill as Spell;
                                    spells.Add(spell);
                                }
                            }
                        }
                    }
                }
            }

            List<Spell> result = GetHighestLevelSpells(spells);

            //foreach (Spell spell in result)
            //    log.Info(spell.Name + " " + spell.Level);

            if (result.Count > 0)
                Spells = result;
        }

        public List<Spell> GetHighestLevelSpells(List<Spell> spells)
        {
            List<Spell> result = new List<Spell>();

            for (int i = 0; i < spells.Count; i++)
            {
                Spell currentSpell = spells[i];
                Spell matchingItem = result.FirstOrDefault(x => x.DamageType == currentSpell.DamageType
                                                             && x.SpellType == currentSpell.SpellType
                                                             && x.Radius == currentSpell.Radius
                                                             && x.Frequency == currentSpell.Frequency
                                                             && x.CastTime == currentSpell.CastTime
                                                             && x.Target == currentSpell.Target);

                if (matchingItem == null || currentSpell.Level > matchingItem.Level)
                {
                    //TODO: Some spells meet this critera but shouldn't be removed. All Healer spells have various conflict, Thane DD spells, Skald DD spells are some.
                    //if (currentSpell.SpellType == "Heal")
                    //{
                    //    if (matchingItem != null && matchingItem.Level != 46 && )
                    //    {
                    //        result.Add(currentSpell);
                    //        continue;
                    //    }
                    //}

                    result.RemoveAll(x => x.DamageType == currentSpell.DamageType
                                       && x.SpellType == currentSpell.SpellType
                                       && x.Radius == currentSpell.Radius
                                       && x.Frequency == currentSpell.Frequency
                                       && x.CastTime == currentSpell.CastTime
                                       && x.Target == currentSpell.Target);

                    result.Add(currentSpell);
                }
            }

            return result;
        }

        /// <summary>
        //        /// Default getter for Ability
        //        /// Return Abilities it lists depending on spec level
        //        /// Override to change the condition...
        //        /// </summary>
        //        /// <param name="living"></param>
        //        /// <param name="level">level is only used when called for pretending some level (for trainer display)</param>
        //        /// <returns></returns>
        protected virtual List<Ability> GetAbilitiesForMimic(string keyName, GameLiving living, int level)
        {
            // Select only Enabled and Max Level Abilities
            List<Ability> abs = SkillBase.GetSpecAbilityList(keyName, ((MimicNPC)living).CharacterClass.ID);

            // Get order of first appearing skills
            IOrderedEnumerable<Ability> order = abs.GroupBy(item => item.KeyName)
                .Select(ins => ins.OrderBy(it => it.SpecLevelRequirement).First())
                .Where(item => item.SpecLevelRequirement <= level)
                .OrderBy(item => item.SpecLevelRequirement)
                .ThenBy(item => item.ID);

            // Get best of skills
            List<Ability> best = abs.Where(item => item.SpecLevelRequirement <= level)
                .GroupBy(item => item.KeyName)
                .Select(ins => ins.OrderByDescending(it => it.SpecLevelRequirement).First()).ToList();

            List<Ability> results = new List<Ability>();
            // make some kind of "Join" between the order of appearance and the best abilities.
            foreach (Ability ab in order)
            {
                for (int r = 0; r < best.Count; r++)
                {
                    if (best[r].KeyName == ab.KeyName)
                    {
                        results.Add(best[r]);
                        best.RemoveAt(r);
                        break;
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Default Getter For Styles
        /// Return Styles depending on spec level
        /// </summary>
        /// <param name="living"></param>
        /// <param name="level">level is only used when called for pretending some level (for trainer display)</param>
        /// <returns></returns>
        protected virtual List<Style> GetStylesForMimic(string keyName, GameLiving living, int level)
        {
            // Try with Class ID 0 if no class id styles
            int classid = ((MimicNPC)living).CharacterClass.ID;

            List<Style> styles = SkillBase.GetStyleList(keyName, classid);

            if (styles.Count == 0)
                styles = SkillBase.GetStyleList(keyName, 0);

            // Select only enabled Styles and Order them
            return styles.Where(item => item.SpecLevelRequirement <= level)
                .OrderBy(item => item.SpecLevelRequirement)
                .ThenBy(item => item.ID).ToList();
        }

        /// <summary>
        //        /// Default Getter For Spells
        //        /// Retrieve Spell index by SpellLine, List Spell by Level Order
        //        /// Select Only enabled Spells by spec or living level constraint.
        //        /// </summary>
        //        /// <param name="living"></param>
        //        /// <param name="level">level is only used when called for pretending some level (for trainer display)</param>
        //        /// <returns></returns>
        protected virtual IDictionary<SpellLine, List<Skill>> GetLinesSpellsForLiving(GameLiving living, int level, Specialization spec)
        {
            IDictionary<SpellLine, List<Skill>> dict = new Dictionary<SpellLine, List<Skill>>();

            foreach (SpellLine sl in GetSpellLinesForMimic(living, level, spec.KeyName))
            {
                dict.Add(sl, SkillBase.GetSpellList(sl.KeyName)
                         .Where(item => item.Level <= sl.Level)
                         .OrderBy(item => item.Level)
                         .ThenBy(item => item.ID).Cast<Skill>().ToList());
            }

            return dict;
        }

        /// <summary>
        //        /// Default getter for SpellLines
        //        /// Retrieve spell line depending on advanced class and class hint
        //        /// Order by Baseline
        //        /// </summary>
        //        /// <param name="living"></param>
        //        /// <param name="level">level is only used when called for pretending some level (for trainer display)</param>
        //        /// <returns></returns>
        protected virtual List<SpellLine> GetSpellLinesForMimic(GameLiving living, int level, string KeyName)
        {
            List<SpellLine> list = new List<SpellLine>();
            IList<Tuple<SpellLine, int>> spsl = SkillBase.GetSpecsSpellLines(KeyName);

            // Get Spell Lines by order of appearance
            if (living is MimicNPC mimic)
            {
                // select only spec line if is advanced class...
                var tmp = spsl.Where(item => (item.Item1.IsBaseLine || mimic.CharacterClass.HasAdvancedFromBaseClass()))
                    .OrderBy(item => (item.Item1.IsBaseLine ? 0 : 1)).ThenBy(item => item.Item1.ID);

                // try with class hint
                var baseline = tmp.Where(item => item.Item1.IsBaseLine && item.Item2 == mimic.CharacterClass.ID);
                if (baseline.Any())
                {
                    foreach (Tuple<SpellLine, int> ls in baseline)
                    {
                        ls.Item1.Level = mimic.Level;
                        list.Add(ls.Item1);
                    }
                }
                else
                {
                    foreach (Tuple<SpellLine, int> ls in tmp.Where(item => item.Item1.IsBaseLine && item.Item2 == 0))
                    {
                        ls.Item1.Level = mimic.Level;
                        list.Add(ls.Item1);
                    }
                }

                // try spec with class hint
                var specline = tmp.Where(item => !item.Item1.IsBaseLine && item.Item2 == mimic.CharacterClass.ID);
                if (specline.Any())
                {
                    foreach (Tuple<SpellLine, int> ls in specline)
                    {
                        ls.Item1.Level = level;
                        list.Add(ls.Item1);
                    }
                }
                else
                {
                    foreach (Tuple<SpellLine, int> ls in tmp.Where(item => !item.Item1.IsBaseLine && item.Item2 == 0))
                    {
                        ls.Item1.Level = level;
                        list.Add(ls.Item1);
                    }
                }
            }

            return list;
        }

        private List<Spell> m_spells = new(0);

        /// <summary>
		/// property of spell array of NPC
		/// </summary>
		public override IList Spells
        {
            get { return m_spells; }
            set
            {
                if (value == null || value.Count < 1)
                {
                    m_spells.Clear();
                    InstantHarmfulSpells = null;
                    HarmfulSpells = null;
                    InstantHealSpells = null;
                    HealSpells = null;
                    BoltSpells = null;
                    InstantCrowdControlSpells = null;
                    CrowdControlSpells = null;
                    InstantMiscSpells = null;
                    MiscSpells = null;
                }
                else
                {
                    m_spells = value.Cast<Spell>().ToList();
                    //if(!SortedSpells)
                    SortSpells();
                }
            }
        }

        public override void SortSpells()
        {
            if (Spells.Count < 1)
                return;

            // Clear the lists
            if (InstantHarmfulSpells != null)
                InstantHarmfulSpells.Clear();
            if (HarmfulSpells != null)
                HarmfulSpells.Clear();

            if (InstantHealSpells != null)
                InstantHealSpells.Clear();
            if (HealSpells != null)
                HealSpells.Clear();

            if (InstantCrowdControlSpells != null)
                InstantCrowdControlSpells.Clear();
            if (CrowdControlSpells != null)
                CrowdControlSpells.Clear();

            if (BoltSpells != null)
                BoltSpells.Clear();

            if (InstantMiscSpells != null)
                InstantMiscSpells.Clear();
            if (MiscSpells != null)
                MiscSpells.Clear();

            // Sort spells into lists
            foreach (Spell spell in m_spells)
            {
                if (spell == null)
                    continue;

                // Nightshades were casting weapon poisons as instant spells.
                if (CharacterClass.ID == (int)eCharacterClass.Nightshade && (spell.SpellType != eSpellType.NightshadeNuke))
                    continue;

                if (spell.SpellType == eSpellType.Bolt)
                {
                    if (BoltSpells == null)
                        BoltSpells = new List<Spell>(1);
                    BoltSpells.Add(spell);
                }
                else if (spell.SpellType == eSpellType.Mesmerize || (spell.SpellType == eSpellType.SpeedDecrease && spell.Value >= 99))
                {
                    //if (spell.IsInstantCast)
                    //{
                    //    if (InstantCrowdControlSpells == null)
                    //        InstantCrowdControlSpells = new List<Spell>(1);
                    //    InstantCrowdControlSpells.Add(spell);
                    //}
                    //else
                    //{
                    if (CrowdControlSpells == null)
                        CrowdControlSpells = new List<Spell>(1);
                    CrowdControlSpells.Add(spell);
                    //}
                }
                else if (spell.IsHarmful)
                {
                    if (spell.IsInstantCast)
                    {
                        if (InstantHarmfulSpells == null)
                            InstantHarmfulSpells = new List<Spell>(1);
                        InstantHarmfulSpells.Add(spell);
                    }
                    else
                    {
                        if (HarmfulSpells == null)
                            HarmfulSpells = new List<Spell>(1);
                        HarmfulSpells.Add(spell);
                    }
                }
                else if (spell.IsHealing && !spell.IsPulsing)
                {
                    if (HealSpells == null)
                        HealSpells = new List<Spell>(1);
                    HealSpells.Add(spell);
                }
                else
                {
                    // Skald speed is instant, but instant misc isn't checked outside combat. Add an exception.
                    if (spell.IsInstantCast && spell.SpellType != eSpellType.SpeedEnhancement)
                    {
                        if (InstantMiscSpells == null)
                            InstantMiscSpells = new List<Spell>(1);
                        InstantMiscSpells.Add(spell);
                    }
                    else
                    {
                        if (MiscSpells == null)
                            MiscSpells = new List<Spell>(1);
                        MiscSpells.Add(spell);
                    }
                }
            } // foreach

            //SortedSpells = true;
        }

        public void SetLevel(byte level)
        {
            Level = level;
            //Experience = ExperienceForNextLevel - 1;
            Experience = ExperienceForCurrentLevel;

            if (level >= 20 && level < 25)
                RealmLevel = Util.Random(1, 10);
            else if (level > 24 && level < 30)
                RealmLevel = Util.Random(1, 15);
            else if (level > 29 && level < 35)
                RealmLevel = Util.Random(1, 25);
            else if (level > 39 && level < 50)
                RealmLevel = Util.Random(1, 45);
            else if (level == 50)
                RealmLevel = Util.Random(1, 90);

            RealmPoints = REALMPOINTS_FOR_LEVEL[RealmLevel];
        }

        public void SetRaceAndName()
        {
            PlayerRace playerRace = CharacterClass.EligibleRaces[Util.Random(CharacterClass.EligibleRaces.Count - 1)];

            Gender = Util.RandomBool() ? eGender.Female : eGender.Male;
            Race = (short)playerRace.ID;
            Model = (ushort)playerRace.GetModel(Gender);
            Size = (byte)Util.Random(45, 60);

            Dictionary<eStat, int> statDict;
            GlobalConstants.STARTING_STATS_DICT.TryGetValue((eRace)Race, out statDict);

            ChangeBaseStat(eStat.STR, (short)statDict[eStat.STR]);
            ChangeBaseStat(eStat.CON, (short)statDict[eStat.CON]);
            ChangeBaseStat(eStat.DEX, (short)statDict[eStat.DEX]);
            ChangeBaseStat(eStat.QUI, (short)statDict[eStat.QUI]);
            ChangeBaseStat(eStat.INT, (short)statDict[eStat.INT]);
            ChangeBaseStat(eStat.PIE, (short)statDict[eStat.PIE]);
            ChangeBaseStat(eStat.EMP, (short)statDict[eStat.EMP]);
            ChangeBaseStat(eStat.CHR, (short)statDict[eStat.CHR]);
            ChangeBaseStat(CharacterClass.PrimaryStat, 10);
            ChangeBaseStat(CharacterClass.SecondaryStat, 10);
            ChangeBaseStat(CharacterClass.TertiaryStat, 10);

            foreach (KeyValuePair<eRealm, List<eCharacterClass>> keyValuePair in GlobalConstants.STARTING_CLASSES_DICT)
            {
                if (keyValuePair.Value.Contains((eCharacterClass)CharacterClass.ID))
                {
                    Realm = keyValuePair.Key;
                    break;
                }
            }

            switch (Realm)
            {
                case eRealm.Albion: Faction = MimicManager.alb; break;
                case eRealm.Hibernia: Faction = MimicManager.hib; break;
                case eRealm.Midgard: Faction = MimicManager.mid; break;
            }

            Name = MimicNames.GetName(Gender, Realm);
        }

        public override void StartAttack(GameObject target)
        {
            if (IsSitting)
                Sit(false);

            base.StartAttack(target);
        }

        public override bool AddToWorld()
        {
            if (!(base.AddToWorld()))
                return false;

            m_healthRegenerationTimer = new ECSGameTimer(this);
            m_powerRegenerationTimer = new ECSGameTimer(this);
            m_enduRegenerationTimer = new ECSGameTimer(this);

            m_healthRegenerationTimer.Callback = new ECSGameTimer.ECSTimerCallback(HealthRegenerationTimerCallback);
            m_powerRegenerationTimer.Callback = new ECSGameTimer.ECSTimerCallback(PowerRegenerationTimerCallback);
            m_enduRegenerationTimer.Callback = new ECSGameTimer.ECSTimerCallback(EnduranceRegenerationTimerCallback);

            return true;
        }

        private readonly object m_LockObject = new object();
        public int Regen { get; set; }
        public int Endchant { get; set; }
        public long LastEnduTick { get; set; }
        public int RegenRateAtChange { get; set; }
        public int EnduDebuff { get; set; }
        public double RegenBuff { get; set; }
        public double RegenAfterTireless { get; set; }
        public double NonCombatNonSprintRegen { get; set; }
        public double CombatRegen { get; set; }
        public double SpecLock { get; set; }
        public long LastWorldUpdate { get; set; }

        public ECSGameTimer EnduRegenTimer { get { return m_enduRegenerationTimer; } }

        private PlayerDeck _randomNumberDeck;

        #region Client/Character/VariousFlags

        /// <summary>
        /// This is our gameclient!
        /// </summary>
        protected readonly GameClient m_client;

        /// <summary>
        /// This holds the character this player is
        /// based on!
        /// (renamed and private, cause if derive is needed overwrite PlayerCharacter)
        /// </summary>
        protected DbCoreCharacter m_dbCharacter;

        /// <summary>
        /// The guild id this character belong to
        /// </summary>
        protected string m_guildId;

        /// <summary>
        /// Char spec points checked on load
        /// </summary>
        protected bool SpecPointsOk = true;

        /// <summary>
        /// Has this player entered the game, will be
        /// true after the first time the char enters
        /// the world
        /// </summary>
        protected bool m_enteredGame;

        /// <summary>
        /// Is this player being 'jumped' to a new location?
        /// </summary>
        public bool IsJumping { get; set; }

        /// <summary>
        /// true if the targetObject is visible
        /// </summary>
        protected bool m_targetInView;

        /// <summary>
        /// Property for the optional away from keyboard message.
        /// </summary>
        public static readonly string AFK_MESSAGE = "afk_message";

        /// <summary>
        /// Property for the optional away from keyboard message.
        /// </summary>
        public static readonly string QUICK_CAST_CHANGE_TICK = "quick_cast_change_tick";

        /// <summary>
        /// Last spell cast from a used item
        /// </summary>
        public static readonly string LAST_USED_ITEM_SPELL = "last_used_item_spell";

        public static readonly string REALM_LOYALTY_KEY = "realm_loyalty";
        public static readonly string CURRENT_LOYALTY_KEY = "current_loyalty_days";

        public static readonly string DUEL_PREVIOUS_LASTATTACKTICKPVP = "DUEL_PREVIOUS_LASTATTACKTICKPVP";
        public static readonly string DUEL_PREVIOUS_LASTATTACKEDBYENEMYTICKPVP = "DUEL_PREVIOUS_LASTATTACKEDBYENEMYTICKPVP";

        /// <summary>
        /// Effectiveness of the rez sick that should be applied. This is set by rez spells just before rezzing.
        /// </summary>
        public static readonly string RESURRECT_REZ_SICK_EFFECTIVENESS = "RES_SICK_EFFECTIVENESS";

        /// <summary>
        /// Array that stores ML step completition
        /// </summary>
        private ArrayList m_mlSteps = new ArrayList();

        private bool m_gmStealthed = false;
        public bool GMStealthed { get { return m_gmStealthed; } set { m_gmStealthed = value; } }

        /// <summary>
        /// Can this living accept any item regardless of tradable or droppable?
        /// </summary>
        public override bool CanTradeAnyItem { get { return Client.Account.PrivLevel > (int)ePrivLevel.Player; } }

        /// <summary>
        /// Gets or sets the targetObject's visibility
        /// </summary>
        public override bool TargetInView
        {
            get
            {
                if (GetDistanceTo(TargetObject) <= TargetInViewAlwaysTrueMinRange)
                    return true;

                return m_targetInView;
            }
            set => m_targetInView = value;
        }

        public override int TargetInViewAlwaysTrueMinRange => (TargetObject is GamePlayer targetPlayer && targetPlayer.IsMoving) ? 100 : 64;

        public PlayerDeck RandomNumberDeck
        {
            get
            {
                if (_randomNumberDeck == null)
                    _randomNumberDeck = new PlayerDeck();

                return _randomNumberDeck;
            }
            set { _randomNumberDeck = value; }
        }

        /// <summary>
        /// Holds the ground target visibility flag
        /// </summary>
        protected bool m_groundtargetInView;

        /// <summary>
        /// Gets or sets the GroundTargetObject's visibility
        /// </summary>
        public override bool GroundTargetInView
        {
            get { return m_groundtargetInView; }
            set { m_groundtargetInView = value; }
        }

        protected int m_OutOfClassROGPercent = 0;

        public int OutOfClassROGPercent
        {
            get { return m_OutOfClassROGPercent; }
            set { m_OutOfClassROGPercent = value; }
        }

        /// <summary>
        /// Player is in BG ?
        /// </summary>
        protected bool m_isInBG;
        public bool isInBG
        {
            get { return m_isInBG; }
            set { m_isInBG = value; }
        }

        protected bool m_usedetailedcombatlog = false;

        public bool UseDetailedCombatLog
        {
            get { return m_usedetailedcombatlog; }
            set { m_usedetailedcombatlog = value; }
        }

        public eXPLogState XPLogState
        {
            get { return m_xplogstate; }
            set { m_xplogstate = value; }
        }

        private eXPLogState m_xplogstate = 0;

        /// <summary>
        /// Current warmap page
        /// </summary>
        private volatile byte m_warmapPage = 1;
        public byte WarMapPage
        {
            get { return m_warmapPage; }
            set { m_warmapPage = value; }
        }

        /// <summary>
        /// Returns the GameClient of this Player
        /// </summary>
        public virtual GameClient Client
        {
            get { return m_client; }
        }

        /// <summary>
        /// The character the player is based on
        /// </summary>
        internal DbCoreCharacter DBCharacter
        {
            get { return m_dbCharacter; }
        }

        /// <summary>
        /// Has this player entered the game for the first
        /// time after logging on (not Zoning!)
        /// </summary>
        public bool EnteredGame
        {
            get { return m_enteredGame; }
            set { m_enteredGame = value; }
        }

        protected DateTime m_previousLoginDate = DateTime.MinValue;
        /// <summary>
        /// What was the last time this player logged in?
        /// </summary>
        public DateTime PreviousLoginDate
        {
            get { return m_previousLoginDate; }
            set { m_previousLoginDate = value; }
        }

        /// <summary>
        /// Gets or sets the anonymous flag for this player
        /// (delegate to property in PlayerCharacter)
        /// </summary>
        public bool IsAnonymous
        {
            get { return DBCharacter != null ? DBCharacter.IsAnonymous && (ServerProperties.Properties.ANON_MODIFIER != -1) : false; }
            set
            {
                var old = IsAnonymous;
                if (DBCharacter != null)
                    DBCharacter.IsAnonymous = value;

                if (old != IsAnonymous)
                    GameEventMgr.Notify(GamePlayerEvent.ChangeAnonymous, this);
            }
        }

        /// <summary>
        /// Whether or not the player can be attacked.
        /// </summary>
        public override bool IsAttackable { get { return (Client.Account.PrivLevel <= (uint)ePrivLevel.Player && base.IsAttackable); } }

        /// <summary>
        /// Can this player use cross realm items
        /// </summary>
        public virtual bool CanUseCrossRealmItems { get { return ServerProperties.Properties.ALLOW_CROSS_REALM_ITEMS; } }

        protected bool m_canUseSlashLevel = false;
        public bool CanUseSlashLevel { get { return m_canUseSlashLevel; } }

        /// <summary>
        /// if player uses debug before (to prevent hack client fly mode for players using debug and then turning it off)
        /// </summary>
        protected bool m_canFly;

        /// <summary>
        /// Is this player allowed to fly?
        /// This should only be set in debug command handler.  If player is flying but this flag is false then fly hack is detected
        /// </summary>
        public bool IsAllowedToFly
        {
            get { return m_canFly; }
            set { m_canFly = value; }
        }

        private bool m_statsAnon = false;

        /// <summary>
        /// Gets or sets the stats anon flag for the command /statsanon
        /// (delegate to property in PlayerCharacter)
        /// </summary>
        public bool StatsAnonFlag
        {
            get { return m_statsAnon; }
            set { m_statsAnon = value; }
        }

        protected bool m_lastDeathPvP;

        public bool LastDeathPvP
        {
            get { return m_lastDeathPvP; }
            set { m_lastDeathPvP = value; }
        }

        protected bool m_wasmovedbycorpsesummoner;

        public bool WasMovedByCorpseSummoner
        {
            get { return m_wasmovedbycorpsesummoner; }
            set { m_wasmovedbycorpsesummoner = value; }
        }

        #region Object Caches
        public ConcurrentDictionary<GameNPC, ClientService.CachedNpcValues> NpcUpdateCache { get; private set; } = new();
        public ConcurrentDictionary<GameStaticItem, long> ItemUpdateCache { get; private set; } = new();
        public ConcurrentDictionary<GameDoorBase, long> DoorUpdateCache { get; private set; } = new();
        public ConcurrentDictionary<House, long> HouseUpdateCache { get; private set; } = new();
        #endregion

        #region Database Accessor

        /// <summary>
        /// Gets or sets the Database ObjectId for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public string ObjectId
        {
            get { return DBCharacter != null ? DBCharacter.ObjectId : InternalID; }
            set { if (DBCharacter != null) DBCharacter.ObjectId = value; }
        }

        /// <summary>
        /// Gets or sets the no help flag for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public bool NoHelp
        {
            get { return DBCharacter != null ? DBCharacter.NoHelp : false; }
            set { if (DBCharacter != null) DBCharacter.NoHelp = value; }
        }

        /// <summary>
        /// Gets or sets the show guild logins flag for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public bool ShowGuildLogins
        {
            get { return DBCharacter != null ? DBCharacter.ShowGuildLogins : false; }
            set { if (DBCharacter != null) DBCharacter.ShowGuildLogins = value; }
        }

        /// <summary>
        /// Gets or sets the gain XP flag for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public bool GainXP
        {
            get { return true; }
            set { if (DBCharacter != null) DBCharacter.GainXP = value; }
        }

        /// <summary>
        /// Gets or sets the gain RP flag for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public bool GainRP
        {
            get { return true; }
            set { if (DBCharacter != null) DBCharacter.GainRP = value; }
        }

        /// <summary>
        /// Gets or sets the roleplay flag for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public bool RPFlag
        {
            get { return (DBCharacter != null ? DBCharacter.RPFlag : true); }
            set { if (DBCharacter != null) DBCharacter.RPFlag = value; }
        }

        /// <summary>
        /// Gets or sets the hardcore flag for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public bool HCFlag
        {
            get { return false; }
            set { if (DBCharacter != null) DBCharacter.HCFlag = value; }
        }

        /// <summary>
        /// Gets or sets the HideSpecializationAPI for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public bool HideSpecializationAPI
        {
            get { return (DBCharacter != null ? DBCharacter.HideSpecializationAPI : false); }
            set { if (DBCharacter != null) DBCharacter.HideSpecializationAPI = value; }
        }

        /// <summary>
        /// Gets or sets the hardcore flag for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public bool HCCompleted
        {
            get { return false; }
            set { if (DBCharacter != null) DBCharacter.HCCompleted = value; }
        }

        /// <summary>
        /// Gets or sets the boosted flag for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public bool Boosted
        {
            get { return (DBCharacter != null ? DBCharacter.isBoosted : true); }
            set { if (DBCharacter != null) DBCharacter.isBoosted = value; }
        }

        /// <summary>
        /// gets or sets the guildnote for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public string GuildNote
        {
            get { return DBCharacter != null ? DBCharacter.GuildNote : String.Empty; }
            set { if (DBCharacter != null) DBCharacter.GuildNote = value; }
        }

        /// <summary>
        /// Gets or sets the autoloot flag for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public bool Autoloot
        {
            get { return DBCharacter != null ? DBCharacter.Autoloot : true; }
            set { if (DBCharacter != null) DBCharacter.Autoloot = value; }
        }

        /// <summary>
        /// Gets or sets the advisor flag for this player
        /// (delegate to property in PlayerCharacter)
        /// </summary>
        public bool Advisor
        {
            get { return DBCharacter != null ? DBCharacter.Advisor : false; }
            set { if (DBCharacter != null) DBCharacter.Advisor = value; }
        }

        /// <summary>
        /// Gets or sets the SerializedFriendsList for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public string[] SerializedFriendsList
        {
            get { return DBCharacter != null ? DBCharacter.SerializedFriendsList.Split(',').Select(name => name.Trim()).Where(name => !string.IsNullOrEmpty(name)).ToArray() : new string[0]; }
            set { if (DBCharacter != null) DBCharacter.SerializedFriendsList = string.Join(",", value.Select(name => name.Trim()).Where(name => !string.IsNullOrEmpty(name))); }
        }

        /// <summary>
        /// Gets or sets the NotDisplayedInHerald for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public byte NotDisplayedInHerald
        {
            get { return DBCharacter != null ? DBCharacter.NotDisplayedInHerald : (byte)0; }
            set { if (DBCharacter != null) DBCharacter.NotDisplayedInHerald = value; }
        }

        /// <summary>
        /// Gets or sets the LastFreeLevel for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public int LastFreeLevel
        {
            get { return DBCharacter != null ? DBCharacter.LastFreeLevel : 0; }
            set { if (DBCharacter != null) DBCharacter.LastFreeLevel = value; }
        }

        /// <summary>
        /// Gets or sets the LastFreeLeveled for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public DateTime LastFreeLeveled
        {
            get { return DBCharacter != null ? DBCharacter.LastFreeLeveled : DateTime.MinValue; }
            set { if (DBCharacter != null) DBCharacter.LastFreeLeveled = value; }
        }

        /// <summary>
        /// Gets or sets the SerializedIgnoreList for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public string[] SerializedIgnoreList
        {
            get { return DBCharacter != null ? DBCharacter.SerializedIgnoreList.Split(',').Select(name => name.Trim()).Where(name => !string.IsNullOrEmpty(name)).ToArray() : new string[0]; }
            set { if (DBCharacter != null) DBCharacter.SerializedIgnoreList = string.Join(",", value.Select(name => name.Trim()).Where(name => !string.IsNullOrEmpty(name))); }
        }

        /// <summary>
        /// Gets or sets the UsedLevelCommand for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public bool UsedLevelCommand
        {
            get { return DBCharacter != null ? DBCharacter.UsedLevelCommand : false; }
            set { if (DBCharacter != null) DBCharacter.UsedLevelCommand = value; }
        }

        /// <summary>
        /// Gets or sets the BindHouseRegion for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public int BindHouseRegion
        {
            get { return DBCharacter != null ? DBCharacter.BindHouseRegion : 0; }
            set { if (DBCharacter != null) DBCharacter.BindHouseRegion = value; }
        }

        /// <summary>
        /// Gets or sets the BindHouseXpos for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public int BindHouseXpos
        {
            get { return DBCharacter != null ? DBCharacter.BindHouseXpos : 0; }
            set { if (DBCharacter != null) DBCharacter.BindHouseXpos = value; }
        }

        /// <summary>
        /// Gets or sets the BindHouseYpos for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public int BindHouseYpos
        {
            get { return DBCharacter != null ? DBCharacter.BindHouseYpos : 0; }
            set { if (DBCharacter != null) DBCharacter.BindHouseYpos = value; }
        }

        /// <summary>
        /// Gets or sets BindHouseZpos for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public int BindHouseZpos
        {
            get { return DBCharacter != null ? DBCharacter.BindHouseZpos : 0; }
            set { if (DBCharacter != null) DBCharacter.BindHouseZpos = value; }
        }

        /// <summary>
        /// Gets or sets the BindHouseHeading for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public int BindHouseHeading
        {
            get { return DBCharacter != null ? DBCharacter.BindHouseHeading : 0; }
            set { if (DBCharacter != null) DBCharacter.BindHouseHeading = value; }
        }

        /// <summary>
        /// Gets or sets the CustomisationStep for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public byte CustomisationStep
        {
            get { return DBCharacter != null ? DBCharacter.CustomisationStep : (byte)0; }
            set { if (DBCharacter != null) DBCharacter.CustomisationStep = value; }
        }

        /// <summary>
        /// Gets or sets the IgnoreStatistics for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public bool IgnoreStatistics
        {
            get { return DBCharacter != null ? DBCharacter.IgnoreStatistics : false; }
            set { if (DBCharacter != null) DBCharacter.IgnoreStatistics = value; }
        }

        /// <summary>
        /// Gets or sets the DeathTime for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public long DeathTime
        {
            get { return DBCharacter != null ? DBCharacter.DeathTime : 0; }
            set { if (DBCharacter != null) DBCharacter.DeathTime = value; }
        }

        /// <summary>
        /// Gets or sets the ShowXFireInfo for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public bool ShowXFireInfo
        {
            get { return DBCharacter != null ? DBCharacter.ShowXFireInfo : false; }
            set { if (DBCharacter != null) DBCharacter.ShowXFireInfo = value; }
        }

        /// <summary>
        /// Gets/sets the characters option to receive ROGs /eventrog
        /// (delegate to property in DBCharacter)
        /// </summary>
        public bool ReceiveROG
        {
            get { return DBCharacter != null ? DBCharacter.ReceiveROG : true; }
            set { if (DBCharacter != null) DBCharacter.ReceiveROG = value; }
        }

        /// <summary>
        /// Gets or sets the BindRegion for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public int BindRegion
        {
            get { return DBCharacter != null ? DBCharacter.BindRegion : 0; }
            set { if (DBCharacter != null) DBCharacter.BindRegion = value; }
        }

        /// <summary>
        /// Gets or sets the BindXpos for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public int BindXpos
        {
            get { return DBCharacter != null ? DBCharacter.BindXpos : 0; }
            set { if (DBCharacter != null) DBCharacter.BindXpos = value; }
        }

        /// <summary>
        /// Gets or sets the BindYpos for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public int BindYpos
        {
            get { return DBCharacter != null ? DBCharacter.BindYpos : 0; }
            set { if (DBCharacter != null) DBCharacter.BindYpos = value; }
        }

        /// <summary>
        /// Gets or sets the BindZpos for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public int BindZpos
        {
            get { return DBCharacter != null ? DBCharacter.BindZpos : 0; }
            set { if (DBCharacter != null) DBCharacter.BindZpos = value; }
        }

        /// <summary>
        /// Gets or sets the BindHeading for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public int BindHeading
        {
            get { return DBCharacter != null ? DBCharacter.BindHeading : 0; }
            set { if (DBCharacter != null) DBCharacter.BindHeading = value; }
        }

        /// <summary>
        /// Gets or sets the Database MaxEndurance for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public int DBMaxEndurance
        {
            get { return DBCharacter != null ? DBCharacter.MaxEndurance : 100; }
            set { if (DBCharacter != null) DBCharacter.MaxEndurance = value; }
        }

        /// <summary>
        /// Gets AccountName for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public string AccountName
        {
            get { return DBCharacter != null ? DBCharacter.AccountName : string.Empty; }
        }

        /// <summary>
        /// Gets CreationDate for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public DateTime CreationDate
        {
            get { return DBCharacter != null ? DBCharacter.CreationDate : DateTime.MinValue; }
        }

        /// <summary>
        /// Gets LastPlayed for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public DateTime LastPlayed
        {
            get { return DBCharacter != null ? DBCharacter.LastPlayed : DateTime.MinValue; }
        }

        /// <summary>
        /// Gets or sets the BindYpos for this player
        /// (delegate to property in DBCharacter)
        /// </summary>
        public byte DeathCount
        {
            get { return DBCharacter != null ? DBCharacter.DeathCount : (byte)0; }
            set { if (DBCharacter != null) DBCharacter.DeathCount = value; }
        }

        private int m_killstreak = 0;

        public int KillStreak
        {
            get { return m_killstreak; }
            set { m_killstreak = value; }
        }

        //public long PlayedTimeSinceLevel
        //{
        //    get { return DBCharacter != null ? DBCharacter.PlayedTimeSinceLevel : 0; }
        //    set { if (DBCharacter != null) DBCharacter.PlayedTimeSinceLevel = PlayedTimeSinceLevel; }
        //}

        /// <summary>
        /// Gets the last time this player leveled up
        /// </summary>
        public DateTime LastLevelUp
        {
            get { return DBCharacter != null ? DBCharacter.LastLevelUp : DateTime.MinValue; }
            set { if (DBCharacter != null) DBCharacter.LastLevelUp = value; }
        }
        #endregion

        #endregion

        #region Player Quitting
        /// <summary>
        /// quit timer
        /// </summary>
        protected AuxECSGameTimer m_quitTimer;

        /// <summary>
        /// Timer callback for quit
        /// </summary>
        /// <param name="callingTimer">the calling timer</param>
        /// <returns>the new intervall</returns>
        protected virtual int QuitTimerCallback(AuxECSGameTimer callingTimer)
        {
            if (!IsAlive || ObjectState != eObjectState.Active)
            {
                m_quitTimer = null;
                return 0;
            }

            bool bInstaQuit = false;

            if (Client.Account.PrivLevel > 1) // GMs can always insta quit
                bInstaQuit = true;
            else if (ServerProperties.Properties.DISABLE_QUIT_TIMER && Client.Player.InCombat == false)  // Players can only insta quit if they aren't in combat
                bInstaQuit = true;

            if (bInstaQuit == false)
            {
                //if (CraftTimer != null && CraftTimer.IsAlive)
                //{
                //    Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Quit.CantQuitCrafting"), eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                //    m_quitTimer = null;
                //    return 0;
                //}

                long lastCombatAction = LastAttackedByEnemyTick;
                if (lastCombatAction < LastAttackTick)
                {
                    lastCombatAction = LastAttackTick;
                }
                long secondsleft = 60 - (GameLoop.GameLoopTime - lastCombatAction + 500) / 1000; // 500 is for rounding
                if (secondsleft > 0)
                {
                    if (secondsleft == 15 || secondsleft == 10 || secondsleft == 5)
                    {
                        // Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Quit.YouWillQuit1", secondsleft), eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                    }
                    return 1000;
                }
            }

            if (CharacterClass.ID == (int)eCharacterClass.Necromancer && IsShade)
                Shade(false);

            // Out.SendPlayerQuit(false);
            //Quit(true);
            //CraftingProgressMgr.FlushAndSaveInstance(this);
            SaveIntoDatabase();
            m_quitTimer?.Stop();
            m_quitTimer = null;
            return 0;
        }

        /// <summary>
        /// Gets the amount of time the player must wait before quit, in seconds
        /// </summary>
        public virtual int QuitTime
        {
            get
            {
                if (m_quitTimer == null)
                {
                    // dirty trick ;-) (20sec min quit time)
                    //Commenting out the LastAttackTickPvP part as it was messing up the Realm Timer.
                    // if (GameLoop.GameLoopTime - LastAttackTickPvP > 40000)
                    //     LastAttackTickPvP = GameLoop.GameLoopTime - 40000;
                    if (GameLoop.GameLoopTime - LastAttackTickPvE > 40000)
                        LastAttackTickPvE = GameLoop.GameLoopTime - 40000;
                }
                long lastCombatAction = LastAttackTick;
                if (lastCombatAction < LastAttackedByEnemyTick)
                {
                    lastCombatAction = LastAttackedByEnemyTick;
                }

                return (int)(60 - (GameLoop.GameLoopTime - lastCombatAction + 500) / 1000); // 500 is for rounding
            }
            set
            { }
        }

        #endregion

        #region Combat timer
        /// <summary>
        /// gets the DamageRvR Memory of this player
        /// </summary>
        public override long DamageRvRMemory
        {
            get => m_damageRvRMemory;
            set => m_damageRvRMemory = value;
        }

        public override long LastAttackTickPvE
        {
            set
            {
                bool wasInCombat = InCombat;
                base.LastAttackTickPvE = value;

                //if (!wasInCombat && InCombat)
                //    Out.SendUpdateMaxSpeed();

                ResetInCombatTimer();
            }
        }

        public override long LastAttackTickPvP
        {
            set
            {
                bool wasInCombat = InCombat;
                base.LastAttackTickPvP = value;

                //if (!wasInCombat && InCombat)
                //    Out.SendUpdateMaxSpeed();

                ResetInCombatTimer();
            }
        }

        public override long LastAttackedByEnemyTickPvE
        {
            set
            {
                bool wasInCombat = InCombat;
                base.LastAttackedByEnemyTickPvE = value;

                //if (!wasInCombat && InCombat)
                //    Out.SendUpdateMaxSpeed();

                ResetInCombatTimer();
            }
        }

        public override long LastAttackedByEnemyTickPvP
        {
            set
            {
                bool wasInCombat = InCombat;
                base.LastAttackTickPvP = value;
                //if (!wasInCombat && InCombat)
                //    Out.SendUpdateMaxSpeed();

                ResetInCombatTimer();
            }
        }

        /// <summary>
        /// Expire Combat Timer Interval
        /// </summary>
        private static int COMBAT_TIMER_INTERVAL => 11000;

        /// <summary>
        /// Combat Timer
        /// </summary>
        private ECSGameTimer m_combatTimer;

        /// <summary>
        /// Reset and Restart Combat Timer
        /// </summary>
        protected virtual void ResetInCombatTimer()
        {
            m_combatTimer.Start(COMBAT_TIMER_INTERVAL);
        }

        #endregion

        #region release/bind/pray
        #region Binding
        /// <summary>
        /// Property that holds tick when the player bind last time
        /// </summary>
        public const string LAST_BIND_TICK = "LastBindTick";

        /// <summary>
        /// Min Allowed Interval Between Player Bind
        /// </summary>
        public virtual int BindAllowInterval { get { return 60000; } }

        /// <summary>
        /// Binds this player to the current location
        /// </summary>
        /// <param name="forced">if true, can bind anywhere</param>
        public virtual void Bind(bool forced)
        {
            if (CurrentRegion.IsInstance)
            {
                //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Bind.CantHere"), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return;
            }

            if (forced)
            {
                BindRegion = CurrentRegionID;
                BindHeading = Heading;
                BindXpos = X;
                BindYpos = Y;
                BindZpos = Z;
                if (DBCharacter != null)
                    GameServer.Database.SaveObject(DBCharacter);
                return;
            }

            if (!IsAlive)
            {
                //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Bind.CantBindDead"), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return;
            }

            //60 second rebind timer
            long lastBindTick = TempProperties.GetProperty<long>(LAST_BIND_TICK, 0);
            long changeTime = CurrentRegion.Time - lastBindTick;
            if (Client.Account.PrivLevel <= (uint)ePrivLevel.Player && changeTime < BindAllowInterval)
            {
                //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Bind.MustWait", (1 + (BindAllowInterval - changeTime) / 1000)), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return;
            }

            //string description = string.Format("in {0}", this.GetBindSpotDescription());
            //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Bind.LastBindPoint", description), eChatType.CT_System, eChatLoc.CL_SystemWindow);

            bool bound = false;

            //var bindarea = CurrentAreas.OfType<Area.BindArea>().FirstOrDefault(ar => GameServer.ServerRules.IsAllowedToBind(this, ar.BindPoint));
            //if (bindarea != null)
            //{
            //    bound = true;
            //    BindRegion = CurrentRegionID;
            //    BindHeading = Heading;
            //    BindXpos = X;
            //    BindYpos = Y;
            //    BindZpos = Z;
            //    if (DBCharacter != null)
            //        GameServer.Database.SaveObject(DBCharacter);
            //}

            //if we are not bound yet lets check if we are in a house where we can bind
            if (!bound && InHouse && CurrentHouse != null)
            {
                var house = CurrentHouse;
                bool canbindhere;
                try
                {
                    canbindhere = house.HousepointItems.Any(kv => ((GameObject)kv.Value.GameObject).GetName(0, false).EndsWith("bindstone", StringComparison.OrdinalIgnoreCase));
                }
                catch
                {
                    canbindhere = false;
                }

                if (canbindhere)
                {
                    // make sure we can actually use the bindstone
                    //if (!house.CanBindInHouse(this))
                    //{
                    //    Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Bind.CantHere"), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    //    return;
                    //}
                    //else
                    //{
                    //    bound = true;
                    //    double angle = house.Heading * ((Math.PI * 2) / 360); // angle*2pi/360;
                    //    int outsideX = (int)(house.X + (0 * Math.Cos(angle) + 500 * Math.Sin(angle)));
                    //    int outsideY = (int)(house.Y - (500 * Math.Cos(angle) - 0 * Math.Sin(angle)));
                    //    ushort outsideHeading = (ushort)((house.Heading < 180 ? house.Heading + 180 : house.Heading - 180) / 0.08789);
                    //    BindHouseRegion = CurrentRegionID;
                    //    BindHouseHeading = outsideHeading;
                    //    BindHouseXpos = outsideX;
                    //    BindHouseYpos = outsideY;
                    //    BindHouseZpos = house.Z;
                    //    if (DBCharacter != null)
                    //        GameServer.Database.SaveObject(DBCharacter);
                    //}
                }
            }

            if (bound)
            {
                if (!IsMoving)
                {
                    eEmote bindEmote = eEmote.Bind;
                    switch (Realm)
                    {
                        case eRealm.Albion: bindEmote = eEmote.BindAlb; break;
                        case eRealm.Midgard: bindEmote = eEmote.BindMid; break;
                        case eRealm.Hibernia: bindEmote = eEmote.BindHib; break;
                    }

                    foreach (GamePlayer player in GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
                    {
                        if (player == null)
                            return;

                        if ((int)player.Client.Version < (int)GameClient.eClientVersion.Version187)
                            player.Out.SendEmoteAnimation(this, eEmote.Bind);
                        else
                            player.Out.SendEmoteAnimation(this, bindEmote);
                    }
                }

                TempProperties.SetProperty(LAST_BIND_TICK, CurrentRegion.Time);
                //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Bind.Bound"), eChatType.CT_System, eChatLoc.CL_SystemWindow);
            }
            else
            {
                //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Bind.CantHere"), eChatType.CT_System, eChatLoc.CL_SystemWindow);
            }
        }
        #endregion

        #region Releasing
        /// <summary>
        /// tick when player is died
        /// </summary>
        protected long m_deathTick;

        /// <summary>
        /// choosed the player to release as soon as possible?
        /// </summary>
        protected bool m_automaticRelease = false;

        /// <summary>
        /// The release timer for this player
        /// </summary>
        protected ECSGameTimer m_releaseTimer;

        /// <summary>
        /// Stops release timer and closes timer window
        /// </summary>
        public void StopReleaseTimer()
        {
            //Out.SendCloseTimerWindow();
            if (m_releaseTimer != null)
            {
                m_releaseTimer.Stop();
                m_releaseTimer = null;
            }
        }

        /// <summary>
        /// minimum time to wait before release is possible in seconds
        /// </summary>
        protected const int RELEASE_MINIMUM_WAIT = 10;

        /// <summary>
        /// max time before auto release in seconds
        /// </summary>
        protected const int RELEASE_TIME = 900;

        /// <summary>
        /// The property name that is set when relea
        /// sing to another region
        /// </summary>
        public const string RELEASING_PROPERTY = "releasing";

        /// <summary>
        /// The current release type
        /// </summary>
        protected eReleaseType m_releaseType = eReleaseType.Normal;

        /// <summary>
        /// Gets the player's current release type.
        /// </summary>
        public eReleaseType ReleaseType
        {
            get { return m_releaseType; }
        }

        /// <summary>
        /// Releases this player after death ... subtracts xp etc etc...
        /// </summary>
        /// <param name="releaseCommand">The type of release used for this player</param>
        /// <param name="forced">if true, will release even if not dead</param>
        public virtual void Release(eReleaseType releaseCommand, bool forced)
        {
            DbCoreCharacter character = DBCharacter;
            if (character == null)
                return;

            // check if valid housebind
            if (releaseCommand == eReleaseType.House && character.BindHouseRegion < 1)
            {
                //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Release.NoValidBindpoint"), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                releaseCommand = eReleaseType.Bind;
            }

            //battlegrounds caps
            DbBattleground bg = GameServer.KeepManager.GetBattleground(CurrentRegionID);
            if (bg != null && releaseCommand == eReleaseType.RvR)
            {
                if (Level > bg.MaxLevel)
                    releaseCommand = eReleaseType.Normal;
            }

            if (IsAlive)
            {
                //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Release.NotDead"), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return;
            }

            if (!forced)
            {
                if (m_releaseType == eReleaseType.Duel)
                {
                    //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Release.CantReleaseDuel"), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    return;
                }
                m_releaseType = releaseCommand;
                // we use realtime, because timer window is realtime
                long diff = m_deathTick - GameLoop.GameLoopTime + RELEASE_MINIMUM_WAIT * 1000;
                if (diff >= 1000)
                {
                    if (m_automaticRelease)
                    {
                        m_automaticRelease = false;
                        m_releaseType = eReleaseType.Normal;
                        //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Release.NoLongerReleaseAuto", diff / 1000), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                        return;
                    }

                    m_automaticRelease = true;
                    switch (releaseCommand)
                    {
                        default:
                            {
                                // Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Release.WillReleaseAuto", diff / 1000), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                                return;
                            }
                        case eReleaseType.City:
                            {
                                //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Release.WillReleaseAutoCity", diff / 1000), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                                return;
                            }
                        case eReleaseType.RvR:
                            {
                                //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Release.ReleaseToPortalKeep", diff / 1000), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                                return;
                            }
                        case eReleaseType.House:
                            {
                                //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Release.ReleaseToHouse", diff / 1000), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                                return;
                            }
                    }
                }
            }
            else
            {
                m_releaseType = releaseCommand;
            }

            int relX = 0, relY = 0, relZ = 0;
            ushort relRegion = 0, relHeading = 0;
            switch (m_releaseType)
            {
                case eReleaseType.Duel:
                    {
                        relRegion = (ushort)character.Region;
                        relX = character.Xpos;
                        relY = character.Ypos;
                        relZ = character.Zpos;
                        relHeading = 2048;
                        break;
                    }
                case eReleaseType.House:
                    {
                        relRegion = (ushort)BindHouseRegion;
                        relX = BindHouseXpos;
                        relY = BindHouseYpos;
                        relZ = BindHouseZpos;
                        relHeading = (ushort)BindHouseHeading;
                        break;
                    }

                case eReleaseType.City:
                    {
                        if (Realm == eRealm.Hibernia)
                        {
                            relRegion = 201; // Tir Na Nog
                            relX = 34149;
                            relY = 32063;
                            relZ = 8047;
                            relHeading = 1025;
                        }
                        else if (Realm == eRealm.Midgard)
                        {
                            relRegion = 101; // Jordheim
                            relX = 30094;
                            relY = 27589;
                            relZ = 8763;
                            relHeading = 3468;
                        }
                        else
                        {
                            relRegion = 10; // City of Camelot
                            relX = 36240;
                            relY = 29695;
                            relZ = 7985;
                            relHeading = 4095;
                        }
                        relHeading = 2048;
                        break;
                    }
                case eReleaseType.RvR:
                    {
                        GamePlayer player = Client.Player as GamePlayer;

                        if (player.CurrentRegionID == 27)
                        {
                            relRegion = 27;
                            relX = 342521;
                            relY = 385230;
                            relZ = 5410;
                            relHeading = 1756;
                            break;
                        }

                        foreach (AbstractGameKeep keep in GameServer.KeepManager.GetKeepsOfRegion(CurrentRegionID))
                        {
                            if (keep.IsPortalKeep && keep.OriginalRealm == Realm)
                            {
                                relRegion = keep.CurrentRegion.ID;
                                relX = keep.X;
                                relY = keep.Y;
                                relZ = keep.Z;
                            }
                        }

                        //if we aren't releasing anywhere, release to the border keeps
                        if (relX == 0)
                        {
                            relRegion = CurrentRegion.ID;
                            GameServer.KeepManager.GetBorderKeepLocation(((byte)Realm * 2) / 1, out relX, out relY, out relZ, out relHeading);
                        }
                        break;
                    }
                default:
                    {
                        if (!ServerProperties.Properties.DISABLE_TUTORIAL)
                        {
                            //Tutorial
                            if (BindRegion == 27)
                            {
                                switch (Realm)
                                {
                                    case eRealm.Albion:
                                        {
                                            relRegion = 1; // Cotswold
                                            relX = 8192 + 553251;
                                            relY = 8192 + 502936;
                                            relZ = 2280;
                                            break;
                                        }
                                    case eRealm.Midgard:
                                        {
                                            relRegion = 100; // Mularn
                                            relX = 8192 + 795621;
                                            relY = 8192 + 719590;
                                            relZ = 4680;
                                            break;
                                        }
                                    case eRealm.Hibernia:
                                        {
                                            relRegion = 200; // MagMell
                                            relX = 8192 + 338652;
                                            relY = 8192 + 482335;
                                            relZ = 5200;
                                            break;
                                        }
                                }
                                break;
                            }
                        }
                        switch (CurrentRegionID)
                        {
                            //battlegrounds
                            case 234:
                            case 235:
                            case 236:
                            case 237:
                            case 238:
                            case 239:
                            case 240:
                            case 241:
                            case 242:
                                {
                                    //get the bg cap
                                    byte cap = 50;
                                    foreach (AbstractGameKeep keep in GameServer.KeepManager.GetKeepsOfRegion(CurrentRegionID))
                                    {
                                        if (keep.DBKeep.BaseLevel < cap)
                                        {
                                            cap = keep.DBKeep.BaseLevel;
                                            break;
                                        }
                                    }
                                    //get the portal location
                                    foreach (AbstractGameKeep keep in GameServer.KeepManager.GetKeepsOfRegion(CurrentRegionID))
                                    {
                                        if (keep.DBKeep.BaseLevel > 50 && keep.Realm == Realm)
                                        {
                                            relRegion = (ushort)keep.Region;
                                            relX = keep.X;
                                            relY = keep.Y;
                                            relZ = keep.Z;
                                            break;
                                        }
                                    }
                                    break;
                                }
                            //nf
                            case 163:
                                {
                                    if (BindRegion != 163)
                                    {
                                        relRegion = 163;
                                        switch (Realm)
                                        {
                                            case eRealm.Albion:
                                                {
                                                    GameServer.KeepManager.GetBorderKeepLocation(1, out relX, out relY, out relZ, out relHeading);
                                                    break;
                                                }
                                            case eRealm.Midgard:
                                                {
                                                    GameServer.KeepManager.GetBorderKeepLocation(3, out relX, out relY, out relZ, out relHeading);
                                                    break;
                                                }
                                            case eRealm.Hibernia:
                                                {
                                                    GameServer.KeepManager.GetBorderKeepLocation(5, out relX, out relY, out relZ, out relHeading);
                                                    break;
                                                }
                                        }
                                        break;
                                    }
                                    else
                                    {
                                        relRegion = (ushort)BindRegion;
                                        relX = BindXpos;
                                        relY = BindYpos;
                                        relZ = BindZpos;
                                        relHeading = (ushort)BindHeading;
                                    }
                                    break;
                                }/*
                        //bg45-49
                        case 165:
                        {
                            break;
                        }*/
                            default:
                                {
                                    relRegion = (ushort)BindRegion;
                                    relX = BindXpos;
                                    relY = BindYpos;
                                    relZ = BindZpos;
                                    relHeading = (ushort)BindHeading;
                                    break;
                                }
                        }
                        break;
                    }
            }

            //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Release.YouRelease"), eChatType.CT_YouDied, eChatLoc.CL_SystemWindow);
            //Out.SendCloseTimerWindow();
            if (m_releaseTimer != null)
            {
                m_releaseTimer.Stop();
                m_releaseTimer = null;
            }

            if (Realm != eRealm.None)
            {
                if (Level >= ServerProperties.Properties.PVE_EXP_LOSS_LEVEL && !HCFlag)
                {
                    // actual lost exp, needed for 2nd stage deaths
                    long lostExp = Experience;
                    long lastDeathExpLoss = TempProperties.GetProperty<long>(DEATH_EXP_LOSS_PROPERTY);
                    TempProperties.RemoveProperty(DEATH_EXP_LOSS_PROPERTY);

                    GainExperience(eXPSource.Other, -lastDeathExpLoss);
                    lostExp -= Experience;

                    // raise only the gravestone if xp has to be stored in it
                    if (lostExp > 0)
                    {
                        // find old gravestone of player and remove it
                        if (character.HasGravestone)
                        {
                            Region reg = WorldMgr.GetRegion((ushort)character.GravestoneRegion);
                            if (reg != null)
                            {
                                //GameGravestone oldgrave = reg.FindGraveStone(this);
                                //if (oldgrave != null)
                                //{
                                //    oldgrave.Delete();
                                //}
                            }
                            character.HasGravestone = false;
                        }

                        //GameGravestone gravestone = new GameGravestone(this, lostExp);
                        //gravestone.AddToWorld();
                        //character.GravestoneRegion = gravestone.CurrentRegionID;
                        //character.HasGravestone = true;
                        //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Release.GraveErected"), eChatType.CT_YouDied, eChatLoc.CL_SystemWindow);
                        //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Release.ReturnToPray"), eChatType.CT_YouDied, eChatLoc.CL_SystemWindow);
                    }
                }
            }

            if (Level >= ServerProperties.Properties.PVE_CON_LOSS_LEVEL)
            {
                int deathConLoss = TempProperties.GetProperty<int>(DEATH_CONSTITUTION_LOSS_PROPERTY); // get back constitution lost at death
                if (deathConLoss > 0)
                {
                    TotalConstitutionLostAtDeath += deathConLoss;
                    // Out.SendCharStatsUpdate();
                    // Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Release.LostConstitution"), eChatType.CT_YouDied, eChatLoc.CL_SystemWindow);
                }
            }

            //Update health&sit state first!
            m_isDead = false;
            Health = MaxHealth;
            Endurance = MaxEndurance;
            Mana = MaxMana;
            StartPowerRegeneration();
            StartEnduranceRegeneration();
            LastDeathPvP = false;

            var maxChargeItems = ServerProperties.Properties.MAX_CHARGE_ITEMS;
            /*
            foreach (var item in this.Inventory.EquippedItems)
            {
                //max 2 charges
                if (item.SpellID > 0 && SelfBuffChargeIDs.Contains(item.SpellID) && LoyaltyManager.GetPlayerRealmLoyalty(this).Days > 30)
                {
                    if(ActiveBuffCharges < maxChargeItems)
                        UseItemCharge(item, (int)eUseType.use1);
                    else
                    {
                        Out.SendMessage("You may only use two buff charge effects. This item fails to affect you.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    }
                }

                //max 2 charges
                if (item.SpellID1 > 0 && SelfBuffChargeIDs.Contains(item.SpellID1) && LoyaltyManager.GetPlayerRealmLoyalty(this).Days > 30)
                {
                    if(ActiveBuffCharges < maxChargeItems)
                        UseItemCharge(item, (int)eUseType.use2);
                    else
                    {
                        Out.SendMessage("You may only use two buff charge effects. This item fails to affect you.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    }
                }
            }*/

            //UpdatePlayerStatus();

            Region region = null;
            if ((region = WorldMgr.GetRegion((ushort)BindRegion)) != null && region.GetZone(BindXpos, BindYpos) != null)
            {
                //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Release.SurroundingChange"), eChatType.CT_YouDied, eChatLoc.CL_SystemWindow);
            }
            else
            {
                //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Release.NoValidBindpoint"), eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                Bind(true);
            }

            int oldRegion = CurrentRegionID;

            //Call MoveTo after new GameGravestone(this...
            //or the GraveStone will be located at the player's bindpoint

            MoveTo(relRegion, relX, relY, relZ, relHeading);
            //It is enough if we revive the player on this client only here
            //because for other players the player will be removed in the MoveTo
            //method and added back again (if in view) with full health ... so no
            //revive needed for others...
            //Out.SendPlayerRevive(this);
            //			Out.SendUpdatePlayer();
            //Out.SendUpdatePoints();

            //Set property indicating that we are releasing to another region; used for Released event
            if (oldRegion != CurrentRegionID)
                TempProperties.SetProperty(RELEASING_PROPERTY, true);
            else
            {
                // fire the player revive event
                Notify(GamePlayerEvent.Revive, this);
                Notify(GamePlayerEvent.Released, this);
            }

            TempProperties.RemoveProperty(DEATH_CONSTITUTION_LOSS_PROPERTY);

            //Reset last valide position array to prevent /stuck avec /release
            lock (m_lastUniqueLocations)
            {
                for (int i = 0; i < m_lastUniqueLocations.Length; i++)
                {
                    GameLocation loc = m_lastUniqueLocations[i];
                    loc.X = X;
                    loc.Y = Y;
                    loc.Z = Z;
                    loc.Heading = Heading;
                    loc.RegionID = CurrentRegionID;
                }
            }
        }

        /// <summary>
        /// helper state var for different release phases
        /// </summary>
        private byte m_releasePhase = 0;

        /// <summary>
        /// callback every second to control realtime release
        /// </summary>
        /// <param name="callingTimer"></param>
        /// <returns></returns>
        protected virtual int ReleaseTimerCallback(ECSGameTimer callingTimer)
        {
            if (IsAlive)
                return 0;

            long diffToRelease = GameLoop.GameLoopTime - m_deathTick;
            if (m_automaticRelease && diffToRelease > RELEASE_MINIMUM_WAIT * 1000)
            {
                Release(m_releaseType, true);
                return 0;
            }
            diffToRelease = (RELEASE_TIME * 1000 - diffToRelease) / 1000;
            if (diffToRelease <= 0)
            {
                Release(m_releaseType, true);
                return 0;
            }
            if (m_releasePhase <= 1 && diffToRelease <= 10 && diffToRelease >= 8)
            {
                // Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Release.WillReleaseIn", 10), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                m_releasePhase = 2;
            }
            if (m_releasePhase == 0 && diffToRelease <= 30 && diffToRelease >= 28)
            {
                //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Release.WillReleaseIn", 30), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                m_releasePhase = 1;
            }
            return 1000;
        }

        /// <summary>
        /// The current death type
        /// </summary>
        protected eDeathType m_deathtype;

        /// <summary>
        /// Gets the player's current death type.
        /// </summary>
        public eDeathType DeathType
        {
            get { return m_deathtype; }
            set { m_deathtype = value; }
        }
        /// <summary>
        /// Called when player revive
        /// </summary>
        public virtual void OnRevive(DOLEvent e, object sender, EventArgs args)
        {
            GamePlayer player = (GamePlayer)sender;
            //effectListComponent.CancelAll();
            m_isDead = false;

            bool applyRezSick = true;

            // Used by spells like Perfect Recovery
            if (TempProperties.GetAllProperties().Contains(RESURRECT_REZ_SICK_EFFECTIVENESS) && TempProperties.GetProperty<double>(RESURRECT_REZ_SICK_EFFECTIVENESS) == 0)
            {
                applyRezSick = false;
                TempProperties.RemoveProperty(RESURRECT_REZ_SICK_EFFECTIVENESS);
            }
            else if (player.Level < ServerProperties.Properties.RESS_SICKNESS_LEVEL)
            {
                applyRezSick = false;
            }

            if (player.IsUnderwater && player.CanBreathUnderWater == false)
                player.UpdateWaterBreathState(eWaterBreath.Holding);
            //We need two different sickness spells because RvR sickness is not curable by Healer NPC -Unty
            if (applyRezSick)
                switch (DeathType)
                {
                    case eDeathType.RvR:
                        SpellLine rvrsick = SkillBase.GetSpellLine(GlobalSpellsLines.Realm_Spells);
                        if (rvrsick == null) return;
                        Spell rvrillness = SkillBase.FindSpell(8181, rvrsick);
                        //player.CastSpell(rvrillness, rvrsick);
                        castingComponent.RequestStartCastSpell(rvrillness, rvrsick);
                        break;
                    case eDeathType.PvP: //PvP sickness is the same as PvE sickness - Curable
                    case eDeathType.PvE:
                        SpellLine pvesick = SkillBase.GetSpellLine(GlobalSpellsLines.Realm_Spells);
                        if (pvesick == null) return;
                        Spell pveillness = SkillBase.FindSpell(2435, pvesick);
                        //player.CastSpell(pveillness, pvesick);
                        castingComponent.RequestStartCastSpell(pveillness, pvesick);
                        break;
                }

            GameEventMgr.RemoveHandler(this, GamePlayerEvent.Revive, new DOLEventHandler(OnRevive));
            m_deathtype = eDeathType.None;
            LastDeathPvP = false;
            //UpdatePlayerStatus();
            //Out.SendPlayerRevive(this);
        }

        /// <summary>
        /// Property that saves experience lost on last death
        /// </summary>
        public const string DEATH_EXP_LOSS_PROPERTY = "death_exp_loss";
        /// <summary>
        /// Property that saves condition lost on last death
        /// </summary>
        public const string DEATH_CONSTITUTION_LOSS_PROPERTY = "death_con_loss";
        #endregion

        #region Praying
        /// <summary>
        /// The timer that will be started when the player wants to pray
        /// </summary>
        private ECSGameTimer m_prayAction;

        /// <summary>
        /// Gets the praying-state of this living
        /// </summary>
        public virtual bool IsPraying => m_prayAction?.IsAlive == true;

        /// <summary>
        /// Prays on a gravestone for XP!
        /// </summary>
        public virtual void Pray()
        {
            string cantPrayMessage = null;
            GameGravestone gravestone = TargetObject as GameGravestone;

            if (!IsAlive)
                cantPrayMessage = "GamePlayer.Pray.CantPrayNow";
            //else if (IsRiding)
            //    cantPrayMessage = "GamePlayer.Pray.CantPrayRiding";
            else if (gravestone == null)
                cantPrayMessage = "GamePlayer.Pray.NeedTarget";
            else if (!gravestone.InternalID.Equals(InternalID))
                cantPrayMessage = "GamePlayer.Pray.SelectGrave";
            else if (!IsWithinRadius(gravestone, 2000))
                cantPrayMessage = "GamePlayer.Pray.MustGetCloser";
            else if (IsMoving)
                cantPrayMessage = "GamePlayer.Pray.MustStandingStill";
            else if (IsPraying)
                cantPrayMessage = "GamePlayer.Pray.AlreadyPraying";

            if (cantPrayMessage != null)
            {
                //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, cantPrayMessage), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return;
            }

            m_prayAction = new ECSGameTimer(this, new ECSGameTimer.ECSTimerCallback(_ =>
            {
                if (gravestone.XPValue > 0)
                {
                    // Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Pray.GainBack"), eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                    GainExperience(eXPSource.Praying, gravestone.XPValue);
                }

                gravestone.XPValue = 0;
                gravestone.Delete();
                m_prayAction = null;
                return 0;
            }), 5000);

            Sit(true);
            // Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Pray.Begin"), eChatType.CT_System, eChatLoc.CL_SystemWindow);

            foreach (GamePlayer player in GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
            {
                if (player == null)
                    continue;

                player.Out.SendEmoteAnimation(this, eEmote.Pray);
            }
        }

        /// <summary>
        /// Stop praying; used when player changes target
        /// </summary>
        public void PrayTimerStop()
        {
            if (!IsPraying)
                return;
            m_prayAction.Stop();
            m_prayAction = null;
        }

        #endregion

        #endregion

        #region Name/LastName/GuildName/Model

        /// <summary>
        /// The lastname of this player
        /// (delegate to PlayerCharacter)
        /// </summary>
        public virtual string LastName
        {
            get => DBCharacter != null ? DBCharacter.LastName : string.Empty;
            set
            {
                if (DBCharacter == null)
                    return;

                DBCharacter.LastName = value;

                // Update last name for all players if client is playing.
                if (ObjectState == eObjectState.Active)
                {
                    //Out.SendUpdatePlayer();

                    foreach (GamePlayer player in GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
                    {
                        player.Out.SendObjectRemove(this);
                        //player.Out.SendPlayerCreate(this);
                        player.Out.SendLivingEquipmentUpdate(this);
                    }
                }
            }
        }

        /// <summary>
        /// Gets or sets the guildname of this player
        /// (delegate to PlayerCharacter)
        /// </summary>
        //public override string GuildName
        //{
        //    get
        //    {
        //        if (m_guild == null)
        //            return "";

        //        return m_guild.Name;
        //    }
        //    set
        //    { }
        //}

        /// <summary>
        /// Gets or sets the name of the player
        /// (delegate to PlayerCharacter)
        /// </summary>
        public override string Name
        {
            get => DBCharacter != null ? DBCharacter.Name : base.Name;
            set
            {
                string oldname = base.Name;
                base.Name = value;

                if (DBCharacter != null)
                    DBCharacter.Name = value;

                if (oldname != value)
                {
                    // Update name for all players if client is playing.
                    if (ObjectState == eObjectState.Active)
                    {
                        //  Out.SendUpdatePlayer();

                        //  if (Group != null)
                        //   Out.SendGroupWindowUpdate();

                        foreach (GamePlayer player in GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
                        {
                            player.Out.SendObjectRemove(this);
                            //player.Out.SendPlayerCreate(this);
                            player.Out.SendLivingEquipmentUpdate(this);
                        }
                    }
                }
            }
        }

        #endregion

        #region Stats

        /// <summary>
        /// Gets/sets the player efficacy percent
        /// (delegate to PlayerCharacter)
        /// </summary>
        public virtual int TotalConstitutionLostAtDeath
        {
            get { return DBCharacter != null ? DBCharacter.ConLostAtDeath : 0; }
            set { if (DBCharacter != null) DBCharacter.ConLostAtDeath = value; }
        }

        /// <summary>
        /// Change a stat value
        /// (delegate to PlayerCharacter)
        /// </summary>
        /// <param name="stat">The stat to change</param>
        /// <param name="val">The new value</param>
        public override void ChangeBaseStat(eStat stat, short val)
        {
            int oldstat = GetBaseStat(stat);
            base.ChangeBaseStat(stat, val);
            short newstat = (short)GetBaseStat(stat);
            //DbCoreCharacter character = DBCharacter; // to call it only once, if in future there will be some special code to get the character
            // Graveen: always positive and not null. This allows /player stats to substract values safely
            if (newstat < 1) newstat = 1;
            if (oldstat != newstat)
            {
                switch (stat)
                {
                    case eStat.STR: Strength = newstat; break;
                    case eStat.DEX: Dexterity = newstat; break;
                    case eStat.CON: Constitution = newstat; break;
                    case eStat.QUI: Quickness = newstat; break;
                    case eStat.INT: Intelligence = newstat; break;
                    case eStat.PIE: Piety = newstat; break;
                    case eStat.EMP: Empathy = newstat; break;
                    case eStat.CHR: Charisma = newstat; break;
                }
            }
        }

        /// <summary>
        /// Gets player's constitution
        /// </summary>
        public override short Constitution
        {
            get { return (short)GetModified(eProperty.Constitution); }
            set { m_charStat[eStat.CON - eStat._First] = value; }
        }

        /// <summary>
        /// Gets player's dexterity
        /// </summary>
        public override short Dexterity
        {
            get { return (short)GetModified(eProperty.Dexterity); }
            set { m_charStat[eStat.DEX - eStat._First] = value; }
        }

        /// <summary>
        /// Gets player's strength
        /// </summary>
        public override short Strength
        {
            get { return (short)GetModified(eProperty.Strength); }
            set { m_charStat[eStat.STR - eStat._First] = value; }
        }

        /// <summary>
        /// Gets player's quickness
        /// </summary>
        public override short Quickness
        {
            get { return (short)GetModified(eProperty.Quickness); }
            set { m_charStat[eStat.QUI - eStat._First] = value; }
        }

        /// <summary>
        /// Gets player's intelligence
        /// </summary>
        public override short Intelligence
        {
            get { return (short)GetModified(eProperty.Intelligence); }
            set { m_charStat[eStat.INT - eStat._First] = value; }
        }

        /// <summary>
        /// Gets player's piety
        /// </summary>
        public override short Piety
        {
            get { return (short)GetModified(eProperty.Piety); }
            set { m_charStat[eStat.PIE - eStat._First] = value; }
        }

        /// <summary>
        /// Gets player's empathy
        /// </summary>
        public override short Empathy
        {
            get { return (short)GetModified(eProperty.Empathy); }
            set { m_charStat[eStat.EMP - eStat._First] = value; }
        }

        /// <summary>
        /// Gets player's charisma
        /// </summary>
        public override short Charisma
        {
            get { return (short)GetModified(eProperty.Charisma); }
            set { m_charStat[eStat.CHR - eStat._First] = value; }
        }

        protected IPlayerStatistics m_statistics = null;

        /// <summary>
        /// Get the statistics for this player
        /// </summary>
        public virtual IPlayerStatistics Statistics
        {
            get { return m_statistics; }
        }

        #endregion

        #region Health/Mana/Endurance/Regeneration

        /// <summary>
        /// Starts the health regeneration.
        /// Overriden. No lazy timers for GamePlayers.
        /// </summary>
        public override void StartHealthRegeneration()
        {
            if (!IsAlive || ObjectState != eObjectState.Active)
                return;

            if (m_healthRegenerationTimer is { IsAlive: true })
                return;

            if (m_healthRegenerationTimer == null)
            {
                m_healthRegenerationTimer = new ECSGameTimer(this);
                m_healthRegenerationTimer.Callback = new ECSGameTimer.ECSTimerCallback(HealthRegenerationTimerCallback);
            }
            else if (m_healthRegenerationTimer.IsAlive)
            {
                return;
            }

            m_healthRegenerationTimer.Start(m_healthRegenerationPeriod);
        }
        /// <summary>
        /// Starts the power regeneration.
        /// Overriden. No lazy timers for GamePlayers.
        /// </summary>
        public override void StartPowerRegeneration()
        {
            if (ObjectState != eObjectState.Active) return;
            if (m_powerRegenerationTimer is { IsAlive: true }) return;
            if (m_powerRegenerationTimer == null)
            {
                m_powerRegenerationTimer = new ECSGameTimer(this);
                m_powerRegenerationTimer.Callback = new ECSGameTimer.ECSTimerCallback(PowerRegenerationTimerCallback);
            }

            PowerRegenStackingBonus = 0;
            m_powerRegenerationTimer.Start(m_powerRegenerationPeriod);
        }
        /// <summary>
        /// Starts the endurance regeneration.
        /// Overriden. No lazy timers for GamePlayers.
        /// </summary>
        public override void StartEnduranceRegeneration()
        {
            if (ObjectState != eObjectState.Active)
                return;

            if (m_enduRegenerationTimer is { IsAlive: true }) return;
            if (m_enduRegenerationTimer == null)
            {
                m_enduRegenerationTimer = new ECSGameTimer(this);
                m_enduRegenerationTimer.Callback =
                    new ECSGameTimer.ECSTimerCallback(EnduranceRegenerationTimerCallback);
            }

            m_enduRegenerationTimer.Start(m_enduranceRegenerationPeriod);
        }
        /// <summary>
        /// Stop the health regeneration.
        /// Overriden. No lazy timers for GamePlayers.
        /// </summary>
        public override void StopHealthRegeneration()
        {
            if (m_healthRegenerationTimer == null)
                return;

            m_healthRegenerationTimer.Stop();
        }
        /// <summary>
        /// Stop the power regeneration.
        /// Overriden. No lazy timers for GamePlayers.
        /// </summary>
        public override void StopPowerRegeneration()
        {
            PowerRegenStackingBonus = 0;
            if (m_powerRegenerationTimer == null)
                return;

            m_powerRegenerationTimer.Stop();
        }
        /// <summary>
        /// Stop the endurance regeneration.
        /// Overriden. No lazy timers for GamePlayers.
        /// </summary>
        public override void StopEnduranceRegeneration()
        {
            if (m_enduRegenerationTimer == null)
                return;

            m_enduRegenerationTimer.Stop();
        }

        /// <summary>
        /// Override HealthRegenTimer because if we are not connected anymore
        /// we DON'T regenerate health, even if we are not garbage collected yet!
        /// </summary>
        /// <param name="callingTimer">the timer</param>
        /// <returns>the new time</returns>
        protected override int HealthRegenerationTimerCallback(ECSGameTimer callingTimer)
        {
            // adjust timer based on Live testing of player
            if (Health < MaxHealth)
            {
                ChangeHealth(this, eHealthChangeType.Regenerate, GetModified(eProperty.HealthRegenerationRate));
            }

            #region PVP DAMAGE

            if (DamageRvRMemory > 0)
                DamageRvRMemory -= (long)Math.Max(GetModified(eProperty.HealthRegenerationRate), 0);

            #endregion PVP DAMAGE

            //If we are fully healed, we stop the timer
            if (Health >= MaxHealth)
            {

                #region PVP DAMAGE

                // Fully Regenerated, Set DamageRvRMemory to 0
                if (DamageRvRMemory > 0)
                    DamageRvRMemory = 0;

                #endregion PVP DAMAGE

                //We clean all damagedealers if we are fully healed,
                //no special XP calculations need to be done
                lock (m_xpGainers.SyncRoot)
                {
                    m_xpGainers.Clear();
                }

                callingTimer.Stop();
            }

            if (InCombat)
            {
                // in combat each tic is aprox 6 seconds - tolakram
                return HealthRegenerationPeriod * 2;
            }

            if (IsSitting)
            {
                return HealthRegenerationPeriod / 2;
            }

            //Heal at standard rate
            return HealthRegenerationPeriod;
        }

        public int PowerRegenStackingBonus = 0;

        /// <summary>
        /// Override PowerRegenTimer because if we are not connected anymore
        /// we DON'T regenerate mana, even if we are not garbage collected yet!
        /// </summary>
        /// <param name="selfRegenerationTimer">the timer</param>
        /// <returns>the new time</returns>
        protected override int PowerRegenerationTimerCallback(ECSGameTimer selfRegenerationTimer)
        {
            //if (Client.ClientState != GameClient.eClientState.Playing)
            //    return PowerRegenerationPeriod;

            if (IsSitting)
            {
                if (PowerRegenStackingBonus < 3)
                    PowerRegenStackingBonus++;
            }
            else
                PowerRegenStackingBonus = 0;

            int interval = base.PowerRegenerationTimerCallback(selfRegenerationTimer);
            return interval;
        }

        /// <summary>
        /// Override EnduranceRegenTimer because if we are not connected anymore
        /// we DON'T regenerate endurance, even if we are not garbage collected yet!
        /// </summary>
        /// <param name="selfRegenerationTimer">the timer</param>
        /// <returns>the new time</returns>
        protected override int EnduranceRegenerationTimerCallback(ECSGameTimer selfRegenerationTimer)
        {
            //if (Client.ClientState != GameClient.eClientState.Playing)
            //    return EnduranceRegenerationPeriod;

            LastEnduTick = GameLoop.GameLoopTime;

            bool sprinting = IsSprinting;

            if (Endurance < MaxEndurance || sprinting)
            {
                int regen = GetModified(eProperty.EnduranceRegenerationRate);  //default is 1
                int endchant = GetModified(eProperty.FatigueConsumption);      //Pull chant/buff value
                var charge = EffectListService.GetEffectOnTarget(this, eEffect.Charge);

                Regen = regen;
                Endchant = endchant;

                int longwind = 5;
                if (sprinting && IsMoving)
                {
                    if (charge is null)
                    {
                        #region Calculation : AtlasOF_LongWind
                        // --- [START] --- AtlasOF_EtherealBond --------------------------------------------------------
                        AtlasOF_LongWindAbility raLongWind = GetAbility<AtlasOF_LongWindAbility>();
                        if (raLongWind != null)
                        {
                            longwind -= (raLongWind.GetAmountForLevel(CalculateSkillLevel(raLongWind)) * 5 / 100);
                        }
                        // --- [START] --- AtlasOF_EtherealBond --------------------------------------------------------
                        #endregion

                        regen -= longwind;

                        if (endchant > 1) regen = (int)Math.Ceiling(regen * endchant * 0.01);

                        if (Endurance + regen > MaxEndurance - longwind)
                        {
                            regen -= (Endurance + regen) - (MaxEndurance - longwind);
                        }
                    }
                }

                RegenRateAtChange = regen;

                if (regen != 0)
                {
                    ChangeEndurance(this, eEnduranceChangeType.Regenerate, regen);
                }
            }

            if (!sprinting)
            {
                if (Endurance >= MaxEndurance)
                    selfRegenerationTimer.Stop();
            }
            else
            {
                long lastmove = TempProperties.GetProperty<long>(PlayerPositionUpdateHandler.LASTMOVEMENTTICK);
                if ((lastmove > 0 && lastmove + 5000 < CurrentRegion.Time) //cancel sprint after 5sec without moving?
                    || Endurance - 5 <= 0)
                    Sprint(false);
            }

            var rate = EnduranceRegenerationPeriod;

            if (IsSitting)
                rate /= 2;

            return rate;
        }

        protected bool m_isDead = false;
        /// <summary>
        /// returns if this living is alive
        /// </summary>
        public override bool IsAlive
        {
            //get { return Health > 0; }
            get { return !m_isDead; }
        }

        /// <summary>
        /// Gets/sets the object health
        /// </summary>
        public override int Health
        {
            get { return base.Health; }
            set
            {
                value = Math.Clamp(value, 0, MaxHealth);
                //If it is already set, don't do anything
                if (Health == value)
                {
                    base.Health = value; //needed to start regeneration
                    return;
                }

                int oldPercent = HealthPercent;
                base.Health = value;

                if (m_health == 0)
                    m_isDead = true;

                if (oldPercent != HealthPercent)
                {
                    if (Group != null)
                        Group.UpdateMember(this, false, false);
                    //UpdatePlayerStatus();
                }
            }
        }

        /// <summary>
        /// Calculates the maximum health for a specific playerlevel and constitution
        /// </summary>
        /// <param name="level">The level of the player</param>
        /// <param name="constitution">The constitution of the player</param>
        /// <returns></returns>
        public virtual int CalculateMaxHealth(int level, int constitution)
        {
            constitution -= 50;
            if (constitution < 0) constitution *= 2;

            // hp1 : from level
            // hp2 : from constitution
            // hp3 : from champions level
            // hp4 : from artifacts such Spear of Kings charge
            int hp1 = CharacterClass.BaseHP * level;
            int hp2 = hp1 * constitution / 10000;
            int hp3 = 0;
            //if (ChampionLevel >= 1)
            //hp3 = ServerProperties.Properties.HPS_PER_CHAMPIONLEVEL * ChampionLevel;
            double hp4 = 20 + hp1 / 50 + hp2 + hp3;
            if (GetModified(eProperty.ExtraHP) > 0)
                hp4 += Math.Round(hp4 * (double)GetModified(eProperty.ExtraHP) / 100);

            return Math.Max(1, (int)hp4);
        }

        public override byte HealthPercentGroupWindow
        {
            get
            {
                return CharacterClass.HealthPercentGroupWindow;
            }
        }

        /// <summary>
        /// Calculate max mana for this player based on level and mana stat level
        /// </summary>
        /// <param name="level"></param>
        /// <param name="manaStat"></param>
        /// <returns></returns>
        public virtual int CalculateMaxMana(int level, int manaStat)
        {
            int maxpower = 0;

            //Special handling for Vampiirs:
            /* There is no stat that affects the Vampiir's power pool or the damage done by its power based spells.
             * The Vampiir is not a focus based class like, say, an Enchanter.
             * The Vampiir is a lot more cut and dried than the typical casting class.
             * EDIT, 12/13/04 - I was told today that this answer is not entirely accurate.
             * While there is no stat that affects the damage dealt (in the way that intelligence or piety affects how much damage a more traditional caster can do),
             * the Vampiir's power pool capacity is intended to be increased as the Vampiir's strength increases.
             *
             * This means that strength ONLY affects a Vampiir's mana pool
             * 
             * http://www.camelotherald.com/more/1913.shtml
             * Strength affects the amount of damage done by spells in all of the Vampiir's spell lines.
             * The amount of said affecting was recently increased slightly (fixing a bug), and that minor increase will go live in 1.74 next week.
             * 
             * Strength ALSO affects the size of the power pool for a Vampiir sort of.
             * Your INNATE strength (the number of attribute points your character has for strength) has no effect at all.
             * Extra points added through ITEMS, however, does increase the size of your power pool.

             */
            if (CharacterClass.ManaStat != eStat.UNDEFINED || CharacterClass.ID == (int)eCharacterClass.Vampiir)
            {
                maxpower = Math.Max(5, (level * 5) + (manaStat - 50));
            }
            //else if (CharacterClass.ManaStat == eStat.UNDEFINED && Champion && ChampionLevel > 0)
            //{
            //    maxpower = 100; // This is a guess, need feedback
            //}

            #region Calculation : AtlasOF_EtheralBond
            // --- [START] --- AtlasOF_EtherealBond --------------------------------------------------------
            AtlasOF_EtherealBondAbility raEtherealBond = GetAbility<AtlasOF_EtherealBondAbility>();
            if (raEtherealBond != null)
            {
                if (raEtherealBond.Level > 0)
                {
                    maxpower += (maxpower * raEtherealBond.Level) / 100;
                }
            }
            // --- [ END ] --- AtlasOF_EtherealBond --------------------------------------------------------
            #endregion

            if (maxpower < 0)
                maxpower = 0;

            return maxpower;
        }


        /// <summary>
        /// Gets/sets the object mana
        /// </summary>
        public override int Mana
        {
            get { return m_mana; }
            set
            {
                int maxMana = MaxMana;
                int mana;
                mana = Math.Min(value, maxMana);
                mana = Math.Max(mana, 0);

                if (IsAlive && (mana < maxMana))
                    StartPowerRegeneration();

                int oldPercent = ManaPercent;
                m_mana = mana;

                if (oldPercent != ManaPercent)
                {
                    if (Group != null)
                        Group.UpdateMember(this, false, false);
                }
            }
        }

        /// <summary>
        /// Gets/sets the object max mana
        /// </summary>
        public override int MaxMana
        {
            get { return GetModified(eProperty.MaxMana); }
        }

        /// <summary>
        /// Gets/sets the object endurance
        /// </summary>
        public override int Endurance
        {
            get { return m_endurance; }
            set
            {
                int endurance;
                endurance = Math.Min(value, MaxEndurance);
                endurance = Math.Max(endurance, 0);

                if (IsAlive && endurance < MaxEndurance)
                    StartEnduranceRegeneration();

                int oldPercent = EndurancePercent;
                m_endurance = endurance;

                if (oldPercent != EndurancePercent)
                {
                    if (Group != null)
                        Group.UpdateMember(this, false, false);
                }
            }
        }

        public override int MaxEndurance
        {
            get { return GetModified(eProperty.Fatigue); }
            set { base.MaxEndurance = value; }
        }

        /// <summary>
        /// Gets the concentration left
        /// </summary>
        public override int Concentration
        {
            get { return MaxConcentration - UsedConcentration; }
        }

        /// <summary>
        /// Gets the maximum concentration for this player
        /// </summary>
        public override int MaxConcentration
        {
            get { return GetModified(eProperty.MaxConcentration); }
        }

        #region Calculate Fall Damage

        /// <summary>
        /// Calculates fall damage taking fall damage reduction bonuses into account
        /// </summary>
        /// <returns></returns>
        public virtual double CalcFallDamage(int fallDamagePercent)
        {
            if (fallDamagePercent <= 0)
                return 0;

            int safeFallLevel = GetAbilityLevel("Safe Fall");
            int mythSafeFall = GetModified(eProperty.MythicalSafeFall);

            if (mythSafeFall > 0 & mythSafeFall < fallDamagePercent)
            {
                Client.Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "PlayerPositionUpdateHandler.MythSafeFall"), eChatType.CT_Damaged, eChatLoc.CL_SystemWindow);
                fallDamagePercent = mythSafeFall;
            }
            // if (safeFallLevel > 0 & mythSafeFall == 0)
            //  Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "PlayerPositionUpdateHandler.SafeFall"), eChatType.CT_Damaged, eChatLoc.CL_SystemWindow);

            Endurance -= MaxEndurance * fallDamagePercent / 100;
            double damage = (0.01 * fallDamagePercent * (MaxHealth - 1));

            // [Freya] Nidel: CloudSong falling damage reduction
            GameSpellEffect cloudSongFall = SpellHandler.FindEffectOnTarget(this, "CloudsongFall");
            if (cloudSongFall != null)
                damage -= (damage * cloudSongFall.Spell.Value) * 0.01;

            //Mattress: SafeFall property for Mythirians, the value of the MythicalSafeFall property represents the percent damage taken in a fall.
            if (mythSafeFall != 0 && damage > mythSafeFall)
                damage = ((MaxHealth - 1) * (mythSafeFall * 0.01));

            // Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "PlayerPositionUpdateHandler.FallingDamage"), eChatType.CT_Damaged, eChatLoc.CL_SystemWindow);
            // Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "PlayerPositionUpdateHandler.FallPercent", fallDamagePercent), eChatType.CT_Damaged, eChatLoc.CL_SystemWindow);
            // Out.SendMessage("You lose endurance.", eChatType.CT_Damaged, eChatLoc.CL_SystemWindow);
            TakeDamage(null, eDamageType.Falling, (int)damage, 0);

            //Update the player's health to all other players around
            //foreach (GamePlayer player in GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
            //Out.SendCombatAnimation(null, Client.Player, 0, 0, 0, 0, 0, HealthPercent);

            return damage;
        }

        #endregion

        #endregion

        #region Class/Race

        /// <summary>
        /// Gets/sets the player's race name
        /// </summary>
        public virtual string RaceName
        {
            //get { return this.RaceToTranslatedName(Race, Gender); }
            //get { return string.Format("!{0} - {1}!", ((eRace)Race).ToString("F"), Gender.ToString("F")); }
            get { return ((eRace)Race).ToString("F"); }
        }

        ///// <summary>
        ///// Gets or sets this player's race id
        ///// (delegate to DBCharacter)
        ///// </summary>
        //public override short Race
        //{
        //    get { return m_race; }
        //    set { m_race = value; }
        //}

        //private short m_race;

        /// <summary>
        /// Players class
        /// </summary>
        protected ICharacterClass m_characterClass;

        /// <summary>
        /// Gets the player's character class
        /// </summary>
        public virtual ICharacterClass CharacterClass
        {
            get { return m_characterClass; }
        }

        /// <summary>
        /// Set the character class to a specific one
        /// </summary>
        /// <param name="id">id of the character class</param>
        /// <returns>success</returns>
        public virtual bool SetCharacterClass(int id)
        {
            ICharacterClass cl = ScriptMgr.FindCharacterClass(id);

            if (cl == null)
            {
                if (log.IsErrorEnabled)
                    log.ErrorFormat("No CharacterClass with ID {0} found", id);
                return false;
            }

            m_characterClass = cl;
            //m_characterClass.Init(this);

            //DBCharacter.Class = m_characterClass.ID;

            if (Group != null)
            {
                Group.UpdateMember(this, false, true);
            }
            return true;
        }

        /// <summary>
        /// Hold all player face custom attibutes
        /// </summary>
        protected byte[] m_customFaceAttributes = new byte[(int)eCharFacePart._Last + 1];

        /// <summary>
        /// Get the character face attribute you want
        /// </summary>
        /// <param name="part">face part</param>
        /// <returns>attribute</returns>
        public byte GetFaceAttribute(eCharFacePart part)
        {
            return m_customFaceAttributes[(int)part];
        }

        #endregion

        #region Spells/Skills/Abilities/Effects

        /// <summary>
        /// Holds the player choosen list of Realm Abilities.
        /// </summary>
        protected readonly ReaderWriterList<RealmAbility> m_realmAbilities = new ReaderWriterList<RealmAbility>();

        /// <summary>
        /// Holds the player specializable skills and style lines
        /// (KeyName -> Specialization)
        /// </summary>
        protected readonly Dictionary<string, Specialization> m_specialization = new Dictionary<string, Specialization>();

        /// <summary>
        /// Holds the Spell lines the player can use
        /// </summary>
        protected readonly List<SpellLine> m_spellLines = new List<SpellLine>();

        /// <summary>
        /// Object to use when locking the SpellLines list
        /// </summary>
        protected readonly Object lockSpellLinesList = new Object();

        ///// <summary>
        ///// Holds all styles of the player
        ///// </summary>
        //protected readonly Dictionary<int, Style> m_styles = new Dictionary<int, Style>();

        ///// <summary>
        ///// Used to lock the style list
        ///// </summary>
        //protected readonly Object lockStyleList = new Object();

        /// <summary>
        /// Temporary Stats Boni
        /// </summary>
        protected readonly int[] m_statBonus = new int[8];

        /// <summary>
        /// Temporary Stats Boni in percent
        /// </summary>
        protected readonly int[] m_statBonusPercent = new int[8];

        /// <summary>
        /// Gets/Sets amount of full skill respecs
        /// (delegate to PlayerCharacter)
        /// </summary>
        public virtual int RespecAmountAllSkill
        {
            get { return DBCharacter != null ? DBCharacter.RespecAmountAllSkill : 0; }
            set { if (DBCharacter != null) DBCharacter.RespecAmountAllSkill = value; }
        }

        /// <summary>
        /// Gets/Sets amount of single-line respecs
        /// (delegate to PlayerCharacter)
        /// </summary>
        public virtual int RespecAmountSingleSkill
        {
            get { return DBCharacter != null ? DBCharacter.RespecAmountSingleSkill : 0; }
            set { if (DBCharacter != null) DBCharacter.RespecAmountSingleSkill = value; }
        }

        /// <summary>
        /// Gets/Sets amount of realm skill respecs
        /// (delegate to PlayerCharacter)
        /// </summary>
        public virtual int RespecAmountRealmSkill
        {
            get { return DBCharacter != null ? DBCharacter.RespecAmountRealmSkill : 0; }
            set { if (DBCharacter != null) DBCharacter.RespecAmountRealmSkill = value; }
        }

        /// <summary>
        /// Gets/Sets amount of DOL respecs
        /// (delegate to PlayerCharacter)
        /// </summary>
        public virtual int RespecAmountDOL
        {
            get { return DBCharacter != null ? DBCharacter.RespecAmountDOL : 0; }
            set { if (DBCharacter != null) DBCharacter.RespecAmountDOL = value; }
        }

        /// <summary>
        /// Gets/Sets level respec usage flag
        /// (delegate to PlayerCharacter)
        /// </summary>
        public virtual bool IsLevelRespecUsed
        {
            get { return DBCharacter != null ? DBCharacter.IsLevelRespecUsed : true; }
            set { if (DBCharacter != null) DBCharacter.IsLevelRespecUsed = value; }
        }


        protected static readonly int[] m_numRespecsCanBuyOnLevel =
        {
            1,1,1,1,1, //1-5
            2,2,2,2,2,2,2, //6-12
            3,3,3,3, //13-16
            4,4,4,4,4,4, //17-22
            5,5,5,5,5, //23-27
            6,6,6,6,6,6, //28-33
            7,7,7,7,7, //34-38
            8,8,8,8,8,8, //39-44
            9,9,9,9,9, //45-49
            10 //50
        };


        /// <summary>
        /// Can this player buy a respec?
        /// </summary>
        public virtual bool CanBuyRespec
        {
            get
            {
                return (RespecBought < m_numRespecsCanBuyOnLevel[Level - 1]);
            }
        }

        /// <summary>
        /// Gets/Sets amount of bought respecs
        /// (delegate to PlayerCharacter)
        /// </summary>
        public virtual int RespecBought
        {
            get { return DBCharacter != null ? DBCharacter.RespecBought : 0; }
            set { if (DBCharacter != null) DBCharacter.RespecBought = value; }
        }


        protected static readonly int[] m_respecCost =
        {
            1,2,3, //13
            2,5,9, //14
            3,9,17, //15
            6,16,30, //16
            10,26,48,75, //17
            16,40,72,112, //18
            22,56,102,159, //19
            31,78,140,218, //20
            41,103,187,291, //21
            54,135,243,378, //22
            68,171,308,480,652, //23
            85,214,385,600,814, //24
            105,263,474,738,1001, //25
            128,320,576,896,1216, //26
            153,383,690,1074,1458, //27
            182,455,820,1275,1731,2278, //28
            214,535,964,1500,2036,2679, //29
            250,625,1125,1750,2375,3125, //30
            289,723,1302,2025,2749,3617, //31
            332,831,1497,2329,3161,4159, //32
            380,950,1710,2661,3612,4752, //33
            432,1080,1944,3024,4104,5400,6696, //34
            488,1220,2197,3417,4638,6103,7568, //35
            549,1373,2471,3844,5217,6865,8513, //36
            615,1537,2767,4305,5843,7688,9533, //37
            686,1715,3087,4802,6517,8575,10633, //38
            762,1905,3429,5335,7240,9526,11813,14099, //39
            843,2109,3796,5906,8015,10546,13078,15609, //40
            930,2327,4189,6516,8844,11637,14430,17222, //41
            1024,2560,4608,7168,9728,1280,15872,18944, //42
            1123,2807,5053,7861,10668,14037,17406,20776, //43
            1228,3070,5527,8597,11668,15353,19037,22722, //44
            1339,3349,6029,9378,12725,16748,20767,24787,28806, //45
            1458,3645,6561,10206,13851,18225,22599,26973,31347, //46
            1582,3957,7123,11080,15037,19786,24535,29283,34032, //47
            1714,4286,7716,12003,16290,21434,26578,31722,36867, //48
            1853,4634,8341,12976,17610,23171,28732,34293,39854, //49
            2000,5000,9000,14000,19000,25000,31000,37000,43000,50000 //50
        };


        /// <summary>
        /// How much does this player have to pay for a respec?
        /// </summary>
        public virtual long RespecCost
        {
            get
            {
                if (Level <= 12) //1-12
                    return m_respecCost[0];

                if (CanBuyRespec)
                {
                    int t = 0;
                    for (int i = 13; i < Level; i++)
                    {
                        t += m_numRespecsCanBuyOnLevel[i - 1];
                    }

                    return m_respecCost[t + RespecBought];
                }

                return -1;
            }
        }

        /// <summary>
        /// give player a new Specialization or improve existing one
        /// </summary>
        /// <param name="skill"></param>
        public void AddSpecialization(Specialization skill)
        {
            AddSpecialization(skill, true);
        }

        /// <summary>
        /// give player a new Specialization or improve existing one
        /// </summary>
        /// <param name="skill"></param>
        protected virtual void AddSpecialization(Specialization skill, bool notify)
        {
            if (skill == null)
                return;

            lock (((ICollection)m_specialization).SyncRoot)
            {
                // search for existing key
                if (!m_specialization.ContainsKey(skill.KeyName))
                {
                    // Adding
                    m_specialization.Add(skill.KeyName, skill);
                }
                else
                {
                    // Updating
                    m_specialization[skill.KeyName].Level = skill.Level;
                }
            }
        }

        /// <summary>
        /// Removes the existing specialization from the player
        /// </summary>
        /// <param name="specKeyName">The spec keyname to remove</param>
        /// <returns>true if removed</returns>
        public virtual bool RemoveSpecialization(string specKeyName)
        {
            Specialization playerSpec = null;

            lock (((ICollection)m_specialization).SyncRoot)
            {
                if (!m_specialization.TryGetValue(specKeyName, out playerSpec))
                    return false;

                m_specialization.Remove(specKeyName);
            }

            return true;
        }

        /// <summary>
        /// Removes the existing spellline from the player, the line instance should be called with GamePlayer.GetSpellLine ONLY and NEVER SkillBase.GetSpellLine!!!!!
        /// </summary>
        /// <param name="line">The spell line to remove</param>
        /// <returns>true if removed</returns>
        protected virtual bool RemoveSpellLine(SpellLine line)
        {
            lock (lockSpellLinesList)
            {
                if (!m_spellLines.Contains(line))
                {
                    return false;
                }

                m_spellLines.Remove(line);
            }

            return true;
        }

        /// <summary>
        /// Removes the existing specialization from the player
        /// </summary>
        /// <param name="lineKeyName">The spell line keyname to remove</param>
        /// <returns>true if removed</returns>
        public virtual bool RemoveSpellLine(string lineKeyName)
        {
            SpellLine line = GetSpellLine(lineKeyName);
            if (line == null)
                return false;

            return RemoveSpellLine(line);
        }

        /// <summary>
        /// Reset this player to level 1, respec all skills, remove all spec points, and reset stats
        /// </summary>
        public virtual void Reset()
        {
            byte originalLevel = Level;
            Level = 1;
            Experience = 0;
            RespecAllLines();

            if (Level < originalLevel && originalLevel > 5)
            {
                for (int i = 6; i <= originalLevel; i++)
                {
                    if (CharacterClass.PrimaryStat != eStat.UNDEFINED)
                    {
                        ChangeBaseStat(CharacterClass.PrimaryStat, -1);
                    }
                    if (CharacterClass.SecondaryStat != eStat.UNDEFINED && ((i - 6) % 2 == 0))
                    {
                        ChangeBaseStat(CharacterClass.SecondaryStat, -1);
                    }
                    if (CharacterClass.TertiaryStat != eStat.UNDEFINED && ((i - 6) % 3 == 0))
                    {
                        ChangeBaseStat(CharacterClass.TertiaryStat, -1);
                    }
                }
            }

            //CharacterClass.OnLevelUp(this, originalLevel);
        }

        /// <summary>
        /// Load this player Classes Specialization.
        /// </summary>
        public virtual void LoadClassSpecializations(bool sendMessages)
        {
            // Get this Attached Class Specialization from SkillBase.
            IDictionary<Specialization, int> careers = SkillBase.GetSpecializationCareer(CharacterClass.ID);

            // Remove All Trainable Specialization or "Career Spec" that aren't managed by This Data Career anymore
            var speclist = GetSpecList();
            var careerslist = careers.Keys.Select(k => k.KeyName.ToLower());
            foreach (var spec in speclist.Where(sp => sp.Trainable || !sp.AllowSave))
            {
                if (!careerslist.Contains(spec.KeyName.ToLower()))
                    RemoveSpecialization(spec.KeyName);
            }

            // sort ML Spec depending on ML Line
            byte mlindex = 0;
            foreach (KeyValuePair<Specialization, int> constraint in careers)
            {
                //if (constraint.Key is IMasterLevelsSpecialization)
                //{
                //    if (mlindex != MLLine)
                //    {
                //        if (HasSpecialization(constraint.Key.KeyName))
                //            RemoveSpecialization(constraint.Key.KeyName);

                //        mlindex++;
                //        continue;
                //    }

                //    mlindex++;

                //    if (!MLGranted || MLLevel < 1)
                //    {
                //        continue;
                //    }
                //}

                // load if the spec doesn't exists
                if (Level >= constraint.Value)
                {
                    if (!HasSpecialization(constraint.Key.KeyName))
                        AddSpecialization(constraint.Key, sendMessages);
                }
                else
                {
                    if (HasSpecialization(constraint.Key.KeyName))
                        RemoveSpecialization(constraint.Key.KeyName);
                }
            }
        }

        /// <summary>
        /// Verify this player has the correct number of spec points for the players level
        /// </summary>
        public virtual int VerifySpecPoints()
        {
            // calc normal spec points for the level & classe
            int allpoints = -1;
            for (int i = 1; i <= Level; i++)
            {
                if (i <= 5) allpoints += i; //start levels
                if (i > 5) allpoints += CharacterClass.SpecPointsMultiplier * i / 10; //normal levels
                if (i > 40) allpoints += CharacterClass.SpecPointsMultiplier * (i - 1) / 20; //half levels
            }
            if (IsLevelSecondStage && Level != MaxLevel)
                allpoints += CharacterClass.SpecPointsMultiplier * Level / 20; // add current half level

            // calc spec points player have (autotrain is not anymore processed here - 1.87 livelike)
            int usedpoints = 0;
            foreach (Specialization spec in GetSpecList().Where(e => e.Trainable))
            {
                usedpoints += (spec.Level * (spec.Level + 1) - 2) / 2;
                usedpoints -= GetAutoTrainPoints(spec, 0);
            }

            allpoints -= usedpoints;

            // check if correct, if not respec. Not applicable to GMs
            if (allpoints < 0)
            {
                if (Client.Account.PrivLevel == 1)
                {
                    log.WarnFormat("Spec points total for player {0} incorrect: {1} instead of {2}.", Name, usedpoints, allpoints + usedpoints);
                    RespecAllLines();
                    return allpoints + usedpoints;
                }
            }

            return allpoints;
        }

        public virtual bool RespecAll()
        {
            if (RespecAllLines())
            {
                // Wipe skills and styles.
                RespecAmountAllSkill--; // Decriment players respecs available.
                if (Level == 5)
                    IsLevelRespecUsed = true;

                return true;
            }

            return false;
        }

        public virtual bool RespecDOL()
        {
            if (RespecAllLines()) // Wipe skills and styles.
            {
                RespecAmountDOL--; // Decriment players respecs available.
                return true;
            }

            return false;
        }

        public virtual int RespecSingle(Specialization specLine)
        {
            int specPoints = RespecSingleLine(specLine); // Wipe skills and styles.
            if (!ServerProperties.Properties.FREE_RESPEC)
                RespecAmountSingleSkill--; // Decriment players respecs available.
            if (Level == 20 || Level == 40)
            {
                IsLevelRespecUsed = true;
            }
            return specPoints;
        }

        public virtual bool RespecRealm(bool useRespecPoint = true)
        {
            bool any = m_realmAbilities.Count > 0;

            foreach (Ability ab in m_realmAbilities)
                RemoveAbility(ab.KeyName);

            m_realmAbilities.Clear();
            if (!ServerProperties.Properties.FREE_RESPEC && useRespecPoint)
                RespecAmountRealmSkill--;
            return any;
        }

        protected virtual bool RespecAllLines()
        {
            bool ok = false;
            IList<Specialization> specList = GetSpecList().Where(e => e.Trainable).ToList();
            foreach (Specialization cspec in specList)
            {
                if (cspec.Level < 2)
                    continue;
                RespecSingleLine(cspec);
                ok = true;
            }
            return ok;
        }

        /// <summary>
        /// Respec single line
        /// </summary>
        /// <param name="specLine">spec line being respec'd</param>
        /// <returns>Amount of points spent in that line</returns>
        protected virtual int RespecSingleLine(Specialization specLine)
        {
            int specPoints = (specLine.Level * (specLine.Level + 1) - 2) / 2;
            // Graveen - autotrain 1.87
            specPoints -= GetAutoTrainPoints(specLine, 0);

            //setting directly the autotrain points in the spec
            if (GetAutoTrainPoints(specLine, 4) == 1 && Level >= 8)
            {
                specLine.Level = (int)Math.Floor((double)Level / 4);
            }
            else specLine.Level = 1;

            // If BD subpet spells scaled and capped by BD spec, respecing a spell line
            //	requires re-scaling the spells for all subpets from that line.
            if (CharacterClass is CharacterClassBoneDancer
                && ServerProperties.Properties.PET_SCALE_SPELL_MAX_LEVEL > 0
                && ServerProperties.Properties.PET_CAP_BD_MINION_SPELL_SCALING_BY_SPEC
                && ControlledBrain is IControlledBrain brain && brain.Body is GameSummonedPet pet
                && pet.ControlledNpcList != null)
                foreach (ABrain subBrain in pet.ControlledNpcList)
                    if (subBrain != null && subBrain.Body is BDSubPet subPet && subPet.PetSpecLine == specLine.KeyName)
                        subPet.SortSpells();

            return specPoints;
        }

        /// <summary>
        /// Send this players trainer window
        /// </summary>
        public virtual void SendTrainerWindow()
        {
            //Out.SendTrainerWindow();
        }

        /// <summary>
        /// returns a list with all specializations
        /// in the order they were added
        /// </summary>
        /// <returns>list of Spec's</returns>
        public virtual IList<Specialization> GetSpecList()
        {
            List<Specialization> list;

            lock (((ICollection)m_specialization).SyncRoot)
            {
                // sort by Level and ID to simulate "addition" order... (try to sort your DB if you want to change this !)
                list = m_specialization.Select(item => item.Value).OrderBy(it => it.LevelRequired).ThenBy(it => it.ID).ToList();
            }

            return list;
        }

        /// <summary>
        /// returns a list with all non trainable skills without styles
        /// This is a copy of Ability until any unhandled Skill subclass needs to go in there...
        /// </summary>
        /// <returns>list of Skill's</returns>
        public virtual IList GetNonTrainableSkillList()
        {
            return GetAllAbilities();
        }

        /// <summary>
        /// Retrives a specific specialization by name
        /// </summary>
        /// <param name="name">the name of the specialization line</param>
        /// <param name="caseSensitive">false for case-insensitive compare</param>
        /// <returns>found specialization or null</returns>
        public virtual Specialization GetSpecializationByName(string name, bool caseSensitive = false)
        {
            Specialization spec = null;

            lock (((ICollection)m_specialization).SyncRoot)
            {
                if (caseSensitive && m_specialization.ContainsKey(name))
                    spec = m_specialization[name];

                foreach (KeyValuePair<string, Specialization> entry in m_specialization)
                {
                    if (entry.Key.ToLower().Equals(name.ToLower()))
                    {
                        spec = entry.Value;
                        break;
                    }
                }
            }

            return spec;
        }

        /// <summary>
        /// The best armor level this player can use.
        /// </summary>
        public virtual int BestArmorLevel
        {
            get
            {
                int bestLevel = -1;
                bestLevel = Math.Max(bestLevel, GetAbilityLevel("AlbArmor"));
                bestLevel = Math.Max(bestLevel, GetAbilityLevel("HibArmor"));
                bestLevel = Math.Max(bestLevel, GetAbilityLevel("MidArmor"));
                return bestLevel;
            }
        }

        #region Abilities

        /// <summary>
        /// Adds a new Ability to the player
        /// </summary>
        /// <param name="ability"></param>
        /// <param name="sendUpdates"></param>
        public override void AddAbility(Ability ability, bool sendUpdates)
        {
            if (ability == null)
                return;

            base.AddAbility(ability, sendUpdates);
        }

        /// <summary>
        /// Adds a Realm Ability to the player
        /// </summary>
        /// <param name="ability"></param>
        /// <param name="sendUpdates"></param>
        public virtual void AddRealmAbility(RealmAbility ability, bool sendUpdates)
        {
            if (ability == null)
                return;

            m_realmAbilities.FreezeWhile(list =>
            {
                int index = list.FindIndex(ab => ab.ID == ability.ID);
                if (index > -1)
                {
                    list[index].Level = ability.Level;
                }
                else
                {
                    list.Add(ability);
                }
            });

            RefreshSpecDependantSkills(false);
        }

        #endregion Abilities

        public virtual void RemoveAllAbilities()
        {
            lock (m_lockAbilities)
            {
                m_abilities.Clear();
            }
        }

        public virtual void RemoveAllSpecs()
        {
            lock (((ICollection)m_specialization).SyncRoot)
            {
                m_specialization.Clear();
            }
        }

        public virtual void RemoveAllSpellLines()
        {
            lock (lockSpellLinesList)
            {
                m_spellLines.Clear();
            }
        }

        /// <summary>
        /// Retrieve this player Realm Abilities.
        /// </summary>
        /// <returns></returns>
        public virtual List<RealmAbility> GetRealmAbilities()
        {
            return m_realmAbilities.ToList();
        }

        /// <summary>
        /// Asks for existance of specific specialization
        /// </summary>
        /// <param name="keyName"></param>
        /// <returns></returns>
        public virtual bool HasSpecialization(string keyName)
        {
            bool hasit = false;

            lock (((ICollection)m_specialization).SyncRoot)
            {
                hasit = m_specialization.ContainsKey(keyName);
            }

            return hasit;
        }

        /// <summary>
        /// returns the level of a specialization
        /// if 0 is returned, the spec is non existent on player
        /// </summary>
        /// <param name="keyName"></param>
        /// <returns></returns>
        public override int GetBaseSpecLevel(string keyName)
        {
            Specialization spec = null;
            int level = 0;

            lock (((ICollection)m_specialization).SyncRoot)
            {
                if (m_specialization.TryGetValue(keyName, out spec))
                    level = m_specialization[keyName].Level;
            }

            return level;
        }

        /// <summary>
        /// returns the level of a specialization + bonuses from RR and Items
        /// if 0 is returned, the spec is non existent on the player
        /// </summary>
        /// <param name="keyName"></param>
        /// <returns></returns>
        public override int GetModifiedSpecLevel(string keyName)
        {
            if (keyName.StartsWith(GlobalSpellsLines.Champion_Lines_StartWith))
                return 50;

            if (keyName.StartsWith(GlobalSpellsLines.Realm_Spells))
                return this.Level;

            Specialization spec = null;
            int level = 0;
            lock (((ICollection)m_specialization).SyncRoot)
            {
                if (!m_specialization.TryGetValue(keyName, out spec))
                {
                    if (keyName == GlobalSpellsLines.Combat_Styles_Effect)
                    {
                        if (CharacterClass.ID == (int)eCharacterClass.Reaver || CharacterClass.ID == (int)eCharacterClass.Heretic)
                            level = GetModifiedSpecLevel(Specs.Flexible);
                        if (CharacterClass.ID == (int)eCharacterClass.Valewalker)
                            level = GetModifiedSpecLevel(Specs.Scythe);
                        if (CharacterClass.ID == (int)eCharacterClass.Savage)
                            level = GetModifiedSpecLevel(Specs.Savagery);
                    }

                    level = 0;
                }
            }

            if (spec != null)
            {
                level = spec.Level;
                // TODO: should be all in calculator later, right now
                // needs specKey -> eProperty conversion to find calculator and then
                // needs eProperty -> specKey conversion to find how much points player has spent
                eProperty skillProp = SkillBase.SpecToSkill(keyName);
                if (skillProp != eProperty.Undefined)
                    level += GetModified(skillProp);
            }

            return level;
        }

        /// <summary>
        /// Adds a spell line to the player
        /// </summary>
        /// <param name="line"></param>
        public virtual void AddSpellLine(SpellLine line)
        {
            AddSpellLine(line, true);
        }

        /// <summary>
        /// Adds a spell line to the player
        /// </summary>
        /// <param name="line"></param>
        public virtual void AddSpellLine(SpellLine line, bool notify)
        {
            if (line == null)
                return;

            SpellLine oldline = GetSpellLine(line.KeyName);
            if (oldline == null)
            {
                lock (lockSpellLinesList)
                {
                    m_spellLines.Add(line);
                }
            }
            else
            {
                oldline.Level = line.Level;
            }
        }

        /// <summary>
        /// return a list of spell lines in the order they were added
        /// this is a copy only.
        /// </summary>
        /// <returns></returns>
        public virtual List<SpellLine> GetSpellLines()
        {
            List<SpellLine> list = new List<SpellLine>();
            lock (lockSpellLinesList)
            {
                list = new List<SpellLine>(m_spellLines);
            }

            return list;
        }

        /// <summary>
        /// find a spell line on player and return them
        /// </summary>
        /// <param name="keyname"></param>
        /// <returns></returns>
        public virtual SpellLine GetSpellLine(string keyname)
        {
            lock (lockSpellLinesList)
            {
                foreach (SpellLine line in m_spellLines)
                {
                    if (line.KeyName == keyname)
                        return line;
                }
            }
            return null;
        }

        /// <summary>
        /// Skill cache, maintained for network order on "skill use" request...
        /// Second item is for "Parent" Skill if applicable
        /// </summary>
        protected ReaderWriterList<Tuple<Skill, Skill>> m_usableSkills = new ReaderWriterList<Tuple<Skill, Skill>>();

        /// <summary>
        /// List Cast cache, maintained for network order on "spell use" request...
        /// Second item is for "Parent" SpellLine if applicable
        /// </summary>
        protected ReaderWriterList<Tuple<SpellLine, List<Skill>>> m_usableListSpells = new ReaderWriterList<Tuple<SpellLine, List<Skill>>>();

        /// <summary>
        /// Get All Usable Spell for a list Caster.
        /// </summary>
        /// <param name="update"></param>
        /// <returns></returns>
        public virtual List<Tuple<SpellLine, List<Skill>>> GetAllUsableListSpells(bool update = false)
        {
            List<Tuple<SpellLine, List<Skill>>> results = new List<Tuple<SpellLine, List<Skill>>>();

            if (!update)
            {
                if (m_usableListSpells.Count > 0)
                    results = new List<Tuple<SpellLine, List<Skill>>>(m_usableListSpells);

                // return results if cache is valid.
                if (results.Count > 0)
                    return results;
            }

            // lock during all update, even if replace only take place at end...
            m_usableListSpells.FreezeWhile(innerList =>
            {

                List<Tuple<SpellLine, List<Skill>>> finalbase = new List<Tuple<SpellLine, List<Skill>>>();
                List<Tuple<SpellLine, List<Skill>>> finalspec = new List<Tuple<SpellLine, List<Skill>>>();

                // Add Lists spells ordered.
                foreach (Specialization spec in GetSpecList().Where(item => !item.HybridSpellList))
                {
                    var spells = GetLinesSpellsForLiving(this, spec.Level, spec);

                    foreach (SpellLine sl in GetSpellLinesForMimic(this, Level, spec.KeyName))
                    {
                        List<Tuple<SpellLine, List<Skill>>> working;
                        if (sl.IsBaseLine)
                        {
                            working = finalbase;
                        }
                        else
                        {
                            working = finalspec;
                        }

                        List<Skill> sps = new List<Skill>();
                        SpellLine key = spells.Keys.FirstOrDefault(el => el.ID == sl.ID);

                        if (key != null && spells.ContainsKey(key))
                        {
                            foreach (Skill sp in spells[key])
                            {
                                sps.Add(sp);
                            }
                        }

                        working.Add(new Tuple<SpellLine, List<Skill>>(sl, sps));
                    }
                }

                // Linq isn't used, we need to keep order ! (SelectMany, GroupBy, ToDictionary can't be used !)
                innerList.Clear();
                foreach (var tp in finalbase)
                {
                    innerList.Add(tp);
                    results.Add(tp);
                }

                foreach (var tp in finalspec)
                {
                    innerList.Add(tp);
                    results.Add(tp);
                }
            });

            return results;
        }

        /// <summary>
        /// Get All Player Usable Skill Ordered in Network Order (usefull to check for useskill)
        /// This doesn't get player's List Cast Specs...
        /// </summary>
        /// <param name="update"></param>
        /// <returns></returns>
        public virtual List<Tuple<Skill, Skill>> GetAllUsableSkills(bool update = false)
        {
            List<Tuple<Skill, Skill>> results = new List<Tuple<Skill, Skill>>();

            if (!update)
            {
                if (m_usableSkills.Count > 0)
                    results = new List<Tuple<Skill, Skill>>(m_usableSkills);

                // return results if cache is valid.
                if (results.Count > 0)
                    return results;
            }

            // need to lock for all update.
            m_usableSkills.FreezeWhile(innerList =>
            {

                IList<Specialization> specs = GetSpecList();
                List<Tuple<Skill, Skill>> copylist = new List<Tuple<Skill, Skill>>(innerList);

                // Add Spec
                foreach (Specialization spec in specs.Where(item => item.Trainable))
                {
                    int index = innerList.FindIndex(e => (e.Item1 is Specialization specialization) && specialization.ID == spec.ID);

                    if (index < 0)
                    {
                        // Specs must be appended to spec list
                        innerList.Insert(innerList.Count(e => e.Item1 is Specialization), new Tuple<Skill, Skill>(spec, spec));
                    }
                    else
                    {
                        copylist.Remove(innerList[index]);
                        // Replace...
                        innerList[index] = new Tuple<Skill, Skill>(spec, spec);
                    }
                }

                // Add Abilities (Realm ability should be a custom spec)
                // Abilities order should be saved to db and loaded each time
                foreach (Specialization spec in specs)
                {
                    foreach (Ability abv in spec.GetAbilitiesForLiving(this))
                    {
                        // We need the Instantiated Ability Object for Displaying Correctly According to Player "Activation" Method (if Available)
                        Ability ab = GetAbility(abv.KeyName);

                        if (ab == null)
                            ab = abv;

                        int index = innerList.FindIndex(k => (k.Item1 is Ability ability) && ability.ID == ab.ID);

                        if (index < 0)
                        {
                            // add
                            innerList.Add(new Tuple<Skill, Skill>(ab, spec));
                        }
                        else
                        {
                            copylist.Remove(innerList[index]);
                            // replace
                            innerList[index] = new Tuple<Skill, Skill>(ab, spec);
                        }
                    }
                }

                // Add Hybrid spells
                foreach (Specialization spec in specs.Where(item => item.HybridSpellList))
                {
                    foreach (KeyValuePair<SpellLine, List<Skill>> sl in spec.GetLinesSpellsForLiving(this))
                    {
                        int index = -1;

                        foreach (Spell sp in sl.Value.Where(it => (it is Spell) && !((Spell)it).NeedInstrument).Cast<Spell>())
                        {
                            if (index < innerList.Count)
                                index = innerList.FindIndex(index + 1, e => (e.Item2 is SpellLine spellLine) && spellLine.ID == sl.Key.ID && (e.Item1 is Spell spell) && !spell.NeedInstrument);

                            if (index < 0 || index >= innerList.Count)
                            {
                                // add
                                innerList.Add(new Tuple<Skill, Skill>(sp, sl.Key));
                                // disable replace
                                index = innerList.Count;
                            }
                            else
                            {
                                copylist.Remove(innerList[index]);
                                // replace
                                innerList[index] = new Tuple<Skill, Skill>(sp, sl.Key);
                            }
                        }
                    }
                }

                // Add Songs
                int songIndex = -1;
                foreach (Specialization spec in specs.Where(item => item.HybridSpellList))
                {
                    foreach (KeyValuePair<SpellLine, List<Skill>> sl in spec.GetLinesSpellsForLiving(this))
                    {
                        foreach (Spell sp in sl.Value.Where(it => (it is Spell) && ((Spell)it).NeedInstrument).Cast<Spell>())
                        {
                            if (songIndex < innerList.Count)
                                songIndex = innerList.FindIndex(songIndex + 1, e => (e.Item1 is Spell) && ((Spell)e.Item1).NeedInstrument);

                            if (songIndex < 0 || songIndex >= innerList.Count)
                            {
                                // add
                                innerList.Add(new Tuple<Skill, Skill>(sp, sl.Key));
                                // disable replace
                                songIndex = innerList.Count;
                            }
                            else
                            {
                                copylist.Remove(innerList[songIndex]);
                                // replace
                                innerList[songIndex] = new Tuple<Skill, Skill>(sp, sl.Key);
                            }
                        }
                    }
                }

                // Add Styles
                foreach (Specialization spec in specs)
                {
                    foreach (Style st in spec.GetStylesForLiving(this))
                    {
                        int index = innerList.FindIndex(e => (e.Item1 is Style) && e.Item1.ID == st.ID);
                        if (index < 0)
                        {
                            // add
                            innerList.Add(new Tuple<Skill, Skill>(st, spec));
                        }
                        else
                        {
                            copylist.Remove(innerList[index]);
                            // replace
                            innerList[index] = new Tuple<Skill, Skill>(st, spec);
                        }
                    }
                }

                // clean all not re-enabled skills
                foreach (Tuple<Skill, Skill> item in copylist)
                {
                    innerList.Remove(item);
                }

                foreach (Tuple<Skill, Skill> el in innerList)
                    results.Add(el);
            });

            return results;
        }

        /// <summary>
        /// updates the list of available skills (dependent on caracter specs)
        /// </summary>
        /// <param name="sendMessages">sends "you learn" messages if true</param>
        public virtual void RefreshSpecDependantSkills(bool sendMessages)
        {
            // refresh specs
            LoadClassSpecializations(sendMessages);

            // lock specialization while refreshing...
            lock (((ICollection)m_specialization).SyncRoot)
            {
                foreach (Specialization spec in m_specialization.Values)
                {
                    // check for new Abilities
                    foreach (Ability ab in GetAbilitiesForMimic(spec.KeyName, this, Level))
                    {
                        if (!HasAbility(ab.KeyName) || GetAbility(ab.KeyName).Level < ab.Level)
                            AddAbility(ab, false);
                    }

                    // check for new Styles
                    foreach (Style st in GetStylesForMimic(spec.KeyName, this, spec.Level))
                    {
                        if (st.SpecLevelRequirement == 2 && Level > 5)
                        {
                            if (m_styles.Contains(st))
                            {
                                log.Info("Removed base style.");
                                m_styles.Remove(st);
                            }

                            continue;
                        }

                        //styleComponent.AddStyle(st, false);
                        AddStyle(st, false);
                    }

                    // check for new SpellLine
                    foreach (SpellLine sl in GetSpellLinesForMimic(this, spec.Level, spec.KeyName))
                    {
                        AddSpellLine(sl, false);
                    }
                }
            }
        }

        public virtual void AddStyle(Style st, bool notify)
        {
            lock (m_styles)
            {
                if (!m_styles.Contains(st))
                {
                    m_styles.Add(st);
                }
            }

            Styles = m_styles;
        }

        /// <summary>
        /// Called by trainer when specialization points were added to a skill
        /// </summary>
        /// <param name="skill"></param>
        public void OnSkillTrained(Specialization skill)
        {
            //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnSkillTrained.YouSpend", skill.Level, skill.Name), eChatType.CT_System, eChatLoc.CL_SystemWindow);
            //.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnSkillTrained.YouHave", SkillSpecialtyPoints), eChatType.CT_System, eChatLoc.CL_SystemWindow);
            Message.SystemToOthers(this, LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnSkillTrained.TrainsInVarious", GetName(0, true)), eChatType.CT_System);
            //CharacterClass.OnSkillTrained(this, skill);
            RefreshSpecDependantSkills(true);

            //Out.SendUpdatePlayerSkills();
        }

        /// <summary>
        /// effectiveness of the player (resurrection illness)
        /// Effectiveness is used in physical/magic damage (exept dot), in weapon skill and max concentration formula
        /// </summary>
        protected double m_playereffectiveness = 1.0;

        /// <summary>
        /// get / set the player's effectiveness.
        /// Effectiveness is used in physical/magic damage (exept dot), in weapon skill and max concentration
        /// </summary>
        public override double Effectiveness
        {
            get { return m_playereffectiveness; }
            set { m_playereffectiveness = value; }
        }

        /// <summary>
        /// Creates new effects list for this living.
        /// </summary>
        /// <returns>New effects list instance</returns>
        //protected override GameEffectList CreateEffectsList()
        //{
        //    return new GameEffectPlayerList(this);
        //}

        #endregion

        #region Realm-/Region-/Bount-/Skillpoints...

        private long m_bountyPoints;
        /// <summary>
        /// Gets/sets player bounty points
        /// (delegate to PlayerCharacter)
        /// </summary>
        public virtual long BountyPoints
        {
            get { return m_bountyPoints; }
            set { m_bountyPoints = value; }
        }

        private long m_realmPoints;
        /// <summary>
        /// Gets/sets player realm points
        /// (delegate to PlayerCharacter)
        /// </summary>
        public virtual long RealmPoints
        {
            get { return m_realmPoints; }
            set { m_realmPoints = value; }
        }

        /// <summary>
        /// Gets/sets player skill specialty points
        /// </summary>
        public virtual int SkillSpecialtyPoints
        {
            get { return VerifySpecPoints(); }
        }

        /// <summary>
        /// Gets/sets player realm specialty points
        /// </summary>
        //public virtual int RealmSpecialtyPoints
        //{
        //    get
        //    {
        //        return GameServer.ServerRules.GetPlayerRealmPointsTotal(this)
        //                 - GetRealmAbilities().Where(ab => !(ab is RR5RealmAbility))
        //                     .Sum(ab => Enumerable.Range(0, ab.Level).Sum(i => ab.CostForUpgrade(i)));
        //    }
        //}

        private int m_realmLevel;
        /// <summary>
        /// Gets/sets player realm rank
        /// </summary>
        public virtual int RealmLevel
        {
            get { return m_realmLevel; }
            set { m_realmLevel = value; }
        }

        /// <summary>
        /// Returns the translated realm rank title of the player.
        /// </summary>
        /// <param name="language"></param>
        /// <returns></returns>
        public virtual string RealmRankTitle(string language)
        {
            string translationId = string.Empty;

            if (Realm != eRealm.None && Realm != eRealm.Door)
            {
                int RR = 0;

                if (RealmLevel > 1)
                    RR = RealmLevel / 10 + 1;

                string realm = string.Empty;
                if (Realm == eRealm.Albion)
                    realm = "Albion";
                else if (Realm == eRealm.Midgard)
                    realm = "Midgard";
                else
                    realm = "Hibernia";

                string gender = Gender == eGender.Female ? "Female" : "Male";

                translationId = string.Format("{0}.RR{1}.{2}", realm, RR, gender);
            }
            else
            {
                translationId = "UnknownRealm";
            }

            string translation;
            if (!LanguageMgr.TryGetTranslation(out translation, language, string.Format("GamePlayer.RealmTitle.{0}", translationId)))
                translation = RealmTitle;

            return translation;
        }

        /// <summary>
        /// Gets player realm rank name
        /// sirru mod 20.11.06
        /// </summary>
        public virtual string RealmTitle
        {
            get
            {
                if (Realm == eRealm.None)
                    return "Unknown Realm";

                try
                {
                    return GlobalConstants.REALM_RANK_NAMES[(int)Realm - 1, (int)Gender - 1, (RealmLevel / 10)];
                }
                catch
                {
                    return "Unknown Rank"; // why aren't all the realm ranks defined above?
                }
            }
        }

        /// <summary>
        /// Called when this player gains realm points
        /// </summary>
        /// <param name="amount">The amount of realm points gained</param>
        public override void GainRealmPoints(long amount)
        {
            GainRealmPoints(amount, true, true);
        }

        /// <summary>
        /// Called when this living gains realm points
        /// </summary>
        /// <param name="amount">The amount of realm points gained</param>
        public void GainRealmPoints(long amount, bool modify)
        {
            GainRealmPoints(amount, modify, true);
        }

        /// <summary>
        /// Called when this player gains realm points
        /// </summary>
        /// <param name="amount"></param>
        /// <param name="modify"></param>
        /// <param name="sendMessage"></param>
        public void GainRealmPoints(long amount, bool modify, bool sendMessage)
        {
            GainRealmPoints(amount, modify, true, true);
        }

        /// <summary>
        /// Called when this player gains realm points
        /// </summary>
        /// <param name="amount">The amount of realm points gained</param>
        /// <param name="modify">Should we apply the rp modifer</param>
        /// <param name="sendMessage">Wether to send a message like "You have gained N realmpoints"</param>
        /// <param name="notify"></param>
        public virtual void GainRealmPoints(long amount, bool modify, bool sendMessage, bool notify)
        {
            if (!GainRP)
                return;

            if (modify)
            {
                //rp rate modifier
                double modifier = ServerProperties.Properties.RP_RATE;
                if (modifier != -1)
                    amount = (long)(amount * modifier);

                //[StephenxPimente]: Zone Bonus Support
                if (ServerProperties.Properties.ENABLE_ZONE_BONUSES)
                {
                    //int zoneBonus = (((int)amount * ZoneBonus.GetRPBonus(this)) / 100);
                    //if (zoneBonus > 0)
                    //{
                    //   /Out.SendMessage(ZoneBonus.GetBonusMessage(this, (int)(zoneBonus * ServerProperties.Properties.RP_RATE), ZoneBonus.eZoneBonusType.RP),
                    //        eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                    //    GainRealmPoints((long)(zoneBonus * ServerProperties.Properties.RP_RATE), false, false, false);
                    //}
                }

                //[Freya] Nidel: ToA Rp Bonus
                long rpBonus = GetModified(eProperty.RealmPoints);
                if (rpBonus > 0)
                {
                    amount += (amount * rpBonus) / 100;
                }

                #region Kill Streak

                int killBonus = (int)((0.05 * (KillStreak > 10 ? 10 : KillStreak)) * amount);
                //this.Out.SendMessage($"{KillStreak} kill streak! ({killBonus} RPs)", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                amount += killBonus;

                #endregion
            }

            if (notify)
                base.GainRealmPoints(amount);

            RealmPoints += amount;

            //if (m_guild != null && Client.Account.PrivLevel == 1)
            //    m_guild.RealmPoints += amount;

            if (sendMessage == true && amount > 0)
                while (RealmPoints >= CalculateRPsFromRealmLevel(RealmLevel + 1) && RealmLevel < (REALMPOINTS_FOR_LEVEL.Length - 1))
                {
                    RealmLevel++;

                    if (RealmLevel % 10 == 0)
                    {
                        foreach (GamePlayer plr in GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
                            plr.Out.SendLivingDataUpdate(this, true);

                        Notify(GamePlayerEvent.RRLevelUp, this);
                    }
                    else
                        Notify(GamePlayerEvent.RLLevelUp, this);
                    //if (CanGenerateNews && ((RealmLevel >= 40 && RealmLevel % 10 == 0) || RealmLevel >= 60))
                    //{
                    //    string message = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.GainRealmPoints.ReachedRankNews", Name, RealmLevel + 10, LastPositionUpdateZone.Description);
                    //    string newsmessage = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.GainRealmPoints.ReachedRankNews", Name, RealmLevel + 10, LastPositionUpdateZone.Description);
                    //    NewsMgr.CreateNews(newsmessage, this.Realm, eNewsType.RvRLocal, true);
                    //}
                    //if (CanGenerateNews && RealmPoints >= 1000000 && RealmPoints - amount < 1000000)
                    //{
                    //    string message = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.GainRealmPoints.Earned", Name, LastPositionUpdateZone.Description);
                    //    string newsmessage = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.GainRealmPoints.Earned", Name, LastPositionUpdateZone.Description);
                    //    NewsMgr.CreateNews(newsmessage, this.Realm, eNewsType.RvRLocal, true);
                    //}
                }

            //if (GetAchievementProgress(AchievementUtils.AchievementNames.Realm_Rank) <= (int)Math.Floor((double)(RealmLevel + 10.0) / 10.0))
            //{
            //    SetAchievementTo(AchievementUtils.AchievementNames.Realm_Rank, (int)Math.Floor((double)(RealmLevel + 10.0) / 10.0));
            //}

            //Out.SendUpdatePoints();
        }

        /// <summary>
        /// Called when this living buy something with realm points
        /// </summary>
        /// <param name="amount">The amount of realm points loosed</param>
        public bool RemoveBountyPoints(long amount)
        {
            return RemoveBountyPoints(amount, null);
        }
        /// <summary>
        /// Called when this living buy something with realm points
        /// </summary>
        /// <param name="amount"></param>
        /// <param name="str"></param>
        /// <returns></returns>
        public bool RemoveBountyPoints(long amount, string str)
        {
            return RemoveBountyPoints(amount, str, eChatType.CT_Say, eChatLoc.CL_SystemWindow);
        }
        /// <summary>
        /// Called when this living buy something with realm points
        /// </summary>
        /// <param name="amount">The amount of realm points loosed</param>
        /// <param name="loc">The chat location</param>
        /// <param name="str">The message</param>
        /// <param name="type">The chat type</param>
        public virtual bool RemoveBountyPoints(long amount, string str, eChatType type, eChatLoc loc)
        {
            if (BountyPoints < amount)
                return false;
            BountyPoints -= amount;
            //Out.SendUpdatePoints();
            // if (str != null && amount != 0)
            //     Out.SendMessage(str, type, loc);
            return true;
        }

        /// <summary>
        /// Player gains bounty points
        /// </summary>
        /// <param name="amount">The amount of bounty points</param>
        public override void GainBountyPoints(long amount)
        {
            GainBountyPoints(amount, true, true);
        }

        /// <summary>
        /// Player gains bounty points
        /// </summary>
        /// <param name="amount">The amount of bounty points</param>
        public void GainBountyPoints(long amount, bool modify)
        {
            GainBountyPoints(amount, modify, true);
        }

        /// <summary>
        /// Called when player gains bounty points
        /// </summary>
        /// <param name="amount"></param>
        /// <param name="modify"></param>
        /// <param name="sendMessage"></param>
        public void GainBountyPoints(long amount, bool modify, bool sendMessage)
        {
            GainBountyPoints(amount, modify, true, true);
        }


        /// <summary>
        /// Called when player gains bounty points
        /// </summary>
        /// <param name="amount">The amount of bounty points gained</param>
        /// <param name="multiply">Should this amount be multiplied by the BP Rate</param>
        /// <param name="sendMessage">Wether to send a message like "You have gained N bountypoints"</param>
        public virtual void GainBountyPoints(long amount, bool modify, bool sendMessage, bool notify)
        {
            if (modify)
            {
                //bp rate modifier
                double modifier = ServerProperties.Properties.BP_RATE;
                if (modifier != -1)
                    amount = (long)(amount * modifier);

                //[StephenxPimente]: Zone Bonus Support
                if (ServerProperties.Properties.ENABLE_ZONE_BONUSES)
                {
                    //int zoneBonus = (((int)amount * ZoneBonus.GetBPBonus(this)) / 100);
                    //if (zoneBonus > 0)
                    //{
                    //    //Out.SendMessage(ZoneBonus.GetBonusMessage(this, (int)(zoneBonus * ServerProperties.Properties.BP_RATE), ZoneBonus.eZoneBonusType.BP),
                    //        eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                    //    GainBountyPoints((long)(zoneBonus * ServerProperties.Properties.BP_RATE), false, false, false);
                    //}
                }

                //[Freya] Nidel: ToA Bp Bonus
                long bpBonus = GetModified(eProperty.BountyPoints);

                if (bpBonus > 0)
                {
                    amount += (amount * bpBonus) / 100;
                }
            }

            if (notify)
                base.GainBountyPoints(amount);

            BountyPoints += amount;

            //if (m_guild != null && Client.Account.PrivLevel == 1)
            //    m_guild.BountyPoints += amount;

            ///if (sendMessage == true)
               // Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.GainBountyPoints.YouGet", amount.ToString()), eChatType.CT_Important, eChatLoc.CL_SystemWindow);

            //Out.SendUpdatePoints();
        }

        /// <summary>
        /// Holds realm points needed for special realm level
        /// </summary>
        public static readonly long[] REALMPOINTS_FOR_LEVEL =
        {
            0,	// for level 0
            0,	// for level 1
            25,	// for level 2
            125,	// for level 3
            350,	// for level 4
            750,	// for level 5
            1375,	// for level 6
            2275,	// for level 7
            3500,	// for level 8
            5100,	// for level 9
            7125,	// for level 10
            9625,	// for level 11
            12650,	// for level 12
            16250,	// for level 13
            20475,	// for level 14
            25375,	// for level 15
            31000,	// for level 16
            37400,	// for level 17
            44625,	// for level 18
            52725,	// for level 19
            61750,	// for level 20
            71750,	// for level 21
            82775,	// for level 22
            94875,	// for level 23
            108100,	// for level 24
            122500,	// for level 25
            138125,	// for level 26
            155025,	// for level 27
            173250,	// for level 28
            192850,	// for level 29
            213875,	// for level 30
            236375,	// for level 31
            260400,	// for level 32
            286000,	// for level 33
            313225,	// for level 34
            342125,	// for level 35
            372750,	// for level 36
            405150,	// for level 37
            439375,	// for level 38
            475475,	// for level 39
            513500,	// for level 40
            553500,	// for level 41
            595525,	// for level 42
            639625,	// for level 43
            685850,	// for level 44
            734250,	// for level 45
            784875,	// for level 46
            837775,	// for level 47
            893000,	// for level 48
            950600,	// for level 49
            1010625,	// for level 50
            1073125,	// for level 51
            1138150,	// for level 52
            1205750,	// for level 53
            1275975,	// for level 54
            1348875,	// for level 55
            1424500,	// for level 56
            1502900,	// for level 57
            1584125,	// for level 58
            1668225,	// for level 59
            1755250,	// for level 60
            1845250,	// for level 61
            1938275,	// for level 62
            2034375,	// for level 63
            2133600,	// for level 64
            2236000,	// for level 65
            2341625,	// for level 66
            2450525,	// for level 67
            2562750,	// for level 68
            2678350,	// for level 69
            2797375,	// for level 70
            2919875,	// for level 71
            3045900,	// for level 72
            3175500,	// for level 73
            3308725,	// for level 74
            3445625,	// for level 75
            3586250,	// for level 76
            3730650,	// for level 77
            3878875,	// for level 78
            4030975,	// for level 79
            4187000,	// for level 80
            4347000,	// for level 81
            4511025,	// for level 82
            4679125,	// for level 83
            4851350,	// for level 84
            5027750,	// for level 85
            5208375,	// for level 86
            5393275,	// for level 87
            5582500,	// for level 88
            5776100,	// for level 89
            5974125,	// for level 90
            6176625,	// for level 91
            6383650,	// for level 92
            6595250,	// for level 93
            6811475,	// for level 94
            7032375,	// for level 95
            7258000,	// for level 96
            7488400,	// for level 97
            7723625,	// for level 98
            7963725,	// for level 99
            8208750,	// for level 100
            9111713,	// for level 101
            10114001,	// for level 102
            11226541,	// for level 103
            12461460,	// for level 104
            13832221,	// for level 105
            15353765,	// for level 106
            17042680,	// for level 107
            18917374,	// for level 108
            20998286,	// for level 109
            23308097,	// for level 110
            25871988,	// for level 111
            28717906,	// for level 112
            31876876,	// for level 113
            35383333,	// for level 114
            39275499,	// for level 115
            43595804,	// for level 116
            48391343,	// for level 117
            53714390,	// for level 118
            59622973,	// for level 119
            66181501,	// for level 120
            73461466,	// for level 121
            81542227,	// for level 122
            90511872,	// for level 123
            100468178,	// for level 124
            111519678,	// for level 125
            123786843,	// for level 126
            137403395,	// for level 127
            152517769,	// for level 128
            169294723,	// for level 129
            187917143,	// for level 130

        };

        /// <summary>
        /// Calculates amount of RealmPoints needed for special realm level
        /// </summary>
        /// <param name="realmLevel">realm level</param>
        /// <returns>amount of realm points</returns>
        protected virtual long CalculateRPsFromRealmLevel(int realmLevel)
        {
            if (realmLevel < REALMPOINTS_FOR_LEVEL.Length)
                return REALMPOINTS_FOR_LEVEL[realmLevel];

            // thanks to Linulo from http://daoc.foren.4players.de/viewtopic.php?t=40839&postdays=0&postorder=asc&start=0
            return (long)(25.0 / 3.0 * (realmLevel * realmLevel * realmLevel) - 25.0 / 2.0 * (realmLevel * realmLevel) + 25.0 / 6.0 * realmLevel);
        }

        /// <summary>
        /// Calculates realm level from realm points. SLOW.
        /// </summary>
        /// <param name="realmPoints">amount of realm points</param>
        /// <returns>realm level: RR5L3 = 43, RR1L2 = 2</returns>
        protected virtual int CalculateRealmLevelFromRPs(long realmPoints)
        {
            if (realmPoints == 0)
                return 0;

            int i;

            for (i = REALMPOINTS_FOR_LEVEL.Length - 1; i > 0; i--)
            {
                if (REALMPOINTS_FOR_LEVEL[i] <= realmPoints)
                    break;
            }

            return i;
        }

        /// <summary>
        /// Realm point value of this player
        /// </summary>
        public override int RealmPointsValue
        {
            get
            {
                // http://www.camelotherald.com/more/2275.shtml
                // new 1.81D formula
                // Realm point value = (level - 20)squared + (realm rank level x 5) + (champion level x 10) + (master level (squared)x 5)
                //we use realm level 1L0 = 0, mythic uses 1L0 = 10, so we + 10 the realm level
                int level = Math.Max(0, Level - 20);
                if (level == 0)
                    return Math.Max(1, (RealmLevel + 10) * 5);

                return Math.Max(1, level * level + (RealmLevel + 10) * 5);
            }
        }

        /// <summary>
        /// Bounty point value of this player
        /// </summary>
        public override int BountyPointsValue
        {
            // TODO: correct formula!
            get { return (int)(1 + Level * 0.6); }
        }

        /// <summary>
        /// Returns the amount of experience this player is worth
        /// </summary>
        public override long ExperienceValue
        {
            get
            {
                return base.ExperienceValue * 4;
            }
        }

        public static readonly int[] prcRestore =
        {
            // http://www.silicondragon.com/Gaming/DAoC/Misc/XPs.htm
            1,//0
            3,//1
            6,//2
            10,//3
            15,//4
            21,//5
            33,//6
            53,//7
            82,//8
            125,//9
            188,//10
            278,//11
            352,//12
            443,//13
            553,//14
            688,//15
            851,//16
            1048,//17
            1288,//18
            1578,//19
            1926,//20
            2347,//21
            2721,//22
            3146,//23
            3633,//24
            4187,//25
            4820,//26
            5537,//27
            6356,//28
            7281,//29
            8337,//30
            9532,//31 - from logs
            10886,//32 - from logs
            12421,//33 - from logs
            14161,//34
            16131,//35
            18360,//36 - recheck
            19965,//37 - guessed
            21857,//38
            23821,//39
            25928,//40 - guessed
            28244,//41
            30731,//42
            33411,//43
            36308,//44
            39438,//45
            42812,//46
            46454,//47
            50385,//48
            54625,//49
            59195,//50
        };

        /// <summary>
        /// Money value of this player
        /// </summary>
        public override long MoneyValue
        {
            get
            {
                return 5 * prcRestore[Level < GamePlayer.prcRestore.Length ? Level : GamePlayer.prcRestore.Length - 1];
            }
        }

        #endregion

        #region Level/Experience

        /// <summary>
        /// What is the maximum level a player can achieve?
        /// To alter this in a custom GamePlayer class you must override this method and
        /// provide your own XPForLevel array with MaxLevel + 1 entries
        /// </summary>
        public virtual byte MaxLevel
        {
            get { return 50; }
        }

        /// <summary>
        /// How much experience is needed for a given level?
        /// </summary>
        public virtual long GetExperienceNeededForLevel(int level)
        {
            if (level > MaxLevel)
                return GetScaledExperienceAmountForLevel(MaxLevel);

            if (level <= 0)
                return GetScaledExperienceAmountForLevel(0);

            return GetScaledExperienceAmountForLevel(level - 1);
        }

        /// <summary>
        /// How Much Experience Needed For Level
        /// </summary>
        /// <param name="level"></param>
        /// <returns></returns>
        public static long GetExperienceAmountForLevel(int level)
        {
            try
            {
                return XPForLevel[level];
            }
            catch
            {
                return 0;
            }
        }

        public static long GetScaledExperienceAmountForLevel(int level)
        {
            try
            {
                return ScaledXPForLevel[level];
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// A table that holds the required XP/Level
        /// This must include a final entry for MaxLevel + 1
        /// </summary>
        private static readonly long[] XPForLevel =
        {
            0, // xp to level 1
            50, // xp to level 2
            250, // xp to level 3
            850, // xp to level 4
            2300, // xp to level 5
            6350, // xp to level 6
            15950, // xp to level 7
            37950, // xp to level 8
            88950, // xp to level 9
            203950, // xp to level 10
            459950, // xp to level 11
            839950, // xp to level 12
            1399950, // xp to level 13
            2199950, // xp to level 14
            3399950, // xp to level 15
            5199950, // xp to level 16
            7899950, // xp to level 17
            11799950, // xp to level 18
            17499950, // xp to level 19
            25899950, // xp to level 20
            38199950, // xp to level 21
            54699950, // xp to level 22
            76999950, // xp to level 23
            106999950, // xp to level 24
            146999950, // xp to level 25
            199999950, // xp to level 26
            269999950, // xp to level 27
            359999950, // xp to level 28
            479999950, // xp to level 29
            639999950, // xp to level 30
            849999950, // xp to level 31
            1119999950, // xp to level 32
            1469999950, // xp to level 33
            1929999950, // xp to level 34
            2529999950, // xp to level 35
            3319999950, // xp to level 36
            4299999950, // xp to level 37
            5499999950, // xp to level 38
            6899999950, // xp to level 39
            8599999950, // xp to level 40
            12899999950, // xp to level 41
            20699999950, // xp to level 42
            29999999950, // xp to level 43
            40799999950, // xp to level 44
            53999999950, // xp to level 45
            69599999950, // xp to level 46
            88499999950, // xp to level 47
            110999999950, // xp to level 48
            137999999950, // xp to level 49
            169999999950, // xp to level 50
            999999999950, // xp to level 51
        };

        /// <summary>
        /// A table that holds the required XP/Level
        /// This must include a final entry for MaxLevel + 1
        /// </summary>
        private static readonly long[] ScaledXPForLevel =
        {
            0, // xp to level 1
            50, // xp to level 2
            250, // xp to level 3
            850, // xp to level 4
            2300, // xp to level 5
            6350, // xp to level 6
            15950, // xp to level 7
            37950, // xp to level 8
            88950, // xp to level 9
            203950, // xp to level 10
            459950, // xp to level 11
            839950, // xp to level 12
            1399950, // xp to level 13
            2199950, // xp to level 14
            3399950, // xp to level 15
            6499938, // xp to level 16
            9953937, // xp to level 17
            14985937, // xp to level 18
            22399936, // xp to level 19
            33410936, // xp to level 20
            49659935, // xp to level 21
            71656935, // xp to level 22
            101639934, // xp to level 23
            142309934, // xp to level 24
            196979933, // xp to level 25
            269999933, // xp to level 26
            367199932, // xp to level 27
            493199932, // xp to level 28
            662399931, // xp to level 29
            889599931, // xp to level 30
            1189999930, // xp to level 31
            1579199930, // xp to level 32
            2087399929, // xp to level 33
            2759899929, // xp to level 34
            3643199928, // xp to level 35
            4813999928, // xp to level 36
            6277999927, // xp to level 37
            8084999927, // xp to level 38
            10211999926, // xp to level 39
            12813999926, // xp to level 40
            16382999937, // xp to level 41
            20699999950, // xp to level 42
            29999999950, // xp to level 43
            40799999950, // xp to level 44
            53999999950, // xp to level 45
            69599999950, // xp to level 46
            88499999950, // xp to level 47
            110999999950, // xp to level 48
            137999999950, // xp to level 49
            169999999950, // xp to level 50
            999999999950, // xp to level 51
        };

        private long m_experience;
        /// <summary>
        /// Gets or sets the current xp of this player
        /// </summary>
        public virtual long Experience
        {
            get { return m_experience; }
            set
            {
                m_experience = value;
            }
        }

        /// <summary>
        /// Returns the xp that are needed for the next level
        /// </summary>
        public virtual long ExperienceForNextLevel
        {
            get
            {
                return GetExperienceNeededForLevel(Level + 1);
            }
        }

        /// <summary>
        /// Returns the xp that were needed for the current level
        /// </summary>
        public virtual long ExperienceForCurrentLevel
        {
            get
            {
                return GetExperienceNeededForLevel(Level);
            }
        }

        /// <summary>
        /// Returns the xp that is needed for the second stage of current level
        /// </summary>
        public virtual long ExperienceForCurrentLevelSecondStage
        {
            get { return 1 + ExperienceForCurrentLevel + (ExperienceForNextLevel - ExperienceForCurrentLevel) / 2; }
        }

        /// <summary>
        /// Returns how far into the level we have progressed
        /// A value between 0 and 1000 (1 bubble = 100)
        /// </summary>
        public virtual ushort LevelPermill
        {
            get
            {
                //No progress if we haven't even reached current level!
                if (Experience < ExperienceForCurrentLevel)
                    return 0;
                //No progess after maximum level
                if (Level > MaxLevel)
                    return 0;

                return
                    (ushort)(1000 * (Experience - ExperienceForCurrentLevel) / (ExperienceForNextLevel - ExperienceForCurrentLevel));
            }
        }

        public void ForceGainExperience(long expTotal)
        {
            if (IsLevelSecondStage)
            {
                if (Experience + expTotal < ExperienceForCurrentLevelSecondStage)
                {
                    expTotal = ExperienceForCurrentLevelSecondStage - Experience;
                }
            }
            else if (Experience + expTotal < ExperienceForCurrentLevel)
            {
                expTotal = ExperienceForCurrentLevel - Experience;
            }

            Experience += expTotal;

            //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.GainExperience.YouGet", expTotal.ToString("N0", System.Globalization.NumberFormatInfo.InvariantInfo)), eChatType.CT_Important, eChatLoc.CL_SystemWindow);

            if (expTotal >= 0)
            {
                //Level up
                if (Level >= 5 && !CharacterClass.HasAdvancedFromBaseClass())
                {
                    if (expTotal > 0)
                    {
                        //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.GainExperience.CannotRaise"), eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                        //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.GainExperience.TalkToTrainer"), eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                    }
                }
                else if (Level >= 40 && Level < MaxLevel && !IsLevelSecondStage && Experience >= ExperienceForCurrentLevelSecondStage)
                {
                    OnLevelSecondStage();
                    Notify(GamePlayerEvent.LevelSecondStage, this);
                }
                else if (Level < MaxLevel && Experience >= ExperienceForNextLevel)
                {
                    Level++;
                }
            }
            //Out.SendUpdatePoints();
        }

        /// <summary>
        /// Called whenever this player gains experience
        /// </summary>
        /// <param name="expTotal"></param>
        /// <param name="expCampBonus"></param>
        /// <param name="expGroupBonus"></param>
        /// <param name="expOutpostBonus"></param>
        /// <param name="sendMessage"></param>
        public void GainExperience(eXPSource xpSource, long expTotal, long expCampBonus, long expGroupBonus, long expOutpostBonus, bool sendMessage)
        {
            GainExperience(xpSource, expTotal, expCampBonus, expGroupBonus, expOutpostBonus, sendMessage, true);
        }

        /// <summary>
        /// Called whenever this player gains experience
        /// </summary>
        /// <param name="expTotal"></param>
        /// <param name="expCampBonus"></param>
        /// <param name="expGroupBonus"></param>
        /// <param name="expOutpostBonus"></param>
        /// <param name="sendMessage"></param>
        /// <param name="allowMultiply"></param>
        public void GainExperience(eXPSource xpSource, long expTotal, long expCampBonus, long expGroupBonus, long expOutpostBonus, bool sendMessage, bool allowMultiply)
        {
            GainExperience(xpSource, expTotal, expCampBonus, expGroupBonus, expOutpostBonus, sendMessage, allowMultiply, true);
        }

        /// <summary>
        /// Called whenever this player gains experience
        /// </summary>
        /// <param name="expTotal"></param>
        /// <param name="expCampBonus"></param>
        /// <param name="expGroupBonus"></param>
        /// <param name="expOutpostBonus"></param>
        /// <param name="sendMessage"></param>
        /// <param name="allowMultiply"></param>
        /// <param name="notify"></param>
        public override void GainExperience(eXPSource xpSource, long expTotal, long expCampBonus, long expGroupBonus, long expOutpostBonus, bool sendMessage, bool allowMultiply, bool notify)
        {
            if (!GainXP && expTotal > 0)
                return;

            if (HCFlag && this.Group != null)
            {
                foreach (var player in this.Group.GetPlayersInTheGroup())
                {
                    if (player.Level > this.Level + 5)
                        expTotal = 0;
                }
            }


            long baseXp = 0;
            //xp rate modifier
            if (allowMultiply)
            {
                //we only want to modify the base rate, not the group or camp bonus
                expTotal -= expGroupBonus;
                expTotal -= expCampBonus;
                expTotal -= expOutpostBonus;

                baseXp = expTotal;
                //[StephenxPimentel] - Zone Bonus XP Support
                if (ServerProperties.Properties.ENABLE_ZONE_BONUSES)
                {
                    //long zoneBonus = expTotal * ZoneBonus.GetXPBonus(this) / 100;
                    //if (zoneBonus > 0)
                    //{
                    //    long tmpBonus = (long)(zoneBonus * ServerProperties.Properties.XP_RATE);
                    //    Out.SendMessage(ZoneBonus.GetBonusMessage(this, (int)tmpBonus, ZoneBonus.eZoneBonusType.XP),
                    //        eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                    //    GainExperience(eXPSource.Other, tmpBonus, 0, 0, 0, 0, false, false, false);
                    //}
                }

                if (CurrentRegion.IsRvR)
                    expTotal = (long)(expTotal * ServerProperties.Properties.RvR_XP_RATE);
                else
                    expTotal = (long)(expTotal * ServerProperties.Properties.XP_RATE);

                // [Freya] Nidel: ToA Xp Bonus
                long xpBonus = GetModified(eProperty.XpPoints);
                if (xpBonus != 0)
                {
                    expTotal += (expTotal * xpBonus) / 100;
                }

                long hardXPCap = (long)(GameServer.ServerRules.GetExperienceForLiving(Level) * ServerProperties.Properties.XP_HARDCAP_PERCENT / 100);

                if (expTotal > hardXPCap)
                    expTotal = hardXPCap;

                expTotal += expOutpostBonus;
                expTotal += expGroupBonus;
                expTotal += expCampBonus;
            }

            // Get Champion Experience too
            // GainChampionExperience(expTotal);

            #region Guild XP Bonus
            //long guildBonus = 0;
            //if (this.Guild != null && !this.Guild.IsStartingGuild && this.Guild.BonusType == Guild.eBonusType.Experience && xpSource == eXPSource.NPC)
            //{
            //    guildBonus = (long)Math.Ceiling((double)expTotal * ServerProperties.Properties.GUILD_BUFF_XP / 100);
            //}
            //else if (this.Guild != null && this.Guild.IsStartingGuild && xpSource == eXPSource.NPC)
            //{
            //    guildBonus = (long)Math.Ceiling((double)expTotal * ServerProperties.Properties.GUILD_BUFF_XP / 200);
            //}

            //expTotal += guildBonus;

            #endregion Guild XP Bonus

            //Commenting base.GainExperience out as it was used to Notify which was only used by GuildEvent (which is now moved here)
            //base.GainExperience(xpSource, expTotal, expCampBonus, expGroupBonus, expOutpostBonus, atlasBonus, sendMessage, allowMultiply, notify);

            if (IsLevelSecondStage)
            {
                if (Experience + expTotal < ExperienceForCurrentLevelSecondStage)
                {
                    expTotal = ExperienceForCurrentLevelSecondStage - Experience;
                }
            }
            else if (Experience + expTotal < ExperienceForCurrentLevel)
            {
                expTotal = ExperienceForCurrentLevel - Experience;
            }

            int relicBonus = (int)(baseXp * (0.05 * RelicMgr.GetRelicCount(this.Realm)));
            if (relicBonus > 0) expTotal += relicBonus;

            Experience += expTotal;

            if (expTotal >= 0)
            {
                //Level up
                if (Level >= 40 && Level < MaxLevel && !IsLevelSecondStage && Experience >= ExperienceForCurrentLevelSecondStage)
                {
                    OnLevelSecondStage();
                    m_isLevelSecondStage = true;
                }
                else if (Level < MaxLevel && Experience >= ExperienceForNextLevel)
                {
                    log.Info("level up for " + Name);
                    Level++;
                }
            }

            //Out.SendUpdatePoints();
        }

        /// <summary>
        /// Gets or sets the level of the player
        /// (delegate to PlayerCharacter)
        /// </summary>
        public override byte Level
        {
            get { return base.Level; }
            set
            {
                int oldLevel = Level;
                base.Level = value;

                if (oldLevel > 0)
                    if (value > oldLevel)
                        OnLevelUp(oldLevel);
            }
        }

        /// <summary>
        /// What is the base, unmodified level of this character.
        /// </summary>
        public override byte BaseLevel
        {
            get { return DBCharacter != null ? (byte)DBCharacter.Level : base.BaseLevel; }
        }

        /// <summary>
        /// What level is displayed to another player
        /// </summary>
        public override byte GetDisplayLevel(GamePlayer player)
        {
            return Math.Min((byte)50, Level);
        }

        private bool m_isLevelSecondStage;

        /// <summary>
        /// Is this player in second stage of current level
        /// (delegate to PlayerCharacter)
        /// </summary>
        public virtual bool IsLevelSecondStage
        {
            get { return m_isLevelSecondStage; }
            set { m_isLevelSecondStage = value; }
        }

        /// <summary>
        /// Called when this player levels
        /// </summary>
        /// <param name="previouslevel"></param>
        public virtual void OnLevelUp(int previouslevel)
        {
            IsLevelSecondStage = false;

            if (Level - previouslevel < 2)
            {
                SpendSpecPoints(Level, false);
            }

            //level 20 changes realm title and gives 1 realm skill point
            if (Level == 20)
                GainRealmPoints(0);

            // Adjust stats
            // stat increases start at level 6
            if (Level > 5)
            {
                for (int i = Level; i > Math.Max(previouslevel, 5); i--)
                {
                    if (CharacterClass.PrimaryStat != eStat.UNDEFINED)
                    {
                        ChangeBaseStat(CharacterClass.PrimaryStat, 1);
                    }
                    if (CharacterClass.SecondaryStat != eStat.UNDEFINED && ((i - 6) % 2 == 0))
                    { // base level to start adding stats is 6
                        ChangeBaseStat(CharacterClass.SecondaryStat, 1);
                    }
                    if (CharacterClass.TertiaryStat != eStat.UNDEFINED && ((i - 6) % 3 == 0))
                    { // base level to start adding stats is 6
                        ChangeBaseStat(CharacterClass.TertiaryStat, 1);
                    }
                }
            }

            //CharacterClass.OnLevelUp(this, previouslevel);
            //GameServer.ServerRules.OnPlayerLevelUp(this, previouslevel);

            RefreshSpecDependantSkills(false);

            // Echostorm - Code for display of new title on level up
            // Get old and current rank titles
            //string currenttitle = CharacterClass.GetTitle(this, Level);

            // check for difference
            //if (CharacterClass.GetTitle(this, previouslevel) != currenttitle)
            //{
            //    // Inform player of new title.
            //    //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnLevelUp.AttainedRank", currenttitle), eChatType.CT_Important, eChatLoc.CL_SystemWindow);
            //}

            if (IsAlive)
            {
                // workaround for starting regeneration
                StartHealthRegeneration();
                StartPowerRegeneration();
            }

            DeathCount = 0;

            if (Group != null)
            {
                Group.UpdateGroupWindow();
            }

            // update color on levelup
            if (ObjectState == eObjectState.Active)
            {
                foreach (GamePlayer player in GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
                {
                    if (player == null)
                        continue;

                    player.Out.SendEmoteAnimation(this, eEmote.LvlUp);
                }
            }

            Emote(eEmote.LvlUp);

            // Level up pets and subpets
            if (ServerProperties.Properties.PET_LEVELS_WITH_OWNER &&
                ControlledBrain is ControlledNpcBrain brain && brain.Body is GameSummonedPet pet)
            {
                if (pet.SetPetLevel())
                {
                    if (ServerProperties.Properties.PET_SCALE_SPELL_MAX_LEVEL > 0 && pet.Spells.Count > 0)
                        pet.SortSpells();

                    brain.UpdatePetWindow();
                }

                // subpets
                if (pet.ControlledNpcList != null)
                    foreach (ABrain subBrain in pet.ControlledNpcList)
                        if (subBrain != null && subBrain.Body is GameSummonedPet subPet)
                            if (subPet.SetPetLevel()) // Levels up subpet
                                if (ServerProperties.Properties.PET_SCALE_SPELL_MAX_LEVEL > 0)
                                    subPet.SortSpells();
            }

            // save player to database
            //SaveIntoDatabase();
        }

        /// <summary>
        /// Called when this player reaches second stage of the current level
        /// </summary>
        public virtual void OnLevelSecondStage()
        {
            IsLevelSecondStage = true;

            // spec points
            m_leftOverSpecPoints += CharacterClass.SpecPointsMultiplier * Level / 20;

            //death penalty reset on mini-ding
            DeathCount = 0;

            if (Group != null)
                Group.UpdateGroupWindow();

            Emote(eEmote.LvlUp);

            if (ObjectState == eObjectState.Active)
            {
                foreach (GamePlayer player in GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
                {
                    if (player == null)
                        continue;

                    player.Out.SendEmoteAnimation(this, eEmote.LvlUp);
                }
            }

            SpendSpecPoints(Level, false);
            RefreshSpecDependantSkills(false);
        }

        /// <summary>
        /// Calculate the Autotrain points.
        /// </summary>
        /// <param name="spec">Specialization</param>
        /// <param name="mode">various AT related calculations (amount of points, level of AT...)</param>
        public virtual int GetAutoTrainPoints(Specialization spec, int Mode)
        {
            int max_autotrain = Level / 4;
            if (max_autotrain == 0) max_autotrain = 1;

            foreach (string autotrainKey in CharacterClass.GetAutotrainableSkills())
            {
                if (autotrainKey == spec.KeyName)
                {
                    switch (Mode)
                    {
                        case 0:// return sum of all AT points in the spec
                            {
                                int pts_to_refund = Math.Min(max_autotrain, spec.Level);
                                return ((pts_to_refund * (pts_to_refund + 1) - 2) / 2);
                            }
                        case 1: // return max AT level + message
                            {
                                if (Level % 4 == 0)
                                    if (spec.Level >= max_autotrain)
                                        return max_autotrain;
                                //else
                                //    Out.SendDialogBox(eDialogCode.SimpleWarning, 0, 0, 0, 0, eDialogType.Ok, true, LanguageMgr.GetTranslation(Client.Account.Language, "PlayerClass.OnLevelUp.Autotrain", spec.Name, max_autotrain));
                                return 0;
                            }
                        case 2: // return next free points due to AT change on levelup
                            {
                                if (spec.Level < max_autotrain)
                                    return (spec.Level + 1);
                                else
                                    return 0;
                            }
                        case 3: // return sum of all free AT points
                            {
                                if (spec.Level < max_autotrain)
                                    return (((max_autotrain * (max_autotrain + 1) - 2) / 2) - ((spec.Level * (spec.Level + 1) - 2) / 2));
                                else
                                    return ((max_autotrain * (max_autotrain + 1) - 2) / 2);
                            }
                        case 4: // spec is autotrainable
                            {
                                return 1;
                            }
                    }
                }
            }
            return 0;
        }
        #endregion

        #region Combat

        /// <summary>
        /// Gets/Sets safety flag
        /// (delegate to PlayerCharacter)
        /// </summary>
        public virtual bool SafetyFlag
        {
            get { return DBCharacter != null ? DBCharacter.SafetyFlag : false; }
            set { if (DBCharacter != null) DBCharacter.SafetyFlag = value; }
        }

        /// <summary>
        /// Sets/gets the living's cloak hood state
        /// (delegate to PlayerCharacter)
        /// </summary>
        public override bool IsCloakHoodUp
        {
            get { return base.IsCloakHoodUp; }
            set
            {
                base.IsCloakHoodUp = value;

                BroadcastLivingEquipmentUpdate();
            }
        }

        /// <summary>
        /// Sets/gets the living's cloak visible state
        /// (delegate to PlayerCharacter)
        /// </summary>
        public override bool IsCloakInvisible
        {
            get
            {
                return DBCharacter != null ? DBCharacter.IsCloakInvisible : base.IsCloakInvisible;
            }
            set
            {
                //DBCharacter.IsCloakInvisible = value;

                //Out.SendInventoryItemsUpdate(null);
                UpdateEquipmentAppearance();

                //if (value)
                //{
                //    Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.IsCloakInvisible.Invisible"), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                //}
                //else
                //{
                //    Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.IsCloakInvisible.Visible"), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                //}
            }
        }

        /// <summary>
        /// Sets/gets the living's helm visible state
        /// (delegate to PlayerCharacter)
        /// </summary>
        public override bool IsHelmInvisible
        {
            get
            {
                return DBCharacter != null ? DBCharacter.IsHelmInvisible : base.IsHelmInvisible;
            }
            set
            {
                //DBCharacter.IsHelmInvisible = value;

                //Out.SendInventoryItemsUpdate(null);
                UpdateEquipmentAppearance();

                //if (value)
                //{
                //    Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.IsHelmInvisible.Invisible"), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                //}
                //else
                //{
                //    Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.IsHelmInvisible.Visible"), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                //}
            }
        }

        /// <summary>
        /// Gets or sets the players SpellQueue option
        /// (delegate to PlayerCharacter)
        /// </summary>
        public virtual bool SpellQueue
        {
            get { return DBCharacter != null ? DBCharacter.SpellQueue : false; }
            set { if (DBCharacter != null) DBCharacter.SpellQueue = value; }
        }

        /// <summary>
        /// Switches the active weapon to another one
        /// </summary>
        /// <param name="slot">the new eActiveWeaponSlot</param>
        public override void SwitchWeapon(eActiveWeaponSlot slot)
        {
            //When switching weapons, attackmode is removed!
            if (attackComponent != null && attackComponent.AttackState && ActiveWeapon != null)
            {
                if (ActiveWeapon.Item_Type == (int)eInventorySlot.DistanceWeapon
                    && rangeAttackComponent.RangedAttackState != eRangedAttackState.None
                    && GameLoop.GameLoopTime - this.TempProperties.GetProperty<long>(RangeAttackComponent.RANGED_ATTACK_START) > 100
                    && attackComponent.attackAction != null)
                {
                    attackComponent.attackAction.StartTime = 1000;
                }
                attackComponent.StopAttack();
            }

            if (effectListComponent.ContainsEffectForEffectType(eEffect.Volley))
            {
                AtlasOF_VolleyECSEffect volley = (AtlasOF_VolleyECSEffect)EffectListService.GetEffectOnTarget(this, eEffect.Volley);

                if (volley != null)
                    volley.OnPlayerSwitchedWeapon();
            }

            if (CurrentSpellHandler != null)
            {
                //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.SwitchWeapon.SpellCancelled"), eChatType.CT_SpellResisted, eChatLoc.CL_SystemWindow);
                StopCurrentSpellcast();
            }

            foreach (Spell spell in ActivePulseSpells.Values)
            {
                if (spell.InstrumentRequirement != 0)
                {
                    // Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.SwitchWeapon.SpellCancelled"), eChatType.CT_SpellResisted, eChatLoc.CL_SystemWindow);
                    EffectService.RequestImmediateCancelConcEffect(EffectListService.GetPulseEffectOnTarget(this, spell));
                }
            }

            DbInventoryItem[] oldActiveSlots = new DbInventoryItem[4];
            DbInventoryItem[] newActiveSlots = new DbInventoryItem[4];
            DbInventoryItem rightHandSlot = Inventory.GetItem(eInventorySlot.RightHandWeapon);
            DbInventoryItem leftHandSlot = Inventory.GetItem(eInventorySlot.LeftHandWeapon);
            DbInventoryItem twoHandSlot = Inventory.GetItem(eInventorySlot.TwoHandWeapon);
            DbInventoryItem distanceSlot = Inventory.GetItem(eInventorySlot.DistanceWeapon);

            // save old active weapons
            // simple active slot logic:
            // 0=right hand, 1=left hand, 2=two-hand, 3=range, F=none
            switch (VisibleActiveWeaponSlots & 0x0F)
            {
                case 0: oldActiveSlots[0] = rightHandSlot; break;
                case 2: oldActiveSlots[2] = twoHandSlot; break;
                case 3: oldActiveSlots[3] = distanceSlot; break;
            }

            if ((VisibleActiveWeaponSlots & 0xF0) == 0x10)
                oldActiveSlots[1] = leftHandSlot;

            base.SwitchWeapon(slot);

            // save new active slots
            switch (VisibleActiveWeaponSlots & 0x0F)
            {
                case 0: newActiveSlots[0] = rightHandSlot; break;
                case 2: newActiveSlots[2] = twoHandSlot; break;
                case 3: newActiveSlots[3] = distanceSlot; break;
            }

            if ((VisibleActiveWeaponSlots & 0xF0) == 0x10)
                newActiveSlots[1] = leftHandSlot;

            // unequip changed items
            for (int i = 0; i < 4; i++)
            {
                if (oldActiveSlots[i] != null && newActiveSlots[i] == null)
                    Notify(PlayerInventoryEvent.ItemUnequipped, Inventory, new ItemUnequippedArgs(oldActiveSlots[i], oldActiveSlots[i].SlotPosition));
            }

            // equip new active items
            for (int i = 0; i < 4; i++)
            {
                if (newActiveSlots[i] != null && oldActiveSlots[i] == null)
                    Notify(PlayerInventoryEvent.ItemEquipped, Inventory, new ItemEquippedArgs(newActiveSlots[i], newActiveSlots[i].SlotPosition));
            }

            if (ObjectState == eObjectState.Active)
            {
                UpdateEquipmentAppearance();
            }
        }

        /// <summary>
        /// Switches the active quiver slot to another one
        /// </summary>
        /// <param name="slot"></param>
        /// <param name="forced"></param>
        public virtual void SwitchQuiver(eActiveQuiverSlot slot, bool forced)
        {
            if (slot != eActiveQuiverSlot.None)
            {
                eInventorySlot updatedSlot = eInventorySlot.Invalid;
                if ((slot & eActiveQuiverSlot.Fourth) > 0)
                    updatedSlot = eInventorySlot.FourthQuiver;
                else if ((slot & eActiveQuiverSlot.Third) > 0)
                    updatedSlot = eInventorySlot.ThirdQuiver;
                else if ((slot & eActiveQuiverSlot.Second) > 0)
                    updatedSlot = eInventorySlot.SecondQuiver;
                else if ((slot & eActiveQuiverSlot.First) > 0)
                    updatedSlot = eInventorySlot.FirstQuiver;

                if (Inventory.GetItem(updatedSlot) != null && (rangeAttackComponent.ActiveQuiverSlot != slot || forced))
                    rangeAttackComponent.ActiveQuiverSlot = slot;
                else
                    rangeAttackComponent.ActiveQuiverSlot = eActiveQuiverSlot.None;
            }
            else
            {
                if (Inventory.GetItem(eInventorySlot.FirstQuiver) != null)
                    SwitchQuiver(eActiveQuiverSlot.First, true);
                else if (Inventory.GetItem(eInventorySlot.SecondQuiver) != null)
                    SwitchQuiver(eActiveQuiverSlot.Second, true);
                else if (Inventory.GetItem(eInventorySlot.ThirdQuiver) != null)
                    SwitchQuiver(eActiveQuiverSlot.Third, true);
                else if (Inventory.GetItem(eInventorySlot.FourthQuiver) != null)
                    SwitchQuiver(eActiveQuiverSlot.Fourth, true);
                else
                    rangeAttackComponent.ActiveQuiverSlot = eActiveQuiverSlot.None;
            }
        }

        /// <summary>
        /// This method is called at the end of the attack sequence to
        /// notify objects if they have been attacked/hit by an attack
        /// </summary>
        /// <param name="ad">information about the attack</param>
        public override void OnAttackedByEnemy(AttackData ad)
        {
            base.OnAttackedByEnemy(ad);

            if (ControlledBrain != null && ControlledBrain is ControlledNpcBrain)
            {
                var brain = (ControlledNpcBrain)ControlledBrain;
                brain.OnOwnerAttacked(ad);
            }

            switch (ad.AttackResult)
            {
                case eAttackResult.HitStyle:
                case eAttackResult.HitUnstyled:
                    {
                        if (ad.Damage == -1)
                            break;

                        // If attacked by a non-damaging spell, we should not show damage numbers.
                        // We need to check the damage on the spell here, not in the AD, since this could in theory be a damaging spell that had its damage modified to 0.
                        if (ad.AttackType == AttackData.eAttackType.Spell && ad.SpellHandler.Spell?.Damage == 0)
                            break;

                        if (IsStealthed && !effectListComponent.ContainsEffectForEffectType(eEffect.Vanish))
                        {
                            if (!(ad.AttackType == AttackData.eAttackType.Spell && ad.SpellHandler.Spell.SpellType == eSpellType.DamageOverTime))
                            {
                                Stealth(false);
                            }
                        }

                        // decrease condition of hitted armor piece
                        if (ad.ArmorHitLocation != eArmorSlot.NOTSET)
                        {
                            DbInventoryItem item = Inventory.GetItem((eInventorySlot)ad.ArmorHitLocation);

                            if (item != null)
                            {
                                TryReactiveEffect(item, ad.Attacker);

                                if (item is GameInventoryItem)
                                {
                                    (item as GameInventoryItem).OnStruckByEnemy(this, ad.Attacker);
                                }
                            }
                        }
                        break;
                    }
                case eAttackResult.Blocked:
                    {
                        DbInventoryItem reactiveItem = Inventory.GetItem(eInventorySlot.LeftHandWeapon);
                        if (reactiveItem != null && reactiveItem.Object_Type == (int)eObjectType.Shield)
                        {
                            TryReactiveEffect(reactiveItem, ad.Attacker);

                            if (reactiveItem is GameInventoryItem)
                            {
                                (reactiveItem as GameInventoryItem).OnStruckByEnemy(this, ad.Attacker);
                            }
                        }
                        break;
                    }
            }
            // vampiir
            if (CharacterClass is PlayerClass.ClassVampiir)
            {
                GameSpellEffect removeEffect = SpellHandler.FindEffectOnTarget(this, "VampiirSpeedEnhancement");
                if (removeEffect != null)
                    removeEffect.Cancel(false);
            }
        }


        /// <summary>
        /// Launch any reactive effect on an item
        /// </summary>
        /// <param name="reactiveItem"></param>
        /// <param name="target"></param>
        protected virtual void TryReactiveEffect(DbInventoryItem reactiveItem, GameLiving target)
        {
            if (reactiveItem != null)
            {
                int requiredLevel = reactiveItem.Template.LevelRequirement > 0 ? reactiveItem.Template.LevelRequirement : Math.Min(MaxLevel, reactiveItem.Level);

                if (requiredLevel <= Level)
                {
                    SpellLine reactiveEffectLine = SkillBase.GetSpellLine(GlobalSpellsLines.Item_Effects);

                    if (reactiveEffectLine != null)
                    {
                        if (reactiveItem.ProcSpellID != 0)
                        {
                            Spell spell = SkillBase.FindSpell(reactiveItem.ProcSpellID, reactiveEffectLine);

                            if (spell != null)
                            {
                                int chance = reactiveItem.ProcChance > 0 ? reactiveItem.ProcChance : 10;

                                if (Util.Chance(chance))
                                {
                                    ISpellHandler spellHandler = ScriptMgr.CreateSpellHandler(this, spell, reactiveEffectLine);
                                    if (spellHandler != null)
                                    {
                                        spellHandler.StartSpell(target, reactiveItem);
                                    }
                                }
                            }
                        }

                        if (reactiveItem.ProcSpellID1 != 0)
                        {
                            Spell spell = SkillBase.FindSpell(reactiveItem.ProcSpellID1, reactiveEffectLine);

                            if (spell != null)
                            {
                                int chance = reactiveItem.ProcChance > 0 ? reactiveItem.ProcChance : 10;

                                if (Util.Chance(chance))
                                {
                                    ISpellHandler spellHandler = ScriptMgr.CreateSpellHandler(this, spell, reactiveEffectLine);
                                    if (spellHandler != null)
                                    {
                                        spellHandler.StartSpell(target, reactiveItem);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Does needed interrupt checks and interrupts if needed
        /// </summary>
        /// <param name="attacker">the attacker that is interrupting</param>
        /// <param name="attackType">The attack type</param>
        /// <returns>true if interrupted successfully</returns>
        protected override bool CheckRangedAttackInterrupt(GameLiving attacker, AttackData.eAttackType attackType)
        {
            if (base.CheckRangedAttackInterrupt(attacker, attackType))
            {
                attackComponent.attackAction?.OnAimInterrupt(attacker);
                return true;
            }

            return false;
        }

        public override void TakeDamage(GameObject source, eDamageType damageType, int damageAmount, int criticalAmount)
        {

            //if (this.DuelTarget != null && source != this.DuelTarget)
            //    this.DuelStop();

            #region PVP DAMAGE

            if ((source is GamePlayer || source is MimicNPC) || (source is GameNPC && (source as GameNPC).Brain is IControlledBrain && ((source as GameNPC).Brain as IControlledBrain).GetPlayerOwner() != null) || source is GameSiegeWeapon)
            {
                if (Realm != source.Realm && source.Realm != 0)
                    DamageRvRMemory += (long)(damageAmount + criticalAmount);
            }

            #endregion PVP DAMAGE

            base.TakeDamage(source, damageType, damageAmount, criticalAmount);

            if (this.HasAbility("Defensive Combat Power Regeneration"))
            {
                this.Mana += (int)((damageAmount + criticalAmount) * 0.25);
            }
        }

        /// <summary>
        /// Gets the effective AF of this living.  This is used for the overall AF display
        /// on the character but not used in any damage equations.
        /// </summary>
        public override int EffectiveOverallAF
        {
            get
            {
                int eaf = 0;
                int abs = 0;
                foreach (DbInventoryItem item in Inventory.VisibleItems)
                {
                    double factor = 0;
                    switch (item.Item_Type)
                    {
                        case Slot.TORSO:
                            factor = 2.2;
                            break;
                        case Slot.LEGS:
                            factor = 1.3;
                            break;
                        case Slot.ARMS:
                            factor = 0.75;
                            break;
                        case Slot.HELM:
                            factor = 0.5;
                            break;
                        case Slot.HANDS:
                            factor = 0.25;
                            break;
                        case Slot.FEET:
                            factor = 0.25;
                            break;
                    }

                    int itemAFCap = Level << 1;
                    if (RealmLevel > 39)
                        itemAFCap += 2;
                    switch ((eObjectType)item.Object_Type)
                    {
                        case eObjectType.Cloth:
                            abs = 0;
                            itemAFCap >>= 1;
                            break;
                        case eObjectType.Leather:
                            abs = 10;
                            break;
                        case eObjectType.Reinforced:
                            abs = 19;
                            break;
                        case eObjectType.Studded:
                            abs = 19;
                            break;
                        case eObjectType.Scale:
                            abs = 27;
                            break;
                        case eObjectType.Chain:
                            abs = 27;
                            break;
                        case eObjectType.Plate:
                            abs = 34;
                            break;
                    }

                    if (factor > 0)
                    {
                        int af = item.DPS_AF;
                        if (af > itemAFCap)
                            af = itemAFCap;
                        double piece_eaf = af * item.Quality / 100.0 * item.ConditionPercent / 100.0 * (1 + abs / 100.0);
                        eaf += (int)(piece_eaf * factor);
                    }
                }

                // Overall AF CAP = 10 * level * (1 + abs%/100)
                int bestLevel = -1;
                bestLevel = Math.Max(bestLevel, GetAbilityLevel("AlbArmor"));
                bestLevel = Math.Max(bestLevel, GetAbilityLevel("HibArmor"));
                bestLevel = Math.Max(bestLevel, GetAbilityLevel("MidArmor"));

                switch (bestLevel)
                {
                    default: abs = 0; break; // cloth etc
                    case ArmorLevel.Leather: abs = 10; break;
                    case ArmorLevel.Studded: abs = 19; break;
                    case ArmorLevel.Chain: abs = 27; break;
                    case ArmorLevel.Plate: abs = 34; break;
                }

                eaf += BaseBuffBonusCategory[(int)eProperty.ArmorFactor]; // base buff before cap
                int eafcap = (int)(10 * Level * (1 + abs * 0.01));
                if (eaf > eafcap)
                    eaf = eafcap;
                eaf += (int)Math.Min(Level * 1.875, SpecBuffBonusCategory[(int)eProperty.ArmorFactor])
                       - DebuffCategory[(int)eProperty.ArmorFactor]
                       + BuffBonusCategory4[(int)eProperty.ArmorFactor]
                       + Math.Min(Level, ItemBonus[(int)eProperty.ArmorFactor]);

                eaf = (int)(eaf * BuffBonusMultCategory1.Get((int)eProperty.ArmorFactor));

                return eaf;
            }
        }

        /// <summary>
        /// Calc Armor hit location when player is hit by enemy
        /// </summary>
        /// <returns>slotnumber where enemy hits</returns>
        /// attackdata(ad) changed
        public virtual eArmorSlot CalculateArmorHitLocation(AttackData ad)
        {
            if (ad.Style != null)
            {
                if (ad.Style.ArmorHitLocation != eArmorSlot.NOTSET)
                    return ad.Style.ArmorHitLocation;
            }

            int chancehit = Util.Random(1, 100);

            if (chancehit <= 40)
            {
                return eArmorSlot.TORSO;
            }
            else if (chancehit <= 65)
            {
                return eArmorSlot.LEGS;
            }
            else if (chancehit <= 80)
            {
                return eArmorSlot.ARMS;
            }
            else if (chancehit <= 90)
            {
                return eArmorSlot.HEAD;
            }
            else if (chancehit <= 95)
            {
                return eArmorSlot.HAND;
            }
            else
            {
                return eArmorSlot.FEET;
            }
        }

        /// <summary>
        /// determines current weaponspeclevel
        /// </summary>
        public override int WeaponSpecLevel(DbInventoryItem weapon)
        {
            if (weapon == null)
                return 0;
            // use axe spec if left hand axe is not in the left hand slot
            if (weapon.Object_Type == (int)eObjectType.LeftAxe && weapon.SlotPosition != Slot.LEFTHAND)
                return GetObjectSpecLevel(this, eObjectType.Axe);
            // use left axe spec if axe is in the left hand slot
            if (weapon.SlotPosition == Slot.LEFTHAND
                && weapon.Object_Type == (int)eObjectType.Axe)
                return GetObjectSpecLevel(this, eObjectType.LeftAxe);
            return GetObjectSpecLevel(this, (eObjectType)weapon.Object_Type);
        }

        public int GetObjectSpecLevel(GameLiving living, eObjectType objectType)
        {
            int res = 0;

            foreach (eObjectType obj in GetCompatibleObjectTypes(objectType))
            {
                int spec = living.GetModifiedSpecLevel(SkillBase.ObjectTypeToSpec(obj));
                if (res < spec)
                    res = spec;
            }
            return res;
        }

        protected Hashtable m_compatibleObjectTypes = null;

        /// <summary>
        /// Translates object type to compatible object types based on server type
        /// </summary>
        /// <param name="objectType">The object type</param>
        /// <returns>An array of compatible object types</returns>
        protected virtual eObjectType[] GetCompatibleObjectTypes(eObjectType objectType)
        {
            if (m_compatibleObjectTypes == null)
            {
                m_compatibleObjectTypes = new Hashtable();
                m_compatibleObjectTypes[(int)eObjectType.Staff] = new eObjectType[] { eObjectType.Staff };
                m_compatibleObjectTypes[(int)eObjectType.Fired] = new eObjectType[] { eObjectType.Fired };

                m_compatibleObjectTypes[(int)eObjectType.FistWraps] = new eObjectType[] { eObjectType.FistWraps };
                m_compatibleObjectTypes[(int)eObjectType.MaulerStaff] = new eObjectType[] { eObjectType.MaulerStaff };

                //alb
                m_compatibleObjectTypes[(int)eObjectType.CrushingWeapon] = new eObjectType[] { eObjectType.CrushingWeapon, eObjectType.Blunt, eObjectType.Hammer };
                m_compatibleObjectTypes[(int)eObjectType.SlashingWeapon] = new eObjectType[] { eObjectType.SlashingWeapon, eObjectType.Blades, eObjectType.Sword, eObjectType.Axe };
                m_compatibleObjectTypes[(int)eObjectType.ThrustWeapon] = new eObjectType[] { eObjectType.ThrustWeapon, eObjectType.Piercing };
                m_compatibleObjectTypes[(int)eObjectType.TwoHandedWeapon] = new eObjectType[] { eObjectType.TwoHandedWeapon, eObjectType.LargeWeapons };
                m_compatibleObjectTypes[(int)eObjectType.PolearmWeapon] = new eObjectType[] { eObjectType.PolearmWeapon, eObjectType.CelticSpear, eObjectType.Spear };
                m_compatibleObjectTypes[(int)eObjectType.Flexible] = new eObjectType[] { eObjectType.Flexible };
                m_compatibleObjectTypes[(int)eObjectType.Longbow] = new eObjectType[] { eObjectType.Longbow };
                m_compatibleObjectTypes[(int)eObjectType.Crossbow] = new eObjectType[] { eObjectType.Crossbow };
                //TODO: case 5: abilityCheck = Abilities.Weapon_Thrown; break;

                //mid
                m_compatibleObjectTypes[(int)eObjectType.Hammer] = new eObjectType[] { eObjectType.Hammer, eObjectType.CrushingWeapon, eObjectType.Blunt };
                m_compatibleObjectTypes[(int)eObjectType.Sword] = new eObjectType[] { eObjectType.Sword, eObjectType.SlashingWeapon, eObjectType.Blades };
                m_compatibleObjectTypes[(int)eObjectType.LeftAxe] = new eObjectType[] { eObjectType.LeftAxe };
                m_compatibleObjectTypes[(int)eObjectType.Axe] = new eObjectType[] { eObjectType.Axe, eObjectType.SlashingWeapon, eObjectType.Blades }; //eObjectType.LeftAxe removed
                m_compatibleObjectTypes[(int)eObjectType.HandToHand] = new eObjectType[] { eObjectType.HandToHand };
                m_compatibleObjectTypes[(int)eObjectType.Spear] = new eObjectType[] { eObjectType.Spear, eObjectType.CelticSpear, eObjectType.PolearmWeapon };
                m_compatibleObjectTypes[(int)eObjectType.CompositeBow] = new eObjectType[] { eObjectType.CompositeBow };
                m_compatibleObjectTypes[(int)eObjectType.Thrown] = new eObjectType[] { eObjectType.Thrown };

                //hib
                m_compatibleObjectTypes[(int)eObjectType.Blunt] = new eObjectType[] { eObjectType.Blunt, eObjectType.CrushingWeapon, eObjectType.Hammer };
                m_compatibleObjectTypes[(int)eObjectType.Blades] = new eObjectType[] { eObjectType.Blades, eObjectType.SlashingWeapon, eObjectType.Sword, eObjectType.Axe };
                m_compatibleObjectTypes[(int)eObjectType.Piercing] = new eObjectType[] { eObjectType.Piercing, eObjectType.ThrustWeapon };
                m_compatibleObjectTypes[(int)eObjectType.LargeWeapons] = new eObjectType[] { eObjectType.LargeWeapons, eObjectType.TwoHandedWeapon };
                m_compatibleObjectTypes[(int)eObjectType.CelticSpear] = new eObjectType[] { eObjectType.CelticSpear, eObjectType.Spear, eObjectType.PolearmWeapon };
                m_compatibleObjectTypes[(int)eObjectType.Scythe] = new eObjectType[] { eObjectType.Scythe };
                m_compatibleObjectTypes[(int)eObjectType.RecurvedBow] = new eObjectType[] { eObjectType.RecurvedBow };

                m_compatibleObjectTypes[(int)eObjectType.Shield] = new eObjectType[] { eObjectType.Shield };
                m_compatibleObjectTypes[(int)eObjectType.Poison] = new eObjectType[] { eObjectType.Poison };
                //TODO: case 45: abilityCheck = Abilities.instruments; break;
            }

            eObjectType[] res = (eObjectType[])m_compatibleObjectTypes[(int)objectType];
            if (res == null)
                return new eObjectType[0];
            return res;
        }

        public virtual String GetWeaponSpec(DbInventoryItem weapon)
        {
            if (weapon == null)
                return null;
            // use axe spec if left hand axe is not in the left hand slot
            if (weapon.Object_Type == (int)eObjectType.LeftAxe && weapon.SlotPosition != Slot.LEFTHAND)
                return SkillBase.ObjectTypeToSpec(eObjectType.Axe);
            // use left axe spec if axe is in the left hand slot
            if (weapon.SlotPosition == Slot.LEFTHAND
                && (weapon.Object_Type == (int)eObjectType.Axe
                    || weapon.Object_Type == (int)eObjectType.Sword
                    || weapon.Object_Type == (int)eObjectType.Hammer))
                return SkillBase.ObjectTypeToSpec(eObjectType.LeftAxe);
            return SkillBase.ObjectTypeToSpec((eObjectType)weapon.Object_Type);
        }

        /// <summary>
        /// determines current weaponspeclevel
        /// </summary>
        public int WeaponBaseSpecLevel(DbInventoryItem weapon)
        {
            if (weapon == null)
                return 0;

            // use axe spec if left hand axe is not in the left hand slot
            if (weapon.Object_Type == (int)eObjectType.LeftAxe && weapon.SlotPosition != Slot.LEFTHAND)
            {
                int res = 0;

                int spec = GetBaseSpecLevel(SkillBase.ObjectTypeToSpec(eObjectType.Axe));

                if (res < spec)
                    res = spec;

                return res;

                //return GameServer.ServerRules.GetBaseObjectSpecLevel(this, eObjectType.Axe);
            }

            // use left axe spec if axe is in the left hand slot
            if (weapon.SlotPosition == Slot.LEFTHAND
                && (weapon.Object_Type == (int)eObjectType.Axe
                    || weapon.Object_Type == (int)eObjectType.Sword
                    || weapon.Object_Type == (int)eObjectType.Hammer))
                return GetBaseSpecLevel(SkillBase.ObjectTypeToSpec(eObjectType.LeftAxe));
            return GetBaseSpecLevel(SkillBase.ObjectTypeToSpec((eObjectType)weapon.Object_Type));
        }

        /// <summary>
        /// Gets the weaponskill of weapon
        /// </summary>
        public override double GetWeaponSkill(DbInventoryItem weapon)
        {
            if (weapon == null)
                return 0;

            int classBaseWeaponSkill = weapon.SlotPosition == (int)eInventorySlot.DistanceWeapon ? CharacterClass.WeaponSkillRangedBase : CharacterClass.WeaponSkillBase;
            double weaponSkill = Level * classBaseWeaponSkill / 200.0 * (1 + 0.01 * GetWeaponStat(weapon) / 2) * Effectiveness;
            return Math.Max(0, weaponSkill * GetModified(eProperty.WeaponSkill) * 0.01);
        }

        /// <summary>
        /// calculates weapon stat
        /// </summary>
        /// <param name="weapon"></param>
        /// <returns></returns>
        public override int GetWeaponStat(DbInventoryItem weapon)
        {
            if (weapon != null)
            {
                switch ((eObjectType)weapon.Object_Type)
                {
                    // DEX modifier
                    case eObjectType.Staff:
                    case eObjectType.Fired:
                    case eObjectType.Longbow:
                    case eObjectType.Crossbow:
                    case eObjectType.CompositeBow:
                    case eObjectType.RecurvedBow:
                    case eObjectType.Thrown:
                    case eObjectType.Shield:
                        return GetModified(eProperty.Dexterity);

                    // STR+DEX modifier
                    case eObjectType.ThrustWeapon:
                    case eObjectType.Piercing:
                    case eObjectType.Spear:
                    case eObjectType.Flexible:
                    case eObjectType.HandToHand:
                        return (GetModified(eProperty.Strength) + GetModified(eProperty.Dexterity)) >> 1;
                }
            }
            // STR modifier for others
            return GetModified(eProperty.Strength);
        }

        /// <summary>
        /// calculate item armor factor influenced by quality, con and duration
        /// </summary>
        /// <param name="slot"></param>
        /// <returns></returns>
        public override double GetArmorAF(eArmorSlot slot)
        {
            if (slot == eArmorSlot.NOTSET) return 0;
            DbInventoryItem item = Inventory.GetItem((eInventorySlot)slot);
            if (item == null) return 0;
            double eaf = item.DPS_AF + BaseBuffBonusCategory[(int)eProperty.ArmorFactor]; // base AF buff

            int itemAFcap = Level;
            if (RealmLevel > 39)
                itemAFcap++;
            if (item.Object_Type != (int)eObjectType.Cloth)
            {
                itemAFcap <<= 1;
            }

            eaf = Math.Min(eaf, itemAFcap);
            //eaf *= 4.67; // compensate *4.67 in damage formula

            // my test shows that qual is added after AF buff
            eaf *= item.Quality * 0.01 * item.Condition / item.MaxCondition;

            eaf += GetModified(eProperty.ArmorFactor);
            //eaf *= 4.67; // compensate *4.67 in damage formula

            /*GameSpellEffect effect = SpellHandler.FindEffectOnTarget(this, typeof(VampiirArmorDebuff));
            if (effect != null && slot == (effect.SpellHandler as VampiirArmorDebuff).Slot)
            {
                eaf -= (int)(effect.SpellHandler as VampiirArmorDebuff).Spell.Value;
            }*/
            return eaf;
        }

        /// <summary>
        /// Calculates armor absorb level
        /// </summary>
        public override double GetArmorAbsorb(eArmorSlot slot)
        {
            if (slot == eArmorSlot.NOTSET)
                return 0;

            DbInventoryItem item = Inventory.GetItem((eInventorySlot)slot);

            if (item == null)
                return 0;

            // Debuffs can't lower absorb below 0%: https://darkageofcamelot.com/article/friday-grab-bag-08302019
            return Math.Clamp((item.SPD_ABS + GetModified(eProperty.ArmorAbsorption)) * 0.01, 0, 1);
        }

        /// <summary>
        /// Weaponskill thats shown to the player
        /// </summary>
        public virtual int DisplayedWeaponSkill
        {
            get
            {
                int itemBonus = WeaponSpecLevel(ActiveWeapon) - WeaponBaseSpecLevel(ActiveWeapon) - RealmLevel / 10;
                double m = 0.56 + itemBonus / 70.0;
                double weaponSpec = WeaponSpecLevel(ActiveWeapon) + itemBonus * m;
                double oldWStoNewWSScalar = (3 + .02 * GetWeaponStat(ActiveWeapon)) / (1 + .005 * GetWeaponStat(ActiveWeapon));
                return (int)(GetWeaponSkill(ActiveWeapon) * (1.00 + weaponSpec * 0.01) * oldWStoNewWSScalar);
            }
        }

        //// <summary>
        /// Gets the weapondamage of currently used weapon
        /// Used to display weapon damage in stats, 16.5dps = 1650
        /// </summary>
        /// <param name="weapon">the weapon used for attack</param>
        public override double WeaponDamage(DbInventoryItem weapon)
        {
            if (weapon == null)
                return 0;

            return ApplyWeaponQualityAndConditionToDamage(weapon, WeaponDamageWithoutQualityAndCondition(weapon));
        }

        public double WeaponDamageWithoutQualityAndCondition(DbInventoryItem weapon)
        {
            if (weapon == null)
                return 0;

            double Dps = weapon.DPS_AF;

            // Apply dps cap before quality and condition.
            // http://www.classesofcamelot.com/faq.asp?mode=view&cat=10
            int dpsCap = 12 + 3 * Level;

            if (RealmLevel > 39)
                dpsCap += 3;

            if (Dps > dpsCap)
                Dps = dpsCap;

            Dps *= 1 + GetModified(eProperty.DPS) * 0.01;
            return Dps * 0.1;
        }

        public double ApplyWeaponQualityAndConditionToDamage(DbInventoryItem weapon, double damage)
        {
            return damage * weapon.Quality * 0.01 * weapon.Condition / weapon.MaxCondition;
        }

        public override bool CanCastWhileAttacking()
        {
            switch (CharacterClass)
            {
                case ClassVampiir:
                case ClassMaulerAlb:
                case ClassMaulerMid:
                case ClassMaulerHib:
                    return true;
            }

            return false;
        }

        public override void OnAttackEnemy(AttackData ad)
        {
            //Console.WriteLine(string.Format("OnAttack called on {0}", this.Name));

            // Note that this function is called whenever an attack is made, regardless of whether that attack was successful.
            // i.e. missed melee swings and resisted spells still trigger this.

            if (effectListComponent is null)
                return;

            Stealth(false);

            //Cancel SpeedOfTheRealm (Hastener Speed) 
            if (effectListComponent.Effects.ContainsKey(eEffect.MovementSpeedBuff))
            {
                var effects = effectListComponent.GetSpellEffects(eEffect.MovementSpeedBuff);

                for (int i = 0; i < effects.Count; i++)
                {
                    if (effects[i] is null)
                        continue;

                    var spellEffect = effects[i] as ECSGameSpellEffect;
                    if (spellEffect != null && spellEffect.Name.ToLower().Equals("speed of the realm"))
                    {
                        EffectService.RequestImmediateCancelEffect(effects[i]);
                    }
                }
            }

            if (ad != null && ad.Damage > 0)
                TryCancelMovementSpeedBuffs(true);

            var oProcEffects = effectListComponent.GetSpellEffects(eEffect.OffensiveProc);
            //OffensiveProcs
            if (ad != null && ad.Attacker == this && oProcEffects != null && ad.AttackType != AttackData.eAttackType.Spell && ad.AttackResult != eAttackResult.Missed)
            {
                for (int i = 0; i < oProcEffects.Count; i++)
                {
                    var oProcEffect = oProcEffects[i];

                    (oProcEffect.SpellHandler as OffensiveProcSpellHandler).EventHandler(ad);
                }
            }
            DirtyTricksECSGameEffect dt = (DirtyTricksECSGameEffect)EffectListService.GetAbilityEffectOnTarget(this, eEffect.DirtyTricks);
            if (dt != null)
            {
                foreach (GamePlayer player in GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
                    player.Out.SendSpellEffectAnimation(this, ad.Target, 471, 0, false, 1);

                dt.EventHandler(ad);
            }
            TripleWieldECSGameEffect tw = (TripleWieldECSGameEffect)EffectListService.GetAbilityEffectOnTarget(this, eEffect.TripleWield);
            if (tw != null)
            {
                tw.EventHandler(ad);
            }

            if (ad.Target is GamePlayer || ad.Target is MimicNPC && ad.Target != this)
            {
                LastAttackTickPvP = GameLoop.GameLoopTime;
            }
            else
            {
                LastAttackTickPvE = GameLoop.GameLoopTime;
            }

            //if (this is GameNPC npc)
            //{
            //    var brain = npc.Brain as ControlledNpcBrain;

            //    if (ad.Target is GamePlayer)
            //    {
            //        LastAttackTickPvP = GameLoop.GameLoopTime;
            //        if (brain != null)
            //            brain.Owner.LastAttackedByEnemyTickPvP = GameLoop.GameLoopTime;
            //    }
            //    else
            //    {
            //        LastAttackTickPvE = GameLoop.GameLoopTime;
            //        if (brain != null)
            //            brain.Owner.LastAttackedByEnemyTickPvE = GameLoop.GameLoopTime;
            //    }
            //}

            // Don't cancel offensive focus spell
            if (ad.AttackType != eAttackType.Spell)
                CancelFocusSpell();
        }

        /// <summary>
        /// Stores the amount of realm points gained by other players on last death
        /// </summary>
        protected long m_lastDeathRealmPoints;

        /// <summary>
        /// Gets/sets the amount of realm points gained by other players on last death
        /// </summary>
        public long LastDeathRealmPoints
        {
            get { return m_lastDeathRealmPoints; }
            set { m_lastDeathRealmPoints = value; }
        }

        /// <summary>
        /// Method to broadcast Player to Discord
        /// </summary>
        /// <param name="message">The message</param>
        /// <param name="realm">The realm</param>
        public static void BroadcastDeathOnDiscord(string message, string name, string lastname, string playerClass, int level, long playedTime)
        {
            int color = 0;
            TimeSpan timeLived = TimeSpan.FromSeconds(playedTime);
            string timeLivedString = timeLived.Days + "d " + timeLived.Hours + "h " + timeLived.Minutes + "m ";

            string playerName = "";
            if (lastname != "")
                playerName = name + " " + lastname;
            else
                playerName = name;

            var DiscordObituaryHook =
                "https://discord.com/api/webhooks/929154632389910558/kfJbtzDC9JzyOXvZ0rYUwaPM31LRUebGzDZKSczUKDk_4YyHmB-WJVsh7pJoa4M9-D1U"; // Make it a property later
            var client = new DiscordWebhookClient(DiscordObituaryHook);

            // Create your DiscordMessage with all parameters of your message.
            var discordMessage = new DiscordMessage(
                "",
                username: "Atlas Obituary",
                avatarUrl: "https://cdn.discordapp.com/attachments/919610633656369214/928726197645496382/skull2.png",
                tts: false,
                embeds: new[]
                {
                    new DiscordMessageEmbed(
                        author: new DiscordMessageEmbedAuthor(playerName),
                        color: color,
                        description: message,
                        fields: new[]
                        {
                            new DiscordMessageEmbedField("Level", level.ToString()),
                            new DiscordMessageEmbedField("Class", playerClass),
                            new DiscordMessageEmbedField("Time alive", timeLivedString)
                        }
                    )
                }
            );
            client.SendToDiscord(discordMessage);
        }

        /// <summary>
        /// Called when the player dies
        /// </summary>
        /// <param name="killer">the killer</param>
        public override void ProcessDeath(GameObject killer)
        {
            // Ambient trigger upon killing player
            if (killer is GameNPC)
                (killer as GameNPC).FireAmbientSentence(GameNPC.eAmbientTrigger.killing, killer as GameLiving);

            CharacterClass.Die(killer);

            bool realmDeath = killer != null && killer.Realm != eRealm.None && killer.Realm != Realm;

            TargetObject = null;

            string playerMessage;
            string publicMessage;
            ushort messageDistance = WorldMgr.DEATH_MESSAGE_DISTANCE;
            m_releaseType = eReleaseType.Normal;

            string location = "";
            if (CurrentAreas.Count > 0 && (CurrentAreas[0] is Area.BindArea) == false)
                location = (CurrentAreas[0] as AbstractArea).Description;
            else
                location = CurrentZone.Description;

            if (killer == null)
            {
                if (realmDeath)
                {
                    playerMessage = LanguageMgr.GetTranslation("EN", "GamePlayer.Die.KilledLocation", GetName(0, true), location);
                    publicMessage = LanguageMgr.GetTranslation("EN", "GamePlayer.Die.KilledLocation", GetName(0, true), location);
                }
                else
                {
                    playerMessage = LanguageMgr.GetTranslation("EN", "GamePlayer.Die.Killed", GetName(0, true));
                    publicMessage = LanguageMgr.GetTranslation("EN", "GamePlayer.Die.Killed", GetName(0, true));
                }
            }
            else
            {
                messageDistance = 0;
                if (realmDeath)
                {
                    if (killer is MimicNPC mimic)
                    {
                        mimic.Kills++;
                        mimic.KillStreak++;
                    }

                    KillStreak = 0;

                    switch (CurrentRegionID)
                    {
                        // Thid
                        case 252: MimicBattlegrounds.ThidBattleground.UpdateBattleStats(this); break;
                        // Cathal
                        case 165: MimicBattlegrounds.CathalBattleground.UpdateBattleStats(this); break;
                    }

                    playerMessage = LanguageMgr.GetTranslation("EN", "GamePlayer.Die.KilledByLocation", GetName(0, true), killer.GetName(1, false), location);
                    publicMessage = LanguageMgr.GetTranslation("EN", "GamePlayer.Die.KilledByLocation", GetName(0, true), killer.GetName(1, false), location);
                }
                else
                {
                    playerMessage = LanguageMgr.GetTranslation("EN", "GamePlayer.Die.KilledBy", GetName(0, true), killer.GetName(1, false));
                    publicMessage = LanguageMgr.GetTranslation("EN", "GamePlayer.Die.KilledBy", GetName(0, true), killer.GetName(1, false));
                }
            }

            //DuelStop();

            eChatType messageType;
            if (m_releaseType == eReleaseType.Duel)
                messageType = eChatType.CT_Emote;
            else if (killer == null)
            {
                messageType = eChatType.CT_PlayerDied;
            }
            else
            {
                switch (killer.Realm)
                {
                    case eRealm.Albion: messageType = eChatType.CT_KilledByAlb; break;
                    case eRealm.Midgard: messageType = eChatType.CT_KilledByMid; break;
                    case eRealm.Hibernia: messageType = eChatType.CT_KilledByHib; break;
                    default: messageType = eChatType.CT_PlayerDied; break; // killed by mob
                }
            }

            if (killer is GamePlayer && killer != this)
            {
                ((GamePlayer)killer).Out.SendMessage(LanguageMgr.GetTranslation(((GamePlayer)killer).Client.Account.Language, "GamePlayer.Die.YouKilled", GetName(0, false)), eChatType.CT_PlayerDied, eChatLoc.CL_SystemWindow);
                ((GamePlayer)killer).Out.SendMessage(playerMessage, messageType, eChatLoc.CL_SystemWindow);
            }

            List<GamePlayer> players;

            if (messageDistance == 0)
                players = ClientService.GetPlayersOfRegion(CurrentRegion);
            else
                players = GetPlayersInRadius(messageDistance);

            foreach (GamePlayer player in players)
            {
                // on normal server type send messages only to the killer and dead players realm
                // check for gameplayer is needed because killers realm don't see deaths by guards
                if (player.Realm == Realm
                    || (ServerProperties.Properties.DEATH_MESSAGES_ALL_REALMS && (killer is GamePlayer || killer is MimicNPC || killer is GameKeepGuard))) //Only show Player/Guard kills if shown to all realms

                    player.Out.SendMessage(publicMessage, messageType, eChatLoc.CL_SystemWindow);
            }

            IsSitting = false;
            IsSwimming = false;

            //if (ControlledBrain != null)
            //    CommandNpcRelease();

            // then buffs drop messages
            //GameLivingProcessDeath(killer);

            //BRENT ADDED
            // if (Group != null)
            //     Group.RemoveMember(this);
            base.ProcessDeath(killer);

            //lock (m_LockObject)
            //{
            //if (m_releaseTimer != null)
            //{
            //    m_releaseTimer.Stop();
            //    m_releaseTimer = null;
            //}

            //if (m_healthRegenerationTimer != null)
            //{
            //    m_healthRegenerationTimer.Stop();
            //    m_healthRegenerationTimer = null;
            //}

            //m_automaticRelease = m_releaseType == eReleaseType.Duel;
            //m_releasePhase = 0;
            //m_deathTick = GameLoop.GameLoopTime; // we use realtime, because timer window is realtime

            //m_releaseTimer = new ECSGameTimer(this);
            //m_releaseTimer.Callback = new ECSGameTimer.ECSTimerCallback(ReleaseTimerCallback);
            //m_releaseTimer.Start(1000);


            // clear target object so no more actions can used on this target, spells, styles, attacks...
            //TargetObject = null;

            //// first penalty is 5% of expforlevel, second penalty comes from release
            //int xpLossPercent;
            //if (Level < 40)
            //{
            //    xpLossPercent = MaxLevel - Level;
            //}
            //else
            //{
            //    xpLossPercent = MaxLevel - 40;
            //}

            //if (realmDeath || killer?.Realm == Realm) //Live PvP servers have 3 con loss on pvp death, can be turned off in server properties -Unty
            //{
            //    int conpenalty = 0;
            //    switch (GameServer.Instance.Configuration.ServerType)
            //    {
            //        case EGameServerType.GST_Normal:
            //        xpLossPercent = 0;
            //        m_deathtype = eDeathType.RvR;
            //        break;

            //        case EGameServerType.GST_PvP:
            //       // Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Die.DeadRVR"), eChatType.CT_YouDied, eChatLoc.CL_SystemWindow);
            //        xpLossPercent = 0;
            //        m_deathtype = eDeathType.PvP;
            //        if (ServerProperties.Properties.PVP_DEATH_CON_LOSS)
            //        {
            //            conpenalty = 3;
            //            TempProperties.SetProperty(DEATH_CONSTITUTION_LOSS_PROPERTY, conpenalty);
            //        }
            //        break;
            //    }
            //}
            //else
            //{
            //    if (Level >= ServerProperties.Properties.PVE_EXP_LOSS_LEVEL)
            //    {
            //        //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Die.LoseExperience"), eChatType.CT_YouDied, eChatLoc.CL_SystemWindow);
            //        // if this is the first death in level, you lose only half the penalty
            //        switch (DeathCount)
            //        {
            //            case 0:
            //            //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Die.DeathN1"), eChatType.CT_YouDied, eChatLoc.CL_SystemWindow);
            //            xpLossPercent /= 3;
            //            break;
            //            case 1:
            //            //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Die.DeathN2"), eChatType.CT_YouDied, eChatLoc.CL_SystemWindow);
            //            xpLossPercent = xpLossPercent * 2 / 3;
            //            break;
            //        }

            //        DeathCount++;
            //        m_deathtype = eDeathType.PvE;
            //        long xpLoss = (ExperienceForNextLevel - ExperienceForCurrentLevel) * xpLossPercent / 1000;
            //        GainExperience(eXPSource.Other, -xpLoss, 0, 0, 0, false, true);
            //        //TempProperties.SetProperty(DEATH_EXP_LOSS_PROPERTY, xpLoss);
            //    }

            //    if (Level >= ServerProperties.Properties.PVE_CON_LOSS_LEVEL)
            //    {
            //        int conLoss = DeathCount;
            //        if (conLoss > 3)
            //            conLoss = 3;
            //        else if (conLoss < 1)
            //            conLoss = 1;
            //        //TempProperties.SetProperty(DEATH_CONSTITUTION_LOSS_PROPERTY, conLoss);
            //    }

            //    if (realmDeath)
            //        LastDeathPvP = true;
            //}
            //GameEventMgr.AddHandler(this, GamePlayerEvent.Revive, new DOLEventHandler(OnRevive));
            //}



            //if (this.SiegeWeapon != null)
            //    SiegeWeapon.ReleaseControl();

            // sent after buffs drop
            // GamePlayer.Die.CorpseLies:		{0} just died. {1} corpse lies on the ground.
            //Message.SystemToOthers2(this, eChatType.CT_PlayerDied, "GamePlayer.Die.CorpseLies", GetName(0, true), GetPronoun(this.Client, 1, true));

            //if (m_releaseType == eReleaseType.Duel)
            //{
            //    foreach (GamePlayer player in killer.GetPlayersInRadius(WorldMgr.INFO_DISTANCE))
            //    {
            //        if (player != killer)
            //            // Message: {0} wins the duel!
            //            player.Out.SendMessage(LanguageMgr.GetTranslation(player.Client.Account.Language, "GamePlayer.Duel.Die.KillerWinsDuel", killer.Name), eChatType.CT_Emote, eChatLoc.CL_SystemWindow);
            //    }
            //    // Message: {0} wins the duel!
            //    //Message.SystemToOthers(Client, LanguageMgr.GetTranslation(this, "GamePlayer.Duel.Die.KillerWinsDuel", killer.Name), eChatType.CT_Emote);
            //}

            // deal out exp and realm points based on server rules
            // no other way to keep correct message order...
            //GameServer.ServerRules.OnPlayerKilled(this, killer);
            //if (m_releaseType != eReleaseType.Duel)
            //    DeathTime = PlayedTime;

            //CancelAllConcentrationEffects();
            //effectListComponent.CancelAll();
        }

        // Needed to skip over GameNPC ProcessDeath
        public virtual void GameLivingProcessDeath(GameObject killer)
        {
            try
            {
                attackComponent.StopAttack();

                if (killer is GameLiving livingKiller)
                    attackComponent.Attackers.TryAdd(livingKiller, long.MaxValue);

                List<GamePlayer> playerAttackers = new();

                foreach (GameObject attacker in attackComponent.Attackers.Keys)
                {
                    if (attacker is not GameLiving livingAttacker)
                        continue;

                    GamePlayer player = attacker as GamePlayer;

                    if (attacker is GameNPC npcAttacker && npcAttacker.Brain is IControlledBrain npcAttackerBrain)
                    {
                        // Ok, we're a pet - if our Player owner isn't in the attacker list, let's make them a 'virtual' attacker
                        player = npcAttackerBrain.GetPlayerOwner();

                        if (player != null)
                        {
                            if (!attackComponent.Attackers.ContainsKey(player))
                            {
                                if (!playerAttackers.Contains(player))
                                    playerAttackers.Add(player);
                            }

                            // Pet gets the killed message as well
                            livingAttacker.EnemyKilled(this);
                        }
                    }

                    if (player != null)
                    {
                        if (!playerAttackers.Contains(player))
                            playerAttackers.Add(player);

                        if (player.Group != null)
                        {
                            foreach (GamePlayer groupPlayer in player.Group.GetPlayersInTheGroup())
                            {
                                if (groupPlayer.IsWithinRadius(this, WorldMgr.MAX_EXPFORKILL_DISTANCE) && playerAttackers.Contains(groupPlayer) == false)
                                    playerAttackers.Add(groupPlayer);
                            }
                        }
                    }
                    else
                        livingAttacker.EnemyKilled(this);
                }

                foreach (GamePlayer player in playerAttackers)
                    player.EnemyKilled(this);

                foreach (Quests.DataQuest q in DataQuestList)
                    q.Notify(GameLivingEvent.Dying, this, new DyingEventArgs(killer, playerAttackers));

                attackComponent.Attackers.Clear();

                // clear all of our targets
                rangeAttackComponent.AutoFireTarget = null;
                TargetObject = null;

                // cancel all left effects
                EffectList.CancelAll();
                effectListComponent.CancelAll();

                // Stop the regeneration timers
                StopHealthRegeneration();
                StopPowerRegeneration();
                StopEnduranceRegeneration();

                //Reduce health to zero
                Health = 0;

                // Remove all last attacked times
                LastAttackedByEnemyTickPvE = 0;
                LastAttackedByEnemyTickPvP = 0;

                //Let's send the notification at the end
                Notify(GameLivingEvent.Dying, this, new DyingEventArgs(killer));
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }
            finally
            {
                //isDying flag is ALWAYS set to false even if exception happens so it can get remove from the list
                isDeadOrDying = false;
            }
        }

        /// <summary>
		/// Starts the Respawn Timer
		/// </summary>
		public override void StartRespawn()
        {
            if (IsAlive)
                return;

            if (m_healthRegenerationTimer != null)
            {
                m_healthRegenerationTimer.Stop();
                m_healthRegenerationTimer = null;
            }

            int respawnInt = RespawnInterval;
            int minBound = (int)Math.Floor(respawnInt * .95);
            int maxBound = (int)Math.Floor(respawnInt * 1.05);
            respawnInt = Util.Random(minBound, maxBound);
            if (respawnInt > 0)
            {
                lock (m_respawnTimerLock)
                {
                    if (m_respawnTimer == null)
                    {
                        m_respawnTimer = new AuxECSGameTimer(this);
                        m_respawnTimer.Callback = new AuxECSGameTimer.AuxECSTimerCallback(RespawnTimerCallback);
                    }
                    else if (m_respawnTimer.IsAlive)
                    {
                        m_respawnTimer.Stop();
                    }
                    // register Mob as "respawning"
                    CurrentRegion.MobsRespawning.TryAdd(this, respawnInt);

                    m_respawnTimer.Start(respawnInt);
                }
            }
        }

        /// <summary>
        /// The callback that will respawn this mob
        /// </summary>
        /// <param name="respawnTimer">the timer calling this callback</param>
        /// <returns>the new interval</returns>
        protected override int RespawnTimerCallback(AuxECSGameTimer respawnTimer)
        {
            CurrentRegion.MobsRespawning.TryRemove(this, out _);

            lock (m_respawnTimerLock)
            {
                if (m_respawnTimer != null)
                {
                    m_respawnTimer.Stop();
                    m_respawnTimer = null;
                }
            }

            if (IsAlive || ObjectState == eObjectState.Active)
                return 0;

            /*
			if (m_level >= 5 && m_databaseLevel < 60)
			{
				int minBound = (int) Math.Round(m_databaseLevel * .9);
				int maxBound = (int) Math.Round(m_databaseLevel * 1.1);
				this.Level = (byte)  Util.Random(minBound, maxBound);
			}*/

            SpawnTick = GameLoop.GameLoopTime;

            // Heal this NPC and move it to the spawn location.
            Health = MaxHealth;
            Mana = MaxMana;
            Endurance = MaxEndurance;

            int origSpawnX = m_spawnPoint.X;
            int origSpawnY = m_spawnPoint.Y;
            X = m_spawnPoint.X;
            Y = m_spawnPoint.Y;
            Z = m_spawnPoint.Z;
            Heading = m_spawnHeading;
            AddToWorld();
            m_spawnPoint.X = origSpawnX;
            m_spawnPoint.Y = origSpawnY;

            // Delay the first think tick a bit to prevent clients from sending positive LoS check
            // when they shouldn't, which can happen right after 'SendNPCCreate' and makes mobs aggro through walls.
            Brain.LastThinkTick = GameLoop.GameLoopTime + 1250;
            return 0;
        }

        public override void EnemyKilled(GameLiving enemy)
        {
            if (Group != null)
            {
                foreach (GameLiving living in Group.GetMembersInTheGroup())
                {
                    if (living == this)
                        continue;

                    if (enemy.attackComponent.Attackers.ContainsKey(living))
                        continue;

                    if (IsWithinRadius(living, WorldMgr.MAX_EXPFORKILL_DISTANCE))
                        Notify(GameLivingEvent.EnemyKilled, living, new EnemyKilledEventArgs(enemy));
                }
            }

            if (CurrentZone.IsRvR)
            {
                var activeConquest = ConquestService.ConquestManager.ActiveObjective;
                int baseContribution = enemy.RealmPointsValue / 2; //todo turn it into a server prop?

                if (activeConquest != null && this.GetDistance(new Point2D(activeConquest.Keep.X, activeConquest.Keep.Y)) <=
                    ServerProperties.Properties.MAX_CONQUEST_RANGE)
                {
                    //TODO: add something here
                    if (Group != null)
                    {
                        //activeConquest.Contribute(this, (baseContribution/Group.MemberCount) + 20); //offset to minimize the grouping penalty by a bit
                    }
                    else
                    {
                        //activeConquest.Contribute(this, baseContribution); 
                    }
                }
            }

            base.EnemyKilled(enemy);
        }


        /// <summary>
        /// Check this flag to see wether this living is involved in combat
        /// </summary>
        public override bool InCombat
        {
            get
            {
                IControlledBrain npc = ControlledBrain;
                if (npc != null && npc.Body.InCombat)
                    return true;
                return base.InCombat;
            }
        }

        /// <summary>
        /// Easy method to get the resist of a certain damage type
        /// Good for when we add RAs
        /// </summary>
        /// <param name="property"></param>
        /// <returns></returns>
        public int GetDamageResist(eProperty property)
        {
            int res = 0;
            int classResist = 0;
            int secondResist = 0;

            //Q: Do the Magic resist bonuses from Bedazzling Aura and Empty Mind stack with each other?
            //A: Nope.
            switch ((eResist)property)
            {
                case eResist.Body:
                case eResist.Cold:
                case eResist.Energy:
                case eResist.Heat:
                case eResist.Matter:
                case eResist.Spirit:
                    res += BaseBuffBonusCategory[(int)eProperty.MagicAbsorption];
                    break;
                default:
                    break;
            }
            return (int)((res + classResist) - 0.01 * secondResist * (res + classResist) + secondResist);
        }

        #endregion

        #region Stealth / Wireframe

        bool m_isWireframe = false;

        /// <summary>
        /// Player is drawn as a Wireframe.  Not sure why or when this is used.  -- Tolakram
        /// </summary>
        //public bool IsWireframe
        //{
        //    get { return m_isWireframe; }
        //    set
        //    {
        //        bool needUpdate = m_isWireframe != value;
        //        m_isWireframe = value;
        //        if (needUpdate && ObjectState == eObjectState.Active)
        //            foreach (GamePlayer player in GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
        //            {
        //                if (player == null) continue;
        //                player.Out.SendPlayerModelTypeChange(this, (byte)(value ? 1 : 0));
        //            }
        //    }
        //}

        private bool m_isTorchLighted = false;

        /// <summary>
        /// Is player Torch lighted ?
        /// </summary>
        public bool IsTorchLighted
        {
            get { return m_isTorchLighted; }
            set { m_isTorchLighted = value; }
        }

        /// <summary>
        /// Property that holds tick when stealth state was changed last time
        /// </summary>
        public const string STEALTH_CHANGE_TICK = "StealthChangeTick";
        /// <summary>
        /// The stealth state of this player
        /// </summary>
        public override bool IsStealthed
        {
            get { return effectListComponent.ContainsEffectForEffectType(eEffect.Stealth); }
        }

        public static void Unstealth(DOLEvent ev, object sender, EventArgs args)
        {
            AttackedByEnemyEventArgs atkArgs = args as AttackedByEnemyEventArgs;
            GamePlayer player = sender as GamePlayer;
            if (player == null || atkArgs == null) return;
            if (atkArgs.AttackData.AttackResult != eAttackResult.HitUnstyled && atkArgs.AttackData.AttackResult != eAttackResult.HitStyle) return;
            if (atkArgs.AttackData.Damage == -1) return;

            player.Stealth(false);
        }
        /// <summary>
        /// Set player's stealth state
        /// </summary>
        /// <param name="goStealth">true is stealthing, false if unstealthing</param>
        public virtual void Stealth(bool goStealth)
        {
            if (IsStealthed == goStealth)
                return;

            //if (goStealth && CraftTimer != null && CraftTimer.IsAlive)
            //{
            //    //Out.SendMessage("You can't stealth while crafting!", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            //    return;
            //}

            if (this.effectListComponent.ContainsEffectForEffectType(eEffect.Pulse))
            {
                //Out.SendMessage("You currently have an active, pulsing spell effect and cannot hide!", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return;
            }

            //if (IsOnHorse || IsSummoningMount)
            //    IsOnHorse = false;

            if (goStealth)
            {
                new StealthECSGameEffect(new ECSGameEffectInitParams(this, 0, 1));
            }
            else
            {
                if (effectListComponent.ContainsEffectForEffectType(eEffect.Stealth))
                {
                    EffectService.RequestImmediateCancelEffect(EffectListService.GetEffectOnTarget(this, eEffect.Stealth), false);
                }
                if (effectListComponent.ContainsEffectForEffectType(eEffect.Vanish))
                    EffectService.RequestImmediateCancelEffect(EffectListService.GetEffectOnTarget(this, eEffect.Vanish));
            }
        }

        // UncoverStealthAction is what unstealths player if they are too close to mobs.
        public void StartStealthUncoverAction()
        {
            MimicUncoverStealthAction action = TempProperties.GetProperty<MimicUncoverStealthAction>(UNCOVER_STEALTH_ACTION_PROP, null);
            //start the uncover timer
            if (action == null)
                action = new MimicUncoverStealthAction(this);
            action.Interval = 1000;
            action.Start(1000);
            TempProperties.SetProperty(UNCOVER_STEALTH_ACTION_PROP, action);
        }

        // UncoverStealthAction is what unstealths player if they are too close to mobs.
        public void StopStealthUncoverAction()
        {
            MimicUncoverStealthAction action = TempProperties.GetProperty<MimicUncoverStealthAction>(UNCOVER_STEALTH_ACTION_PROP, null);
            //stop the uncover timer
            if (action != null)
            {
                action.Stop();
                TempProperties.RemoveProperty(UNCOVER_STEALTH_ACTION_PROP);
            }
        }

        /// <summary>
        /// The temp property that stores the uncover stealth action
        /// </summary>
        protected const string UNCOVER_STEALTH_ACTION_PROP = "UncoverStealthAction";

        /// <summary>
        /// Uncovers the player if a mob is too close
        /// </summary>
        protected class MimicUncoverStealthAction : ECSGameTimerWrapperBase
        {
            /// <summary>
            /// Constructs a new uncover stealth action
            /// </summary>
            /// <param name="actionSource">The action source</param>
            public MimicUncoverStealthAction(MimicNPC actionSource) : base(actionSource) { }

            /// <summary>
            /// Called on every timer tick
            /// </summary>
            protected override int OnTick(ECSGameTimer timer)
            {
                MimicNPC mimic = (MimicNPC)timer.Owner;

                foreach (GameNPC npc in mimic.GetNPCsInRadius(1024))
                {
                    // Friendly mobs do not uncover stealthed players
                    if (!GameServer.ServerRules.IsAllowedToAttack(npc, mimic, true))
                        continue;

                    // Npc with player owner don't uncover
                    if (npc.Brain != null
                        && (npc.Brain as IControlledBrain) != null
                        && (npc.Brain as IControlledBrain).GetPlayerOwner() != null)
                        continue;

                    double npcLevel = Math.Max(npc.Level, 1.0);
                    double stealthLevel = mimic.GetModifiedSpecLevel(Specs.Stealth);
                    double detectRadius = 125.0 + ((npcLevel - stealthLevel) * 20.0);

                    // we have detect hidden and enemy don't = higher range
                    if (npc.HasAbility("Detect Hidden") && EffectListService.GetAbilityEffectOnTarget(mimic, eEffect.Camouflage) == null)
                        detectRadius += 125;

                    if (detectRadius < 126) detectRadius = 126;

                    double distanceToPlayer = npc.GetDistanceTo(mimic);

                    if (distanceToPlayer > detectRadius)
                        continue;

                    double fieldOfView = 90.0;  //90 degrees  = standard FOV
                    double fieldOfListen = 120.0; //120 degrees = standard field of listening

                    if (npc.Level > 50)
                        fieldOfListen += (npc.Level - mimic.Level) * 3;

                    double angle = npc.GetAngle(mimic);

                    //player in front
                    fieldOfView /= 2.0;
                    bool canSeePlayer = (angle >= 360 - fieldOfView || angle < fieldOfView);

                    //If npc can not see nor hear the player, continue the loop
                    fieldOfListen /= 2.0;
                    if (canSeePlayer == false &&
                        !(angle >= (45 + 60) - fieldOfListen && angle < (45 + 60) + fieldOfListen) &&
                        !(angle >= (360 - 45 - 60) - fieldOfListen && angle < (360 - 45 - 60) + fieldOfListen))
                        continue;

                    double chanceMod = 1.0;

                    //Chance to detect player decreases after 125 coordinates!
                    if (distanceToPlayer > 125)
                        chanceMod = 1f - (distanceToPlayer - 125.0) / (detectRadius - 125.0);

                    double chanceToUncover = 0.1 + (npc.Level - stealthLevel) * 0.01 * chanceMod;
                    if (chanceToUncover < 0.01) chanceToUncover = 0.01;

                    if (Util.ChanceDouble(chanceToUncover))
                    {
                        if (!canSeePlayer)
                            npc.TurnTo(mimic, 10000);
                        //    mimic.Out.SendCheckLOS(mimic, npc, new CheckLOSResponse(mimic.UncoverLOSHandler));
                        //else
                    }
                }

                return Interval;
            }
        }
        /// <summary>
        /// This handler is called by the unstealth check of mobs
        /// </summary>
        public void UncoverLOSHandler(GamePlayer player, ushort response, ushort targetOID)
        {
            GameObject target = CurrentRegion.GetObject(targetOID);

            if ((target == null) || (player.IsStealthed == false))
                return;

            if ((response & 0x100) == 0x100)
            {
                player.Out.SendMessage(target.GetName(0, true) + " uncovers you!", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                player.Stealth(false);
            }
        }

        /// <summary>
        /// Checks whether this player can detect stealthed enemy
        /// </summary>
        /// <param name="enemy"></param>
        /// <returns>true if enemy can be detected</returns>
        public virtual bool CanDetect(GamePlayer enemy)
        {
            if (enemy.CurrentRegionID != CurrentRegionID)
                return false;
            if (!IsAlive)
                return false;
            if (enemy.EffectList.GetOfType<VanishEffect>() != null)
                return false;
            if (this.Client.Account.PrivLevel > 1)
                return true;
            if (enemy.Client.Account.PrivLevel > 1)
                return false;

            if (this.effectListComponent.ContainsEffectForEffectType(eEffect.TrueSight))
                return true;

            if (HasAbilityType(typeof(AtlasOF_SeeHidden))
                && (enemy.CharacterClass is ClassMinstrel
                     || enemy.CharacterClass is ClassRanger
                     || enemy.CharacterClass is ClassHunter
                     || enemy.CharacterClass is ClassScout)
                && this.IsWithinRadius(enemy, 650)
                && !enemy.effectListComponent.ContainsEffectForEffectType(eEffect.Camouflage))
            {
                return true;
            }

            /*
             * http://www.critshot.com/forums/showthread.php?threadid=3142
             * The person doing the looking has a chance to find them based on their level, minus the stealthed person's stealth spec.
             *
             * -Normal detection range = (enemy lvl  your stealth spec) * 20 + 125
             * -Detect Hidden Range = (enemy lvl  your stealth spec) * 50 + 250
             * -See Hidden range = 2700 - (38 * your stealth spec)
             */

            int EnemyStealthLevel = enemy.GetModifiedSpecLevel(Specs.Stealth);
            if (EnemyStealthLevel > 50)
                EnemyStealthLevel = 50;
            int levelDiff = this.Level - EnemyStealthLevel;
            if (levelDiff < 0) levelDiff = 0;

            int range = 0;
            bool enemyHasCamouflage = EffectListService.GetAbilityEffectOnTarget(enemy, eEffect.Camouflage) != null;
            bool enemyHasVanish = EffectListService.GetAbilityEffectOnTarget(enemy, eEffect.Vanish) != null;
            if (HasAbility("Detect Hidden") && !enemyHasVanish && !enemyHasCamouflage)
            {
                // we have detect hidden and enemy don't = higher range
                range = levelDiff * 50 + 250; // Detect Hidden advantage
                //range = levelDiff * 50 + 300; // Detect Hidden advantage
            }
            else
            {
                //range = levelDiff * 20 + 125; // Normal detection range
                range = levelDiff * 20 + 125;
            }

            //if (ConquestService.ConquestManager.IsPlayerNearFlag(this))
            //{
            //    range += 50;
            //}

            // Mastery of Stealth Bonus
            /*
             //removed, this is NF MoStealth. OF Version does not add range, only movespeed
            RAPropertyEnhancer mos = GetAbility<MasteryOfStealthAbility>();
            if (mos != null && !enemyHasCamouflage)
                if (!HasAbility(Abilities.DetectHidden) || !enemy.HasAbility(Abilities.DetectHidden))
                    range += mos.GetAmountForLevel(CalculateSkillLevel(mos));
            */
            range += BaseBuffBonusCategory[(int)eProperty.Skill_Stealth];

            // //Buff (Stealth Detection)
            // //Increases the target's ability to detect stealthed players and monsters.
            // GameSpellEffect iVampiirEffect = SpellHandler.FindEffectOnTarget((GameLiving)this, "VampiirStealthDetection");
            // if (iVampiirEffect != null)
            //     range += (int)iVampiirEffect.Spell.Value;
            //
            // //Infill Only - Greater Chance to Detect Stealthed Enemies for 1 minute
            // //after executing a klling blow on a realm opponent.
            // GameSpellEffect HeightenedAwareness = SpellHandler.FindEffectOnTarget((GameLiving)this, "HeightenedAwareness");
            // if (HeightenedAwareness != null)
            //     range += (int)HeightenedAwareness.Spell.Value;
            //
            // //Nightshade Only - Greater chance of remaining hidden while stealthed for 1 minute
            // //after executing a killing blow on a realm opponent.
            // GameSpellEffect SubtleKills = SpellHandler.FindEffectOnTarget((GameLiving)enemy, "SubtleKills");
            // if (SubtleKills != null)
            // {
            //     range -= (int)SubtleKills.Spell.Value;
            //     if (range < 0) range = 0;
            // }
            //
            // // Apply Blanket of camouflage effect
            // GameSpellEffect iSpymasterEffect1 = SpellHandler.FindEffectOnTarget((GameLiving)enemy, "BlanketOfCamouflage");
            // if (iSpymasterEffect1 != null)
            // {
            //     range -= (int)iSpymasterEffect1.Spell.Value;
            //     if (range < 0) range = 0;
            // }
            //
            // // Apply Lookout effect
            // GameSpellEffect iSpymasterEffect2 = SpellHandler.FindEffectOnTarget((GameLiving)this, "Loockout");
            // if (iSpymasterEffect2 != null)
            //     range += (int)iSpymasterEffect2.Spell.Value;
            //
            // // Apply Prescience node effect
            // GameSpellEffect iConvokerEffect = SpellHandler.FindEffectOnTarget((GameLiving)enemy, "Prescience");
            // if (iConvokerEffect != null)
            //     range += (int)iConvokerEffect.Spell.Value;

            //Hard cap is 1900
            if (range > 1900)
                range = 1900;
            //everyone can see your own group stealthed
            else if (enemy.Group != null && Group != null && enemy.Group == Group)
            {
                range = 2500;
            }

            // Fin
            // vampiir stealth range, uncomment when add eproperty stealthrange i suppose
            return this.IsWithinRadius(enemy, range);
        }

        #endregion

        #region Equipment/Encumberance

        /// <summary>
        /// Gets the total possible Encumberance
        /// </summary>
        public virtual int MaxEncumberance
        {
            get
            {
                double enc = (double)Strength;
                RAPropertyEnhancer ab = GetAbility<AtlasOF_LifterAbility>();
                if (ab != null)
                    enc *= 1 + ((double)ab.Amount / 100);

                return (int)enc;
            }
        }

        /// <summary>
        /// Gets the current Encumberance
        /// </summary>
        public virtual int Encumberance
        {
            get { return Inventory.InventoryWeight; }
        }

        /// <summary>
        /// The Encumberance state of this player
        /// </summary>
        protected bool m_overencumbered = true;

        /// <summary>
        /// Gets/Set the players Encumberance state
        /// </summary>
        public bool IsOverencumbered
        {
            get { return m_overencumbered; }
            set { m_overencumbered = value; }
        }

        /// <summary>
        /// Updates the appearance of the equipment this player is using
        /// </summary>
        public virtual void UpdateEquipmentAppearance()
        {
            foreach (GamePlayer player in GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
            {
                if (player == null)
                    continue;

                player.Out.SendLivingEquipmentUpdate(this);
            }
        }

        /// <summary>
        /// Updates Encumberance and its effects
        /// </summary>
        public void UpdateEncumberance()
        {
            if (Inventory.InventoryWeight > MaxEncumberance)
            {
                //IsOverencumbered = true;
                // Out.SendUpdateMaxSpeed();
                //   if (MaxSpeed == 0)
                //   Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "PropertyCalc.MaxSpeed.YouAreEncumbered"), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                // else
                // Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.UpdateEncumberance.EncumberedMoveSlowly"), eChatType.CT_System, eChatLoc.CL_SystemWindow);
            }
            else if (IsOverencumbered)
            {
                //IsOverencumbered = false;
                //Out.SendUpdateMaxSpeed();
            }
            //Out.SendEncumberance();
        }

        public override void UpdateHealthManaEndu()
        {
            //Out.SendCharStatsUpdate();
            //Out.SendUpdateWeaponAndArmorStats();
            //UpdateEncumberance();
            //UpdatePlayerStatus();
            base.UpdateHealthManaEndu();
        }

        /// <summary>
        /// Get the bonus names
        /// </summary>
        public string ItemBonusName(int BonusType)
        {
            string BonusName = "";

            if (BonusType == 1) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus1");//Strength
            if (BonusType == 2) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus2");//Dexterity
            if (BonusType == 3) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus3");//Constitution
            if (BonusType == 4) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus4");//Quickness
            if (BonusType == 5) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus5");//Intelligence
            if (BonusType == 6) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus6");//Piety
            if (BonusType == 7) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus7");//Empathy
            if (BonusType == 8) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus8");//Charisma
            if (BonusType == 9) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus9");//Power
            if (BonusType == 10) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus10");//Hits
            if (BonusType == 11) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus11");//Body
            if (BonusType == 12) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus12");//Cold
            if (BonusType == 13) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus13");//Crush
            if (BonusType == 14) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus14");//Energy
            if (BonusType == 15) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus15");//Heat
            if (BonusType == 16) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus16");//Matter
            if (BonusType == 17) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus17");//Slash
            if (BonusType == 18) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus18");//Spirit
            if (BonusType == 19) BonusName = LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.ItemBonusName.Bonus19");//Thrust
            return BonusName;
        }

        /// <summary>
        /// Adds magical bonuses whenever item was equipped
        /// </summary>
        /// <param name="e"></param>
        /// <param name="sender">inventory</param>
        /// <param name="arguments"></param>
        protected virtual void OnItemEquipped(DOLEvent e, object sender, EventArgs arguments)
        {
            if (arguments is ItemEquippedArgs == false) return;
            DbInventoryItem item = ((ItemEquippedArgs)arguments).Item;
            if (item == null) return;

            if (item is IGameInventoryItem)
            {
                //(item as IGameInventoryItem).OnEquipped(this);
            }

            //if (item.Item_Type >= Slot.RIGHTHAND && item.Item_Type <= Slot.RANGED)
            //{
            //    if (item.Hand == 1) // 2h
            //    {
            //        Out.SendMessage(string.Format(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnItemEquipped.WieldBothHands", item.GetName(0, false))), eChatType.CT_System, eChatLoc.CL_SystemWindow);
            //    }
            //    else if (item.SlotPosition == Slot.LEFTHAND)
            //    {
            //        Out.SendMessage(string.Format(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnItemEquipped.WieldLeftHand", item.GetName(0, false))), eChatType.CT_System, eChatLoc.CL_SystemWindow);
            //    }
            //    else
            //    {
            //        Out.SendMessage(string.Format(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnItemEquipped.WieldRightHand", item.GetName(0, false))), eChatType.CT_System, eChatLoc.CL_SystemWindow);
            //    }
            //}

            //if (item.Item_Type == (int)eInventorySlot.Horse)
            //{
            //    if (item.SlotPosition == Slot.HORSE)
            //    {
            //        ActiveHorse.ID = (byte)(item.SPD_ABS == 0 ? 1 : item.SPD_ABS);
            //        ActiveHorse.Name = item.Creator;
            //    }
            //    return;
            //}
            //else if (item.Item_Type == (int)eInventorySlot.HorseArmor)
            //{
            //    if (item.SlotPosition == Slot.HORSEARMOR)
            //    {
            //        //					Out.SendDebugMessage("Try apply horse armor.");
            //        ActiveHorse.Saddle = (byte)(item.DPS_AF);
            //    }
            //    return;
            //}
            //else if (item.Item_Type == (int)eInventorySlot.HorseBarding)
            //{
            //    if (item.SlotPosition == Slot.HORSEBARDING)
            //    {
            //        //					Out.SendDebugMessage("Try apply horse barding.");
            //        ActiveHorse.Barding = (byte)(item.DPS_AF);
            //    }
            //    return;
            //}

            if (!item.IsMagical) return;

            // Out.SendMessage(string.Format(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnItemEquipped.Magic", item.GetName(0, false))), eChatType.CT_Skill, eChatLoc.CL_SystemWindow);

            if (item.Bonus1 != 0)
            {
                ItemBonus[item.Bonus1Type] += item.Bonus1;
                // if (item.Bonus1Type < 20)
                // Out.SendMessage(string.Format(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnItemEquipped.Increased", ItemBonusName(item.Bonus1Type))), eChatType.CT_Skill, eChatLoc.CL_SystemWindow);
            }
            if (item.Bonus2 != 0)
            {
                ItemBonus[item.Bonus2Type] += item.Bonus2;
                //if (item.Bonus2Type < 20)
                // Out.SendMessage(string.Format(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnItemEquipped.Increased", ItemBonusName(item.Bonus2Type))), eChatType.CT_Skill, eChatLoc.CL_SystemWindow);
            }
            if (item.Bonus3 != 0)
            {
                ItemBonus[item.Bonus3Type] += item.Bonus3;
                // if (item.Bonus3Type < 20)
                // Out.SendMessage(string.Format(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnItemEquipped.Increased", ItemBonusName(item.Bonus3Type))), eChatType.CT_Skill, eChatLoc.CL_SystemWindow);
            }
            if (item.Bonus4 != 0)
            {
                ItemBonus[item.Bonus4Type] += item.Bonus4;
                //if (item.Bonus4Type < 20)
                // Out.SendMessage(string.Format(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnItemEquipped.Increased", ItemBonusName(item.Bonus4Type))), eChatType.CT_Skill, eChatLoc.CL_SystemWindow);
            }
            if (item.Bonus5 != 0)
            {
                ItemBonus[item.Bonus5Type] += item.Bonus5;
                // if (item.Bonus5Type < 20)
                // Out.SendMessage(string.Format(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnItemEquipped.Increased", ItemBonusName(item.Bonus5Type))), eChatType.CT_Skill, eChatLoc.CL_SystemWindow);
            }
            if (item.Bonus6 != 0)
            {
                ItemBonus[item.Bonus6Type] += item.Bonus6;
                //  if (item.Bonus6Type < 20)
                //  Out.SendMessage(string.Format(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnItemEquipped.Increased", ItemBonusName(item.Bonus6Type))), eChatType.CT_Skill, eChatLoc.CL_SystemWindow);
            }
            if (item.Bonus7 != 0)
            {
                ItemBonus[item.Bonus7Type] += item.Bonus7;
                //   if (item.Bonus7Type < 20)
                // Out.SendMessage(string.Format(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnItemEquipped.Increased", ItemBonusName(item.Bonus7Type))), eChatType.CT_Skill, eChatLoc.CL_SystemWindow);
            }
            if (item.Bonus8 != 0)
            {
                ItemBonus[item.Bonus8Type] += item.Bonus8;
                //   if (item.Bonus8Type < 20)
                // Out.SendMessage(string.Format(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnItemEquipped.Increased", ItemBonusName(item.Bonus8Type))), eChatType.CT_Skill, eChatLoc.CL_SystemWindow);
            }
            if (item.Bonus9 != 0)
            {
                ItemBonus[item.Bonus9Type] += item.Bonus9;
                // if (item.Bonus9Type < 20)
                // Out.SendMessage(string.Format(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnItemEquipped.Increased", ItemBonusName(item.Bonus9Type))), eChatType.CT_Skill, eChatLoc.CL_SystemWindow);
            }
            if (item.Bonus10 != 0)
            {
                ItemBonus[item.Bonus10Type] += item.Bonus10;
                // if (item.Bonus10Type < 20)
                // Out.SendMessage(string.Format(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnItemEquipped.Increased", ItemBonusName(item.Bonus10Type))), eChatType.CT_Skill, eChatLoc.CL_SystemWindow);
            }
            if (item.ExtraBonus != 0)
            {
                ItemBonus[item.ExtraBonusType] += item.ExtraBonus;
                //if (item.ExtraBonusType < 20)
                //Out.SendMessage(string.Format(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnItemEquipped.Increased", ItemBonusName(item.ExtraBonusType))), eChatType.CT_Skill, eChatLoc.CL_SystemWindow);
            }
            //Check null on client.player bypass region change
            if (Client.Account.PrivLevel == (uint)ePrivLevel.Player && Client.Player != null && Client.Player.ObjectState == eObjectState.Active)
                if (item.SpellID > 0 || item.SpellID1 > 0)
                    TempProperties.SetProperty("ITEMREUSEDELAY" + item.Id_nb, CurrentRegion.Time);

            /*
            //max 2 charges
            if (item.SpellID > 0 && SelfBuffChargeIDs.Contains(item.SpellID) && LoyaltyManager.GetPlayerRealmLoyalty(this).Days > 30)
            {
                if(ActiveBuffCharges < 2)
                    UseItemCharge(item, (int)eUseType.use1);
                else
                {
                    Out.SendMessage("You may only use two buff charge effects. This item fails to affect you.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                }
            }

            //max 2 charges
            if (item.SpellID1 > 0 && SelfBuffChargeIDs.Contains(item.SpellID1) && LoyaltyManager.GetPlayerRealmLoyalty(this).Days > 30)
            {
                if(ActiveBuffCharges < 2)
                    UseItemCharge(item, (int)eUseType.use2);
                else
                {
                    Out.SendMessage("You may only use two buff charge effects. This item fails to affect you.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                }
            }*/

            if (ObjectState == eObjectState.Active)
            {
                // TODO: remove when properties system is finished
                //Out.SendCharStatsUpdate();
                //Out.SendCharResistsUpdate();
                //Out.SendUpdateWeaponAndArmorStats();
                //Out.SendUpdateMaxSpeed();
                //Out.SendEncumberance();
                //Out.SendUpdatePlayerSkills();
                //UpdatePlayerStatus();


                if (IsAlive)
                {
                    if (Health < MaxHealth) StartHealthRegeneration();
                    else if (Health > MaxHealth) Health = MaxHealth;

                    if (Mana < MaxMana) StartPowerRegeneration();
                    else if (Mana > MaxMana) Mana = MaxMana;

                    if (Endurance < MaxEndurance) StartEnduranceRegeneration();
                    else if (Endurance > MaxEndurance) Endurance = MaxEndurance;
                }
            }
        }

        private int m_activeBuffCharges = 0;

        public int ActiveBuffCharges
        {
            get
            {
                return m_activeBuffCharges;
            }
            set
            {
                m_activeBuffCharges = value;
            }
        }

        private List<int> m_selfBuffIds;
        public List<int> SelfBuffChargeIDs
        {
            get
            {
                if (m_selfBuffIds == null)
                {
                    m_selfBuffIds = new List<int>();
                    m_selfBuffIds.Add(31133); //str/con charge
                    m_selfBuffIds.Add(31132); //dex/qui charge
                    m_selfBuffIds.Add(31131); //acuity charge
                    m_selfBuffIds.Add(31130); //AF charge
                }

                return m_selfBuffIds;
            }
        }

        /// <summary>
        /// Removes magical bonuses whenever item was unequipped
        /// </summary>
        /// <param name="e"></param>
        /// <param name="sender">inventory</param>
        /// <param name="arguments"></param>
        protected virtual void OnItemUnequipped(DOLEvent e, object sender, EventArgs arguments)
        {
            if (arguments is ItemUnequippedArgs == false) return;
            DbInventoryItem item = ((ItemUnequippedArgs)arguments).Item;
            int prevSlot = (int)((ItemUnequippedArgs)arguments).PreviousSlotPosition;
            if (item == null) return;

            //if (item.Item_Type >= Slot.RIGHTHAND && item.Item_Type <= Slot.RANGED)
            //{
            //    if (item.Hand == 1) // 2h
            //    {
            //        Out.SendMessage(string.Format(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnItemUnequipped.BothHandsFree", item.GetName(0, false))), eChatType.CT_Skill, eChatLoc.CL_SystemWindow);
            //    }
            //    else if (prevSlot == Slot.LEFTHAND)
            //    {
            //        Out.SendMessage(string.Format(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnItemUnequipped.LeftHandFree", item.GetName(0, false))), eChatType.CT_Skill, eChatLoc.CL_SystemWindow);
            //    }
            //    else
            //    {
            //        Out.SendMessage(string.Format(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnItemUnequipped.RightHandFree", item.GetName(0, false))), eChatType.CT_Skill, eChatLoc.CL_SystemWindow);
            //    }
            //}

            if (item.Item_Type == Slot.RANGED && (rangeAttackComponent.RangedAttackState == eRangedAttackState.Aim
               || rangeAttackComponent.RangedAttackState == eRangedAttackState.AimFire ||
               rangeAttackComponent.RangedAttackState == eRangedAttackState.AimFireReload ||
               rangeAttackComponent.RangedAttackState == eRangedAttackState.ReadyToFire
               )) attackComponent.attackAction = null;

            if (prevSlot == Slot.MYTHICAL && item.Item_Type == (int)eInventorySlot.Mythical && item is GameMythirian)
            {
                //(item as GameMythirian).OnUnEquipped(this);
            }

            //if (item.Item_Type == (int)eInventorySlot.Horse)
            //{
            //    if (IsOnHorse)
            //        IsOnHorse = false;
            //    ActiveHorse.ID = 0;
            //    ActiveHorse.Name = "";
            //    //				Out.SendDebugMessage("Try unapply horse.");
            //    return;
            //}
            //else if (item.Item_Type == (int)eInventorySlot.HorseArmor)
            //{
            //    ActiveHorse.Saddle = 0;
            //    //				Out.SendDebugMessage("Try unapply saddle.");
            //    return;
            //}
            //else if (item.Item_Type == (int)eInventorySlot.HorseBarding)
            //{
            //    ActiveHorse.Barding = 0;
            //    //				Out.SendDebugMessage("Try unapply barding.");
            //    return;
            //}

            // Cancel any self buffs that are unequipped
            if (item.SpellID > 0 && SelfBuffChargeIDs.Contains(item.SpellID) && Inventory.EquippedItems.Where(x => x.SpellID == item.SpellID).Count() <= 1)
                CancelChargeBuff(item.SpellID);

            if (!item.IsMagical)
                return;

            if (item.Bonus1 != 0)
            {
                ItemBonus[item.Bonus1Type] -= item.Bonus1;
                // if (item.Bonus1Type < 20)
                //  Out.SendMessage(string.Format(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnItemUnequipped.Decreased", ItemBonusName(item.Bonus1Type))), eChatType.CT_Skill, eChatLoc.CL_SystemWindow);
            }
            if (item.Bonus2 != 0)
            {
                ItemBonus[item.Bonus2Type] -= item.Bonus2;
                //  if (item.Bonus2Type < 20)
                //  Out.SendMessage(string.Format(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnItemUnequipped.Decreased", ItemBonusName(item.Bonus2Type))), eChatType.CT_Skill, eChatLoc.CL_SystemWindow);
            }
            if (item.Bonus3 != 0)
            {
                ItemBonus[item.Bonus3Type] -= item.Bonus3;
                //if (item.Bonus3Type < 20)
                //  Out.SendMessage(string.Format(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnItemUnequipped.Decreased", ItemBonusName(item.Bonus3Type))), eChatType.CT_Skill, eChatLoc.CL_SystemWindow);
            }
            if (item.Bonus4 != 0)
            {
                ItemBonus[item.Bonus4Type] -= item.Bonus4;
                // if (item.Bonus4Type < 20)
                //  Out.SendMessage(string.Format(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnItemUnequipped.Decreased", ItemBonusName(item.Bonus4Type))), eChatType.CT_Skill, eChatLoc.CL_SystemWindow);
            }
            if (item.Bonus5 != 0)
            {
                ItemBonus[item.Bonus5Type] -= item.Bonus5;
                //if (item.Bonus5Type < 20)
                // Out.SendMessage(string.Format(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnItemUnequipped.Decreased", ItemBonusName(item.Bonus5Type))), eChatType.CT_Skill, eChatLoc.CL_SystemWindow);
            }
            if (item.Bonus6 != 0)
            {
                ItemBonus[item.Bonus6Type] -= item.Bonus6;
                //if (item.Bonus6Type < 20)
                // Out.SendMessage(string.Format(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnItemUnequipped.Decreased", ItemBonusName(item.Bonus6Type))), eChatType.CT_Skill, eChatLoc.CL_SystemWindow);
            }
            if (item.Bonus7 != 0)
            {
                ItemBonus[item.Bonus7Type] -= item.Bonus7;
                // if (item.Bonus7Type < 20)
                // Out.SendMessage(string.Format(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnItemUnequipped.Decreased", ItemBonusName(item.Bonus7Type))), eChatType.CT_Skill, eChatLoc.CL_SystemWindow);
            }
            if (item.Bonus8 != 0)
            {
                ItemBonus[item.Bonus8Type] -= item.Bonus8;
                //  if (item.Bonus8Type < 20)
                // Out.SendMessage(string.Format(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnItemUnequipped.Decreased", ItemBonusName(item.Bonus8Type))), eChatType.CT_Skill, eChatLoc.CL_SystemWindow);
            }
            if (item.Bonus9 != 0)
            {
                ItemBonus[item.Bonus9Type] -= item.Bonus9;
                //if (item.Bonus9Type < 20)
                // Out.SendMessage(string.Format(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnItemUnequipped.Decreased", ItemBonusName(item.Bonus9Type))), eChatType.CT_Skill, eChatLoc.CL_SystemWindow);
            }
            if (item.Bonus10 != 0)
            {
                ItemBonus[item.Bonus10Type] -= item.Bonus10;
                //if (item.Bonus10Type < 20)
                //Out.SendMessage(string.Format(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnItemUnequipped.Decreased", ItemBonusName(item.Bonus10Type))), eChatType.CT_Skill, eChatLoc.CL_SystemWindow);
            }
            if (item.ExtraBonus != 0)
            {
                ItemBonus[item.ExtraBonusType] -= item.ExtraBonus;
                //if (item.ExtraBonusType < 20)
                //Out.SendMessage(string.Format(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.OnItemUnequipped.Decreased", ItemBonusName(item.ExtraBonusType))), eChatType.CT_Skill, eChatLoc.CL_SystemWindow);
            }

            if (item is IGameInventoryItem)
            {
                //(item as IGameInventoryItem).OnUnEquipped(this);
            }

            if (ObjectState == eObjectState.Active)
            {
                // TODO: remove when properties system is finished
                //Out.SendCharStatsUpdate();
                //Out.SendCharResistsUpdate();
                //Out.SendUpdateWeaponAndArmorStats();
                //Out.SendUpdateMaxSpeed();
                //Out.SendEncumberance();
                //Out.SendUpdatePlayerSkills();
                //UpdatePlayerStatus();

                if (IsAlive)
                {
                    if (Health < MaxHealth) StartHealthRegeneration();
                    else if (Health > MaxHealth) Health = MaxHealth;

                    if (Mana < MaxMana) StartPowerRegeneration();
                    else if (Mana > MaxMana) Mana = MaxMana;

                    if (Endurance < MaxEndurance) StartEnduranceRegeneration();
                    else if (Endurance > MaxEndurance) Endurance = MaxEndurance;
                }
            }
        }

        private void CancelChargeBuff(int spellID)
        {
            EffectService.RequestCancelEffect(effectListComponent.GetSpellEffects().FirstOrDefault(x => x.SpellHandler.Spell.ID == spellID));
        }

        public virtual void RefreshItemBonuses()
        {
            m_itemBonus = new PropertyIndexer();
            string slotToLoad = "";
            switch (VisibleActiveWeaponSlots)
            {
                case 16: slotToLoad = "rightandleftHandSlot"; break;
                case 18: slotToLoad = "leftandtwoHandSlot"; break;
                case 31: slotToLoad = "leftHandSlot"; break;
                case 34: slotToLoad = "twoHandSlot"; break;
                case 51: slotToLoad = "distanceSlot"; break;
                case 240: slotToLoad = "righttHandSlot"; break;
                case 242: slotToLoad = "twoHandSlot"; break;
                default: break;
            }

            //log.Debug("VisibleActiveWeaponSlots= " + VisibleActiveWeaponSlots);
            foreach (DbInventoryItem item in Inventory.EquippedItems)
            {
                if (item == null)
                    continue;

                // skip weapons. only active weapons should fire equip event, done in player.SwitchWeapon
                bool add = true;
                if (slotToLoad != "")
                {
                    switch (item.SlotPosition)
                    {
                        case Slot.TWOHAND:
                            if (slotToLoad.Contains("twoHandSlot") == false)
                            {
                                add = false;
                            }
                            break;

                        case Slot.RIGHTHAND:
                            if (slotToLoad.Contains("right") == false)
                            {
                                add = false;
                            }
                            break;
                        case Slot.SHIELD:
                        case Slot.LEFTHAND:
                            if (slotToLoad.Contains("left") == false)
                            {
                                add = false;
                            }
                            break;
                        case Slot.RANGED:
                            if (slotToLoad != "distanceSlot")
                            {
                                add = false;
                            }
                            break;
                        default: break;
                    }
                }

                if (!add)
                    continue;

                if (item is IGameInventoryItem)
                {
                    //(item as IGameInventoryItem).CheckValid(this);
                }

                if (item.IsMagical)
                {
                    if (item.Bonus1 != 0)
                    {
                        ItemBonus[item.Bonus1Type] += item.Bonus1;
                    }
                    if (item.Bonus2 != 0)
                    {
                        ItemBonus[item.Bonus2Type] += item.Bonus2;
                    }
                    if (item.Bonus3 != 0)
                    {
                        ItemBonus[item.Bonus3Type] += item.Bonus3;
                    }
                    if (item.Bonus4 != 0)
                    {
                        ItemBonus[item.Bonus4Type] += item.Bonus4;
                    }
                    if (item.Bonus5 != 0)
                    {
                        ItemBonus[item.Bonus5Type] += item.Bonus5;
                    }
                    if (item.Bonus6 != 0)
                    {
                        ItemBonus[item.Bonus6Type] += item.Bonus6;
                    }
                    if (item.Bonus7 != 0)
                    {
                        ItemBonus[item.Bonus7Type] += item.Bonus7;
                    }
                    if (item.Bonus8 != 0)
                    {
                        ItemBonus[item.Bonus8Type] += item.Bonus8;
                    }
                    if (item.Bonus9 != 0)
                    {
                        ItemBonus[item.Bonus9Type] += item.Bonus9;
                    }
                    if (item.Bonus10 != 0)
                    {
                        ItemBonus[item.Bonus10Type] += item.Bonus10;
                    }
                    if (item.ExtraBonus != 0)
                    {
                        ItemBonus[item.ExtraBonusType] += item.ExtraBonus;
                    }
                }
            }
        }

        /// <summary>
        /// Handles a bonus change on an item.
        /// </summary>
        /// <param name="e"></param>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        protected virtual void OnItemBonusChanged(DOLEvent e, object sender, EventArgs args)
        {
            ItemBonusChangedEventArgs changeArgs = args as ItemBonusChangedEventArgs;
            if (changeArgs == null || changeArgs.BonusType == 0 || changeArgs.BonusAmount == 0)
                return;

            ItemBonus[changeArgs.BonusType] += changeArgs.BonusAmount;

            if (ObjectState == eObjectState.Active)
            {
                //Out.SendCharStatsUpdate();
                //Out.SendCharResistsUpdate();
                //Out.SendUpdateWeaponAndArmorStats();
                //Out.SendUpdatePlayerSkills();
                //UpdatePlayerStatus();

                if (IsAlive)
                {
                    if (Health < MaxHealth) StartHealthRegeneration();
                    else if (Health > MaxHealth) Health = MaxHealth;

                    if (Mana < MaxMana) StartPowerRegeneration();
                    else if (Mana > MaxMana) Mana = MaxMana;

                    if (Endurance < MaxEndurance) StartEnduranceRegeneration();
                    else if (Endurance > MaxEndurance) Endurance = MaxEndurance;
                }
            }
        }

        #endregion

        #region Shade

        protected ShadeECSGameEffect m_ShadeEffect = null;

        /// <summary>
        /// The shade effect of this player
        /// </summary>
        public ShadeECSGameEffect ShadeEffect
        {
            get { return m_ShadeEffect; }
            set { m_ShadeEffect = value; }
        }

        /// <summary>
        /// Gets flag indication whether player is in shade mode
        /// </summary>
        public bool IsShade
        {
            get
            {
                bool shadeModel = Model == ShadeModel;
                return m_ShadeEffect != null ? true : shadeModel;
            }
        }

        /// <summary>
        /// Create a shade effect for this player.
        /// </summary>
        /// <returns></returns>
        protected virtual ShadeECSGameEffect CreateShadeEffect()
        {
            return CharacterClass.CreateShadeEffect();
        }

        /// <summary>
        /// The model ID used on character creation.
        /// </summary>
        public ushort CreationModel
        {
            get
            {
                return (ushort)m_client.Account.Characters[m_client.ActiveCharIndex].CreationModel;
            }
        }

        /// <summary>
        /// The model ID used for shade morphs.
        /// </summary>
        public ushort ShadeModel
        {
            get
            {
                // Aredhel: Bit fishy, necro in caster from could use
                // Traitor's Dagger... FIXME!

                if (CharacterClass.ID == (int)eCharacterClass.Necromancer)
                    return 822;

                switch (Race)
                {
                    // Albion Models.
                    //case (int)eRace.Inconnu: return (ushort)(DBCharacter.Gender + 1351);
                    //case (int)eRace.Briton: return (ushort)(DBCharacter.Gender + 1353);
                    //case (int)eRace.Avalonian: return (ushort)(DBCharacter.Gender + 1359);
                    //case (int)eRace.Highlander: return (ushort)(DBCharacter.Gender + 1355);
                    //case (int)eRace.Saracen: return (ushort)(DBCharacter.Gender + 1357);
                    //case (int)eRace.HalfOgre: return (ushort)(DBCharacter.Gender + 1361);

                    //// Midgard Models.
                    //case (int)eRace.Troll: return (ushort)(DBCharacter.Gender + 1363);
                    //case (int)eRace.Dwarf: return (ushort)(DBCharacter.Gender + 1369);
                    //case (int)eRace.Norseman: return (ushort)(DBCharacter.Gender + 1365);
                    //case (int)eRace.Kobold: return (ushort)(DBCharacter.Gender + 1367);
                    //case (int)eRace.Valkyn: return (ushort)(DBCharacter.Gender + 1371);
                    //case (int)eRace.Frostalf: return (ushort)(DBCharacter.Gender + 1373);

                    //// Hibernia Models.
                    //case (int)eRace.Firbolg: return (ushort)(DBCharacter.Gender + 1375);
                    //case (int)eRace.Celt: return (ushort)(DBCharacter.Gender + 1377);
                    //case (int)eRace.Lurikeen: return (ushort)(DBCharacter.Gender + 1379);
                    //case (int)eRace.Elf: return (ushort)(DBCharacter.Gender + 1381);
                    //case (int)eRace.Sylvan: return (ushort)(DBCharacter.Gender + 1383);
                    //case (int)eRace.Shar: return (ushort)(DBCharacter.Gender + 1385);

                    default: return Model;
                }
            }
        }

        /// <summary>
        /// Changes shade state of the player.
        /// </summary>
        /// <param name="state">The new state.</param>
        public virtual void Shade(bool state)
        {
            CharacterClass.Shade(state);
        }
        #endregion

        #region X/Y/Z/Region/Realm/Position...

        /// <summary>
        /// Holds all areas this player is currently within
        /// </summary>
        private ReaderWriterList<IArea> m_currentAreas = new ReaderWriterList<IArea>();

        /// <summary>
        /// Holds all areas this player is currently within
        /// </summary>
        public override IList<IArea> CurrentAreas
        {
            get { return m_currentAreas; }
            set { m_currentAreas.FreezeWhile(l => { l.Clear(); l.AddRange(value); }); }
        }

        /// <summary>
        /// Property that saves last maximum Z value
        /// </summary>
        public const string MAX_LAST_Z = "max_last_z";

        /// <summary>
        /// Gets or sets the current speed of this player
        /// </summary>
        public override short CurrentSpeed
        {
            set
            {
                base.CurrentSpeed = value;

                if (value != 0)
                    OnMimicMove();
            }
        }

        public void OnMimicMove()
        {
            if (IsSitting)
                Sit(false);
        }

        /// <summary>
        /// Gets or sets the region of this player
        /// </summary>
        public override Region CurrentRegion
        {
            set
            {
                base.CurrentRegion = value;
                if (DBCharacter != null) DBCharacter.Region = CurrentRegionID;
            }
        }

        /// <summary>
        /// Holds the players max Z for fall damage
        /// </summary>
        private int m_maxLastZ;

        /// <summary>
        /// Gets or sets the players max Z for fall damage
        /// </summary>
        public int MaxLastZ
        {
            get { return m_maxLastZ; }
            set { m_maxLastZ = value; }
        }

        /// <summary>
        /// Gets or sets the realm of this player
        /// </summary>
        public override eRealm Realm
        {
            get { return base.Realm; }
            set { base.Realm = value; }
        }

        /// <summary>
        /// Gets or sets the heading of this player
        /// </summary>
        public override ushort Heading
        {
            set
            {
                base.Heading = value;

                if (attackComponent.AttackState && ActiveWeaponSlot != eActiveWeaponSlot.Distance)
                {
                    AttackData ad = TempProperties.GetProperty<AttackData>(LAST_ATTACK_DATA, null);
                    if (ad != null && ad.IsMeleeAttack && (ad.AttackResult == eAttackResult.TargetNotVisible || ad.AttackResult == eAttackResult.OutOfRange))
                    {
                        //Does the target can be attacked ?
                        //if (ad.Target != null && IsObjectInFront(ad.Target, 120) && this.IsWithinRadius(ad.Target, AttackRange) && m_attackAction != null)
                        if (ad.Target != null && IsObjectInFront(ad.Target, 120) && this.IsWithinRadius(ad.Target, attackComponent.AttackRange) && attackComponent.attackAction != null)
                        {
                            //m_attackAction.Start(1);
                            attackComponent.attackAction.StartTime = 1;
                        }
                    }
                }
            }
        }

        protected bool m_climbing;
        /// <summary>
        /// Gets/sets the current climbing state
        /// </summary>
        public bool IsClimbing
        {
            get { return m_climbing; }
            set
            {
                if (value == m_climbing) return;
                m_climbing = value;
            }
        }

        protected bool m_swimming;
        /// <summary>
        /// Gets/sets the current swimming state
        /// </summary>
        public virtual bool IsSwimming
        {
            get { return m_z <= CurrentZone.Waterlevel; }
            set
            {
                if (value == m_swimming)
                    return;

                m_swimming = value;
            }
        }

        /// <summary>
        /// The stuck state of this player
        /// </summary>
        private bool m_stuckFlag = false;

        /// <summary>
        /// Gets/sets the current stuck state
        /// </summary>
        public virtual bool Stuck
        {
            get { return m_stuckFlag; }
            set
            {
                if (value == m_stuckFlag) return;
                m_stuckFlag = value;
            }
        }

        protected long m_beginDrowningTick;
        protected eWaterBreath m_currentWaterBreathState;

        protected int DrowningTimerCallback(ECSGameTimer callingTimer)
        {
            if (!IsAlive)
            {
                //Out.SendCloseTimerWindow();
                return 0;
            }

            if (ObjectState != eObjectState.Active)
                return 0;

            // Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.DrowningTimerCallback.CannotBreath"), eChatType.CT_Damaged, eChatLoc.CL_SystemWindow);
            //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.DrowningTimerCallback.Take5%Damage"), eChatType.CT_Damaged, eChatLoc.CL_SystemWindow);

            if (GameLoop.GameLoopTime - m_beginDrowningTick > 15000)
            {
                TakeDamage(null, eDamageType.Natural, MaxHealth, 0);
                //Out.SendCloseTimerWindow();
                return 0;
            }
            else
                TakeDamage(null, eDamageType.Natural, MaxHealth / 20, 0);

            return 1000;
        }

        protected bool m_sitting;
        public override bool IsSitting
        {
            get => m_sitting;
            set
            {
                m_sitting = value;
                if (value)
                {
                    CurrentSpellHandler?.CasterMoves();
                    if (attackComponent.AttackState && ActiveWeaponSlot == eActiveWeaponSlot.Distance)
                    {
                        string attackTypeMsg = "shot";
                        if (ActiveWeapon.Object_Type == (int)eObjectType.Thrown)
                            attackTypeMsg = "throw";
                        // Out.SendMessage("You move and interrupt your " + attackTypeMsg + "!", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                        attackComponent.StopAttack();
                    }
                }
            }
        }

        /// <summary>
        /// Gets or sets the max speed of this player
        /// (delegate to PlayerCharacter)
        /// </summary>
        public override short MaxSpeedBase
        {
            get { return base.MaxSpeedBase; }
            set { base.MaxSpeedBase = value; }
        }

        public override bool IsMoving => base.IsMoving || IsStrafing;

        public bool IsSprinting => effectListComponent.ContainsEffectForEffectType(eEffect.Sprint);

        public virtual bool Sprint(bool state)
        {
            if (state == IsSprinting)
                return state;

            if (state)
            {
                // Can't start sprinting with 10 endurance on 1.68 server.
                if (Endurance <= 10)
                    return false;

                if (IsStealthed)
                    return false;

                if (!IsAlive)
                    return false;

                new SprintECSGameEffect(new ECSGameEffectInitParams(this, 0, 1, null));

                return true;
            }
            else
            {
                if (effectListComponent.ContainsEffectForEffectType(eEffect.Sprint))
                    EffectService.RequestImmediateCancelEffect(EffectListService.GetEffectOnTarget(this, eEffect.Sprint), false);

                return false;
            }
        }

        protected bool m_strafing;
        public override bool IsStrafing
        {
            get => m_strafing;
            set
            {
                m_strafing = value;

                if (value)
                    OnPlayerMove();
            }
        }

        public virtual void OnPlayerMove()
        {
            if (IsSitting)
                Sit(false);

            if (IsCasting)
                CurrentSpellHandler?.CasterMoves();

            //if (IsCastingRealmAbility)
            //{
            //    Out.SendInterruptAnimation(this);
            //    Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "SpellHandler.CasterMove"), eChatType.CT_Important, eChatLoc.CL_SystemWindow);
            //    RealmAbilityCastTimer.Stop();
            //    RealmAbilityCastTimer = null;
            //}

            if (attackComponent.AttackState)
            {
                if (ActiveWeaponSlot == eActiveWeaponSlot.Distance)
                {
                    string attackTypeMsg = (ActiveWeapon.Object_Type == (int)eObjectType.Thrown ? "throw" : "shot");
                    //Out.SendMessage("You move and interrupt your " + attackTypeMsg + "!", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                    attackComponent.StopAttack();
                }
                else
                {
                    AttackData ad = TempProperties.GetProperty<AttackData>(LAST_ATTACK_DATA, null);

                    if (ad != null && ad.IsMeleeAttack && (ad.AttackResult == eAttackResult.TargetNotVisible || ad.AttackResult == eAttackResult.OutOfRange))
                    {
                        if (ad.Target != null && IsObjectInFront(ad.Target, 120) && IsWithinRadius(ad.Target, attackComponent.AttackRange) && attackComponent.attackAction != null)
                            attackComponent.attackAction.StartTime = 1;
                    }
                }
            }

            if (effectListComponent.ContainsEffectForEffectType(eEffect.Volley))
            {
                AtlasOF_VolleyECSEffect volley = (AtlasOF_VolleyECSEffect)EffectListService.GetEffectOnTarget(this, eEffect.Volley);

                if (volley != null)
                    volley.OnPlayerMoved();
            }
        }

        public virtual bool Sit(bool sit)
        {
            Sprint(false);

            if (IsSitting == sit)
                return sit;

            if (!IsAlive)
                return false;

            if (IsStunned)
                return false;

            if (IsMezzed)
                return false;

            if (sit && (CurrentSpeed > 0 || IsStrafing))
                return false;

            // Stop attacking if the player sits down.
            if (sit && attackComponent.AttackState)
                attackComponent.StopAttack();

            IsSitting = sit;

            if (sit)
            {
                Emote(eEmote.Drink);
                return true;
            }
            else
            {
                Emote(eEmote.LetsGo);

                return false;
            }
        }

        /// <summary>
        /// Sets the Living's ground-target Coordinates inside the current Region
        /// </summary>
        public override void SetGroundTarget(int groundX, int groundY, int groundZ)
        {
            ECSGameEffect volley = EffectListService.GetEffectOnTarget(this, eEffect.Volley);//volley check for gt
            if (volley != null)
            {
                //Out.SendMessage("You can't change ground target under volley effect!", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return;
            }
            else
            {
                base.SetGroundTarget(groundX, groundY, groundZ);

                //Out.SendMessage(String.Format("You ground-target {0},{1},{2}", groundX, groundY, groundZ), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                //if (SiegeWeapon != null)
                //    SiegeWeapon.SetGroundTarget(groundX, groundY, groundZ);
            }
        }

        /// <summary>
        /// Holds unique locations array
        /// </summary>
        protected readonly GameLocation[] m_lastUniqueLocations;

        /// <summary>
        /// Gets unique locations array
        /// </summary>
        public GameLocation[] LastUniqueLocations
        {
            get { return m_lastUniqueLocations; }
        }

        ///// <summary>
        ///// Updates Health, Mana, Sitting, Endurance, Concentration and Alive status to client
        ///// </summary>
        //public void UpdatePlayerStatus()
        //{
        //    Out.SendStatusUpdate();
        //}

        #endregion

        #region ControlledNpc

        /// <summary>
        /// Sets the controlled object for this player
        /// (delegates to CharacterClass)
        /// </summary>
        /// <param name="controlledNpc"></param>
        public override void SetControlledBrain(IControlledBrain controlledBrain)
        {
            if (controlledBrain == ControlledBrain)
                return;
            //if (controlledBrain.Owner != this)
            //throw new ArgumentException("ControlledNpc with wrong owner is set (mimic=" + Name + ", owner=" + controlledBrain.Owner.Name + ")", "controlledNpc");

            if (ControlledBrain == null)
                InitControlledBrainArray(1);

            ControlledBrain = controlledBrain;
        }

        /// <summary>
        /// Releases controlled object
        /// (delegates to CharacterClass)
        /// </summary>
        public virtual void CommandNpcRelease()
        {
            CharacterClass.CommandNpcRelease();
        }

        /// <summary>
        /// Commands controlled object to attack
        /// </summary>
        public virtual void CommandNpcAttack()
        {
            IControlledBrain npc = ControlledBrain;
            if (npc == null || !GameServer.ServerRules.IsAllowedToAttack(this, TargetObject as GameLiving, false))
                return;

            if (npc.Body.IsConfused)
            {
                //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.CommandNpcAttack.IsConfused", npc.Body.Name), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return;
            }

            if (!IsWithinRadius(TargetObject, 2000))
            {
                //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.CommandNpcAttack.TooFarAwayForPet"), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return;
            }

            if (!TargetInView)
            {
                //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.Attack.CantSeeTarget"), eChatType.CT_SpellResisted, eChatLoc.CL_SystemWindow);
                return;
            }

            //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.CommandNpcAttack.KillTarget", npc.Body.GetName(0, false)), eChatType.CT_System, eChatLoc.CL_SystemWindow);
            npc.Attack(TargetObject);
        }

        /// <summary>
        /// Commands controlled object to follow
        /// </summary>
        public virtual void CommandNpcFollow()
        {
            IControlledBrain npc = ControlledBrain;
            if (npc == null)
                return;

            if (npc.Body.IsConfused)
            {
                //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.CommandNpcAttack.IsConfused", npc.Body.Name), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return;
            }

            //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.CommandNpcAttack.FollowYou", npc.Body.GetName(0, false)), eChatType.CT_System, eChatLoc.CL_SystemWindow);
            npc.Disengage();
            npc.Follow(this);
        }

        /// <summary>
        /// Commands controlled object to stay where it is
        /// </summary>
        public virtual void CommandNpcStay()
        {
            IControlledBrain npc = ControlledBrain;
            if (npc == null)
                return;

            if (npc.Body.IsConfused)
            {
                //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.CommandNpcAttack.IsConfused", npc.Body.Name), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return;
            }

            //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.CommandNpcAttack.Stay", npc.Body.GetName(0, false)), eChatType.CT_System, eChatLoc.CL_SystemWindow);
            npc.Disengage();
            npc.Stay();
        }

        /// <summary>
        /// Commands controlled object to go to players location
        /// </summary>
        public virtual void CommandNpcComeHere()
        {
            IControlledBrain npc = ControlledBrain;
            if (npc == null)
                return;

            if (npc.Body.IsConfused)
            {
                //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.CommandNpcAttack.IsConfused", npc.Body.Name), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return;
            }

            //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.CommandNpcAttack.ComeHere", npc.Body.GetName(0, false)), eChatType.CT_System, eChatLoc.CL_SystemWindow);
            npc.Disengage();
            npc.ComeHere();
        }

        /// <summary>
        /// Commands controlled object to go to target
        /// </summary>
        public virtual void CommandNpcGoTarget()
        {
            IControlledBrain npc = ControlledBrain;
            if (npc == null)
                return;

            if (npc.Body.IsConfused)
            {
                //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.CommandNpcAttack.IsConfused", npc.Body.Name), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return;
            }

            GameObject target = TargetObject;
            if (target == null)
            {
                //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.CommandNpcGoTarget.MustSelectDestination"), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return;
            }

            if (GetDistance(new Point2D(target.X, target.Y)) > 1250)
            {
                //Out.SendMessage("Your target is too far away for your pet to reach!", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return;
            }

            //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.CommandNpcAttack.GoToTarget", npc.Body.GetName(0, false)), eChatType.CT_System, eChatLoc.CL_SystemWindow);
            npc.Disengage();
            npc.Goto(target);
        }

        /// <summary>
        /// Changes controlled object state to passive
        /// </summary>
        public virtual void CommandNpcPassive()
        {
            IControlledBrain npc = ControlledBrain;
            if (npc == null)
                return;

            if (npc.Body.IsConfused)
            {
                //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.CommandNpcAttack.IsConfused", npc.Body.Name), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return;
            }

            //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.CommandNpcAttack.Passive", npc.Body.GetName(0, false)), eChatType.CT_System, eChatLoc.CL_SystemWindow);
            npc.SetAggressionState(eAggressionState.Passive);
        }

        /// <summary>
        /// Changes controlled object state to aggressive
        /// </summary>
        public virtual void CommandNpcAgressive()
        {
            IControlledBrain npc = ControlledBrain;
            if (npc == null)
                return;

            if (npc.Body.IsConfused)
            {
                //.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.CommandNpcAttack.IsConfused", npc.Body.Name), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return;
            }

            //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.CommandNpcAttack.Aggressive", npc.Body.GetName(0, false)), eChatType.CT_System, eChatLoc.CL_SystemWindow);
            npc.SetAggressionState(eAggressionState.Aggressive);
        }

        /// <summary>
        /// Changes controlled object state to defensive
        /// </summary>
        public virtual void CommandNpcDefensive()
        {
            IControlledBrain npc = ControlledBrain;
            if (npc == null)
                return;

            if (npc.Body.IsConfused)
            {
                //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.CommandNpcAttack.IsConfused", npc.Body.Name), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                return;
            }

            //Out.SendMessage(LanguageMgr.GetTranslation(Client.Account.Language, "GamePlayer.CommandNpcAttack.Denfensive", npc.Body.GetName(0, false)), eChatType.CT_System, eChatLoc.CL_SystemWindow);
            npc.SetAggressionState(eAggressionState.Defensive);
        }

        #endregion
    }
}
