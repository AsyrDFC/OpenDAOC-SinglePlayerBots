using DOL.Database;
using DOL.GS;
using DOL.GS.Effects;
using DOL.GS.Keeps;
using DOL.GS.PacketHandler;
using DOL.GS.RealmAbilities;
using DOL.GS.Scripts;
using DOL.GS.ServerProperties;
using DOL.GS.SkillHandler;
using DOL.GS.Spells;
using DOL.Language;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;

namespace DOL.AI.Brain
{
    /// <summary>
    /// Standard brain for standard mobs
    /// </summary>
    public class MimicBrain : ABrain, IOldAggressiveBrain
    {
        public static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public override bool IsActive => Body != null && Body.IsAlive && Body.ObjectState == GameObject.eObjectState.Active;

        public bool IsMainPuller
        { get { return Body.Group?.MimicGroup.MainPuller == Body; } }
        public bool IsMainTank
        { get { return Body.Group?.MimicGroup.MainTank == Body; } }
        public bool IsMainLeader
        { get { return Body.Group?.MimicGroup.MainLeader == Body; } }
        public bool IsMainCC
        { get { return Body.Group?.MimicGroup.MainCC == Body; } }
        public bool IsMainAssist
        { get { return Body.Group?.MimicGroup.MainAssist == Body; } }

        private MimicNPC m_mimicBody;

        public MimicNPC MimicBody
        {
            get { return m_mimicBody; }
            set { m_mimicBody = value; }
        }

        public const int MAX_AGGRO_DISTANCE = 3600;
        public const int MAX_AGGRO_LIST_DISTANCE = 6000;

        public bool PreventCombat;
        public bool PvPMode;
        public bool Defend;
        public bool Roam;
        public bool IsFleeing;
        public bool IsPulling;

        public GameObject LastTargetObject;
        public bool IsFlanking;
        public Point2D TargetFlankPosition;

        public Spell QueuedOffensiveSpell;
        public Point3D TargetFleePosition;

        // Used for AmbientBehaviour "Seeing" - maintains a list of GamePlayer in range
        public List<GamePlayer> PlayersSeen = new();

        /// <summary>
        /// Constructs a new MimicBrain
        /// </summary>
        public MimicBrain() : base()
        {
            FSM = new FSM();
            FSM.Add(new MimicState_Idle(this));
            FSM.Add(new MimicState_WakingUp(this));
            FSM.Add(new MimicState_Aggro(this));
            FSM.Add(new MimicState_ReturnToSpawn(this));
            FSM.Add(new MimicState_Patrolling(this));
            FSM.Add(new MimicState_Roaming(this));
            FSM.Add(new MimicState_Dead(this));
            FSM.Add(new MimicState_FollowLeader(this));
            FSM.Add(new MimicState_Camp(this));

            FSM.SetCurrentState(eFSMStateType.WAKING_UP);
        }

        /// <summary>
        /// Returns the string representation of the MimicBrain
        /// </summary>
        public override string ToString()
        {
            return base.ToString() + ", AggroLevel=" + AggroLevel.ToString() + ", AggroRange=" + AggroRange.ToString();
        }

        public override bool Stop()
        {
            // tolakram - when the brain stops, due to either death or no players in the vicinity, clear the aggro list
            if (base.Stop())
            {
                ClearAggroList();
                return true;
            }

            return false;
        }

        public override void KillFSM()
        {
            FSM.KillFSM();
        }

        #region AI

        public override void Think()
        {
            FSM.Think();
        }

        public void OnGroupMemberAttacked(AttackData ad)
        {
            if (FSM.GetState(eFSMStateType.CAMP) == FSM.GetCurrentState())
            {
                if (!Body.IsWithinRadius(ad.Attacker, AggroRange))
                    return;
            }

            switch (ad.AttackResult)
            {
                case eAttackResult.Blocked:
                case eAttackResult.Evaded:
                case eAttackResult.Fumbled:
                case eAttackResult.HitStyle:
                case eAttackResult.HitUnstyled:
                case eAttackResult.Missed:
                case eAttackResult.Parried:
                    AddToAggroList(ad.Attacker, ad.Attacker.EffectiveLevel + ad.Damage + ad.CriticalDamage);
                    break;
            }

            if (FSM.GetState(eFSMStateType.AGGRO) != FSM.GetCurrentState())
                FSM.SetCurrentState(eFSMStateType.AGGRO);
        }

        public virtual bool CheckProximityAggro(int aggroRange)
        {
            FireAmbientSentence();

            // Check aggro only if our aggro list is empty and we're not in combat.
            if (AggroLevel > 0 && aggroRange > 0 && !HasAggro && !Body.AttackState && Body.CurrentSpellHandler == null)
            {
                CheckPlayerAggro();
                //brent changed to prevent npc aggro in rvr or pvp zones
                if (!Body.CurrentRegion.IsRvR || Body.CurrentRegion.ID != 252)
                {
                    CheckNPCAggro(aggroRange);
                }
            }

            // Some calls rely on this method to return if there's something in the aggro list, not necessarily to perform a proximity aggro check.
            // But this doesn't necessarily return whether or not the check was positive, only the current state (LoS checks take time).
            return HasAggro;
        }

        public virtual bool IsBeyondTetherRange()
        {
            if (Body.MaxDistance != 0)
            {
                int distance = Body.GetDistanceTo(Body.SpawnPoint);
                int maxDistance = Body.MaxDistance > 0 ? Body.MaxDistance : -Body.MaxDistance * AggroRange / 100;
                log.Info("Distance: " + distance + " MaxDistance: " + maxDistance);
                log.Info("IsBeyondTetherRange: " + (maxDistance > 0 && distance > maxDistance));
                return maxDistance > 0 && distance > maxDistance;
            }
            else
                return false;
        }

        public virtual bool HasPatrolPath()
        {
            return Body.MaxSpeedBase > 0 &&
                Body.CurrentSpellHandler == null &&
                !Body.IsMoving &&
                !Body.attackComponent.AttackState &&
                !Body.InCombat &&
                !Body.IsMovingOnPath &&
                Body.PathID != null &&
                Body.PathID != "" &&
                Body.PathID != "NULL";
        }

        /// <summary>
        /// Check for aggro against players
        /// </summary>
        protected virtual void CheckPlayerAggro()
        {
            foreach (GamePlayer player in Body.GetPlayersInRadius((ushort)AggroRange))
            {
                if (!CanAggroTarget(player))
                    continue;

                if (player.IsStealthed || player.Steed != null)
                    continue;

                if (player.EffectList.GetOfType<NecromancerShadeEffect>() != null)
                    continue;

                if (Properties.ALWAYS_CHECK_LOS)
                    // We don't know if the LoS check will be positive, so we have to ask other players
                    player.Out.SendCheckLOS(Body, player, new CheckLOSResponse(LosCheckForAggroCallback));
                else
                {
                    AddToAggroList(player, 0);
                    return;
                }
            }
        }

        /// <summary>
        /// Check for aggro against close NPCs
        /// </summary>
        protected virtual void CheckNPCAggro(int aggroRange)
        {
            foreach (GameNPC npc in Body.GetNPCsInRadius((ushort)aggroRange))
            {
                if (!CanAggroTarget(npc))
                    continue;

                if (npc is GameTaxi or GameTrainingDummy)
                    continue;

                if (Properties.ALWAYS_CHECK_LOS)
                {
                    // Check LoS if either the target or the current mob is a pet
                    if (npc.Brain is ControlledNpcBrain theirControlledNpcBrain && theirControlledNpcBrain.GetPlayerOwner() is GamePlayer theirOwner)
                    {
                        theirOwner.Out.SendCheckLOS(Body, npc, new CheckLOSResponse(LosCheckForAggroCallback));
                        continue;
                    }
                    //else if (this is ControlledNpcBrain ourControlledNpcBrain && ourControlledNpcBrain.GetPlayerOwner() is GamePlayer ourOwner)
                    //{
                    //    ourOwner.Out.SendCheckLOS(Body, npc, new CheckLOSResponse(LosCheckForAggroCallback));
                    //    continue;
                    //}
                }

                //if (!PvPMode && Body.Group != null)
                //{
                //    bool isAttacking = false;

                //    if (Body.Group.MimicGroup.CampPoint != null)
                //        isAttacking = true;

                //    foreach (GameLiving groupMember in Body.Group.GetMembersInTheGroup())
                //    {
                //        if (npc.TargetObject == groupMember)
                //            isAttacking = true;
                //    }

                //    if (!isAttacking)
                //        continue;
                //}

                AddToAggroList(npc, 1);

                //return;
            }
        }

        public virtual void FireAmbientSentence()
        {
            if (Body.ambientTexts != null && Body.ambientTexts.Any(item => item.Trigger == "seeing"))
            {
                // Check if we can "see" players and fire off ambient text
                List<GamePlayer> currentPlayersSeen = new();

                foreach (GamePlayer player in Body.GetPlayersInRadius((ushort)AggroRange))
                {
                    if (!PlayersSeen.Contains(player))
                    {
                        Body.FireAmbientSentence(GameNPC.eAmbientTrigger.seeing, player);
                        PlayersSeen.Add(player);
                    }

                    currentPlayersSeen.Add(player);
                }

                for (int i = 0; i < PlayersSeen.Count; i++)
                {
                    if (!currentPlayersSeen.Contains(PlayersSeen[i]))
                        PlayersSeen.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// The interval for thinking, min 1.5 seconds
        /// 10 seconds for 0 aggro mobs
        /// </summary>
        public override int ThinkInterval
        {
            get
            {
                return 500;
            }
        }

        /// <summary>
        /// If this brain is part of a formation, it edits it's values accordingly.
        /// </summary>
        /// <param name="x">The x-coordinate to refer to and change</param>
        /// <param name="y">The x-coordinate to refer to and change</param>
        /// <param name="z">The x-coordinate to refer to and change</param>
        public virtual bool CheckFormation(ref int x, ref int y, ref int z)
        {
            return false;
        }

        /// <summary>
        /// Checks the Abilities
        /// </summary>
        public virtual void CheckDefensiveAbilities()
        {
            if (Body.Abilities == null || Body.Abilities.Count <= 0)
                return;

            foreach (Ability ab in Body.Abilities.Values)
            {
                switch (ab.KeyName)
                {
                    case Abilities.Intercept:
                        {
                            //if (Body.Group != null)
                            //{
                            //    GameLiving interceptTarget;
                            //    List<GameLiving> interceptTargets = new List<GameLiving>();

                            //    foreach (GameLiving groupMember in Body.Group.GetMembersInTheGroup())
                            //    {
                            //        if (groupMember is MimicNPC mimic)
                            //        {
                            //            if (mimic.CharacterClass.ID == (int)eCharacterClass.Cleric ||
                            //                mimic.CharacterClass.ID == (int)eCharacterClass.Druid ||
                            //                mimic.CharacterClass.ID == (int)eCharacterClass.Healer ||
                            //                mimic.CharacterClass.ID == (int)eCharacterClass.Friar ||
                            //                mimic.CharacterClass.ID == (int)eCharacterClass.Bard ||
                            //                mimic.CharacterClass.ID == (int)eCharacterClass.Shaman)
                            //            {
                            //                interceptTargets.Add(groupMember);
                            //            }
                            //        }
                            //    }
                            //}
                            break;
                        }
                    case Abilities.Guard:
                        {
                            break;
                        }
                    case Abilities.Protect:
                        {
                            break;
                        }
                }
            }
        }

        public void CheckOffensiveAbilities()
        {
            if (Body.Abilities == null || Body.Abilities.Count <= 0)
                return;

            if (CanUseAbility())
            {
                foreach (Ability ab in Body.GetAllAbilities())
                {
                    if (Body.GetSkillDisabledDuration(ab) == 0)
                    {
                        switch (ab.KeyName)
                        {
                            case Abilities.Berserk:
                                {
                                    if (Body.TargetObject is GameLiving target)
                                    {
                                        if (Body.IsWithinRadius(Body.TargetObject, Body.AttackRange) &&
                                            GameServer.ServerRules.IsAllowedToAttack(Body, target, true))
                                        {
                                            new BerserkECSGameEffect(new ECSGameEffectInitParams(Body, 20000, 1));
                                            Body.DisableSkill(ab, 420000);
                                        }
                                    }

                                    break;
                                }

                            case Abilities.Stag:
                                {
                                    if (Body.TargetObject is GameLiving target)
                                    {
                                        if (Body.IsWithinRadius(Body.TargetObject, Body.AttackRange) &&
                                            GameServer.ServerRules.IsAllowedToAttack(Body, target, true) || Body.HealthPercent < 75)
                                        {
                                            new StagECSGameEffect(new ECSGameEffectInitParams(Body, 30000, 1), ab.Level);
                                            Body.DisableSkill(ab, 900000);
                                        }
                                    }

                                    break;
                                }

                            case Abilities.Triple_Wield:
                                {
                                    if (Body.TargetObject is GameLiving target)
                                    {
                                        if (Body.IsWithinRadius(Body.TargetObject, Body.AttackRange) &&
                                            GameServer.ServerRules.IsAllowedToAttack(Body, target, true))
                                        {
                                            new TripleWieldECSGameEffect(new ECSGameEffectInitParams(Body, 30000, 1));
                                            Body.DisableSkill(ab, 420000);
                                        }
                                    }

                                    break;
                                }

                            case Abilities.DirtyTricks:
                                {
                                    if (Body.TargetObject is GameLiving target)
                                    {
                                        GamePlayer gamePlayer = target as GamePlayer;
                                        MimicNPC mimic = target as MimicNPC;

                                        if ((gamePlayer != null && gamePlayer.CharacterClass.ClassType == eClassType.ListCaster) ||
                                            (mimic != null && mimic.CharacterClass.ClassType == eClassType.ListCaster))
                                            break;

                                        if (Body.IsWithinRadius(Body.TargetObject, Body.AttackRange) &&
                                            GameServer.ServerRules.IsAllowedToAttack(Body, target, true))
                                        {
                                            new DirtyTricksECSGameEffect(new ECSGameEffectInitParams(Body, 30000, 1));
                                            Body.DisableSkill(ab, 420000);
                                        }
                                    }

                                    break;
                                }

                            case Abilities.ChargeAbility:
                                {
                                    MimicBody.DevOut("I charged.");
                                    if (Body.TargetObject is GameLiving target &&
                                        GameServer.ServerRules.IsAllowedToAttack(Body, target, true) &&
                                        !Body.IsWithinRadius(target, 500))
                                    {
                                        ChargeAbility charge = Body.GetAbility<ChargeAbility>();

                                        if (charge != null && Body.GetSkillDisabledDuration(charge) <= 0)
                                            charge.Execute(Body);
                                    }

                                    break;
                                }
                        }
                    }
                }
            }
        }

        private bool CanUseAbility()
        {
            if (!Body.IsAlive ||
                Body.IsMezzed ||
                Body.IsStunned ||
                Body.IsSitting)
                return false;

            return true;
        }

        #endregion AI

        #region MimicGroup AI

        #region MainPuller

        public void CheckPuller()
        {
            if (IsPulling && Body.TargetObject != null && Body.TargetObject.ObjectState == GameObject.eObjectState.Active)
            {
                if (CheckResetPuller())
                {
                    Body.ReturnToSpawnPoint(Body.MaxSpeed);

                    if ((MimicBody.CharacterClass.ID != (int)eCharacterClass.Hunter ||
                        MimicBody.CharacterClass.ID != (int)eCharacterClass.Ranger ||
                        MimicBody.CharacterClass.ID != (int)eCharacterClass.Scout) &&
                        MimicBody.CharacterClass.ClassType != eClassType.ListCaster)
                    {
                        if (MimicBody.MimicSpec.is2H)
                            Body.SwitchWeapon(eActiveWeaponSlot.TwoHanded);
                        else
                            Body.SwitchWeapon(eActiveWeaponSlot.Standard);
                    }

                    return;
                }
            }

            if (!Body.InCombat)
            {
                if (CheckDelayPull())
                {
                    Body.StopAttack();
                    Body.StopFollowing();
                }
                else
                {
                    GameLiving pullTarget = GetPullTarget();
                    PerformPull(pullTarget);
                }
            }
        }

        public bool CheckDelayPull()
        {
            if (LastTargetObject != null && LastTargetObject.ObjectState == GameObject.eObjectState.Active)
                return true;

            if (CheckSpells(eCheckSpellType.Defensive) || MimicBody.Sit(CheckStats(75)))
                return true;

            if (Body.Group != null &&
                Body.Group.GetMembersInTheGroup().Any(groupMember => groupMember.IsCasting || groupMember.IsSitting))
                return true;

            return false;
        }

        public GameLiving GetPullTarget()
        {
            if (!Body.IsAttacking && !Body.IsCasting && !Body.IsSitting)
            {
                if (Body.Group.MimicGroup.CCTargets.Count > 0)
                    return Body.Group.MimicGroup.CCTargets[Util.Random(Body.Group.MimicGroup.CCTargets.Count - 1)];

                CheckProximityAggro(3600);

                if (AggroTable.Count > 0)
                {
                    GameLiving closestTarget;

                    if (Body.Group.MimicGroup.PullFromPoint != null)
                        closestTarget = AggroTable.Where(pair => Body.GetConLevel(pair.Key) >= Body.Group.MimicGroup.ConLevelFilter).
                                                   OrderBy(pair => pair.Key.GetDistance(Body.Group.MimicGroup.PullFromPoint)).
                                                   ThenBy(pair => Body.GetDistanceTo(pair.Key)).First().Key;
                    else
                        closestTarget = AggroTable.Where(pair => Body.GetConLevel(pair.Key) > Body.Group.MimicGroup.ConLevelFilter).
                                                   OrderBy(pair => Body.GetDistanceTo(pair.Key)).First().Key;

                    return closestTarget;
                }
            }

            return null;
        }

        private bool CheckResetPuller()
        {
            if (Body.TargetObject is GameNPC npcTarget && npcTarget.Brain is StandardMobBrain mobBrain && mobBrain.HasAggro)
            {
                LastTargetObject = Body.TargetObject;
                IsPulling = false;
                Body.StopAttack();
                ClearAggroList();

                return true;
            }

            return false;
        }

        public void PerformPull(GameLiving target)
        {
            if (target == null)
                return;

            IsPulling = true;

            if (Body.Inventory.GetItem(eInventorySlot.DistanceWeapon) != null)
            {
                Body.SwitchWeapon(eActiveWeaponSlot.Distance);
                Body.StartAttack(target);
            }
            else
            {
                //if (Body.CanCastInstantHarmfulSpells)
                //{
                //    foreach(Spell spell in Body.InstantHarmfulSpells)

                //}
                //if (!Body.IsWithinRadius(Body.TargetObject, spell.Range))
                //{
                //    Body.Follow(Body.TargetObject, spell.Range - 100, 5000);
                //    QueuedOffensiveSpell = spell;
                //    return false;
                //}
            }
        }

        #endregion MainPuller

        #region MainLeader

        public bool CheckDelayRoam()
        {
            if (CheckSpells(eCheckSpellType.Defensive) || MimicBody.Sit(CheckStats(75)))
                return true;

            if (Body.Group != null &&
                Body.Group.GetMembersInTheGroup().Any(groupMember => groupMember.IsCasting || groupMember.IsSitting || (groupMember is MimicNPC mimic &&
                                                      mimic.MimicBrain.FSM.GetCurrentState() == mimic.MimicBrain.FSM.GetState(eFSMStateType.FOLLOW_THE_LEADER) &&
                                                      !Body.IsWithinRadius(groupMember, 1000))))
                return true;

            return false;
        }

        #endregion MainLeader

        #region MainCC

        public void CheckMainCC()
        {
            if (Body.Group.MimicGroup.CCTargets.Count > 0)
            {
                if (CheckSpells(MimicBrain.eCheckSpellType.CrowdControl))
                    return;
            }

            if (!Body.InCombat && Body.Group.MimicGroup.CCTargets.Count > 0)
            {
                Body.Group.MimicGroup.CCTargets = ValidateCCList(Body.Group.MimicGroup.CCTargets);
            }
        }

        // Test for bad lists. Might not be needed.
        private List<GameLiving> ValidateCCList(List<GameLiving> ccList)
        {
            List<GameLiving> validatedList = new List<GameLiving>();

            if (ccList.Any())
            {
                foreach (GameLiving cc in ccList)
                {
                    if (cc is GameNPC npc && npc != null && npc.IsAlive && ((StandardMobBrain)npc.Brain).HasAggro)
                    {
                        validatedList.Add(cc);
                    }
                }
            }

            return validatedList;
        }

        #endregion MainCC

        #region MainTank

        public bool CheckMainTankTarget()
        {
            if (!IsMainTank)
                return false;

            GameLiving target = null;
            List<GameLiving> listOfTargets = null;

            if (AggroTable.Count > 0)
            {
                listOfTargets = (AggroTable.Keys.Where(key => key.TargetObject is GameLiving livingTarget && livingTarget != Body &&
                                            !livingTarget.IsMezzed && !livingTarget.IsRooted).ToList());
            }

            if (listOfTargets != null && listOfTargets.Count > 0)
                target = listOfTargets[Util.Random(listOfTargets.Count - 1)];

            if (target != null)
            {
                Body.TargetObject = target;

                return true;
            }

            return false;
        }

        #endregion MainTank

        public bool CheckStats(short threshold)
        {
            if (Body.HealthPercent < threshold || (Body.MaxMana > 0 && Body.ManaPercent < threshold) || Body.EndurancePercent < threshold)
                return true;

            return false;
        }

        #endregion MimicGroup AI

        #region Aggro

        protected int _aggroRange;

        /// <summary>
        /// Max Aggro range in that this npc searches for enemies
        /// </summary>
        public virtual int AggroRange
        {
            get => Math.Min(_aggroRange, MAX_AGGRO_DISTANCE);
            set => _aggroRange = value;
        }

        /// <summary>
        /// Aggressive Level in % 0..100, 0 means not Aggressive
        /// </summary>
        public virtual int AggroLevel { get; set; }

        /// <summary>
        /// List of livings that this npc has aggro on, living => aggroAmount
        /// </summary>
        public Dictionary<GameLiving, long> AggroTable { get; private set; } = new Dictionary<GameLiving, long>();

        /// <summary>
        /// Checks whether living has someone on its aggrolist
        /// </summary>
        public virtual bool HasAggro
        {
            get
            {
                lock ((AggroTable as ICollection).SyncRoot)
                {
                    return AggroTable.Count > 0;
                }
            }
        }

        /// <summary>
        /// Add aggro table of this brain to that of another living.
        /// </summary>
        public void AddAggroListTo(StandardMobBrain brain)
        {
            if (!brain.Body.IsAlive)
                return;

            KeyValuePair<GameLiving, long>[] aggroTable = Array.Empty<KeyValuePair<GameLiving, long>>();

            lock ((AggroTable as ICollection).SyncRoot)
                aggroTable = AggroTable.ToArray();

            foreach (var aggro in aggroTable)
                brain.AddToAggroList(aggro.Key, Body.MaxHealth);
        }

        /// <summary>
        /// Add living to the aggrolist
        /// aggroAmount can be negative to lower amount of aggro
        /// </summary>
        public virtual void AddToAggroList(GameLiving living, int aggroAmount)
        {
            //log.Info("Added " + living.Name + " to " + Body.Name);
            // tolakram - duration spell effects will attempt to add to aggro after npc is dead
            if (Body.IsConfused || !Body.IsAlive || living == null)
                return;

            // Handle trigger to say sentence on first aggro.
            if (AggroTable.Count < 1)
                Body.FireAmbientSentence(GameNPC.eAmbientTrigger.aggroing, living);

            // Only protect if gameplayer and aggroAmount > 0
            if (living is GamePlayer player && aggroAmount > 0)
            {
                // If player is in group, add whole group to aggro list
                if (player.Group != null)
                {
                    lock ((AggroTable as ICollection).SyncRoot)
                    {
                        foreach (GamePlayer p in player.Group.GetPlayersInTheGroup())
                        {
                            if (!AggroTable.ContainsKey(p))
                                AggroTable[p] = 1L; // Add the missing group member on aggro table
                        }
                    }
                }

                foreach (ProtectECSGameEffect protect in player.effectListComponent.GetAbilityEffects().Where(e => e.EffectType == eEffect.Protect))
                {
                    if (aggroAmount <= 0)
                        break;

                    if (protect.ProtectTarget != living)
                        continue;

                    GamePlayer protectSource = protect.ProtectSource;

                    if (protectSource.IsStunned
                        || protectSource.IsMezzed
                        || protectSource.IsSitting
                        || protectSource.ObjectState != GameObject.eObjectState.Active
                        || !protectSource.IsAlive
                        || !protectSource.InCombat)
                        continue;

                    if (!living.IsWithinRadius(protectSource, ProtectAbilityHandler.PROTECT_DISTANCE))
                        continue;

                    // P I: prevents 10% of aggro amount
                    // P II: prevents 20% of aggro amount
                    // P III: prevents 30% of aggro amount
                    // guessed percentages, should never be higher than or equal to 50%
                    int abilityLevel = protectSource.GetAbilityLevel(Abilities.Protect);
                    int protectAmount = (int)(abilityLevel * 0.10 * aggroAmount);

                    if (protectAmount > 0)
                    {
                        aggroAmount -= protectAmount;
                        protectSource.Out.SendMessage(LanguageMgr.GetTranslation(protectSource.Client.Account.Language, "AI.Brain.StandardMobBrain.YouProtDist", player.GetName(0, false),
                                                                                 Body.GetName(0, false, protectSource.Client.Account.Language, Body)), eChatType.CT_System, eChatLoc.CL_SystemWindow);

                        lock ((AggroTable as ICollection).SyncRoot)
                        {
                            if (AggroTable.ContainsKey(protectSource))
                                AggroTable[protectSource] += protectAmount;
                            else
                                AggroTable[protectSource] = protectAmount;
                        }
                    }
                }
            }

            lock ((AggroTable as ICollection).SyncRoot)
            {
                if (AggroTable.ContainsKey(living))
                {
                    long amount = AggroTable[living];
                    amount += aggroAmount;

                    // can't be removed this way, set to minimum
                    if (amount <= 0)
                        amount = 1L;

                    AggroTable[living] = amount;
                }
                else
                    AggroTable[living] = aggroAmount > 0 ? aggroAmount : 1L;
            }
        }

        public void PrintAggroTable()
        {
            StringBuilder sb = new();

            foreach (GameLiving living in AggroTable.Keys)
                sb.AppendLine($"Living: {living.Name}, aggro: {AggroTable[living].ToString()}");

            Console.WriteLine(sb.ToString());
        }

        /// <summary>
        /// Get current amount of aggro on aggrotable.
        /// </summary>
        public virtual long GetAggroAmountForLiving(GameLiving living)
        {
            lock ((AggroTable as ICollection).SyncRoot)
            {
                return AggroTable.ContainsKey(living) ? AggroTable[living] : 0;
            }
        }

        /// <summary>
        /// Remove one living from aggro list.
        /// </summary>
        public virtual void RemoveFromAggroList(GameLiving living)
        {
            lock ((AggroTable as ICollection).SyncRoot)
                AggroTable.Remove(living);
        }

        /// <summary>
        /// Remove all livings from the aggrolist.
        /// </summary>
        public virtual void ClearAggroList()
        {
            CanBAF = true; // Mobs that drop out of combat can BAF again

            lock ((AggroTable as ICollection).SyncRoot)
            {
                AggroTable.Clear();
            }
        }

        /// <summary>
        /// Selects and attacks the next target or does nothing.
        /// </summary>
        public virtual void AttackMostWanted()
        {
            if (!IsActive)
                return;

            if (ECS.Debug.Diagnostics.AggroDebugEnabled)
                PrintAggroTable();

            //if (PvPMode || CheckAssist == null)

            if (!CheckMainTankTarget())
                Body.TargetObject = CalculateNextAttackTarget();

            if (Body.TargetObject != null)
            {
                if (!IsFleeing && CheckSpells(eCheckSpellType.Offensive))
                    Body.StopAttack();
                else
                {
                    CheckOffensiveAbilities();

                    if (Body.ControlledBrain != null)
                        Body.ControlledBrain.Attack(Body.TargetObject);

                    if (MimicBody.CharacterClass.ClassType == eClassType.ListCaster)
                    {
                        ECSGameAbilityEffect quickCast = EffectListService.GetAbilityEffectOnTarget(Body, eEffect.QuickCast);

                        if (quickCast != null)
                        {
                            CheckSpells(eCheckSpellType.Offensive);
                            return;
                        }

                        // Don't flee if in a group for now. Need better control over when and where and how.
                        if (Body.Group == null)
                        {
                            if ((TargetFleePosition == null && !IsFleeing && Body.IsBeingInterrupted && quickCast == null))
                            {
                                //TODO: Get dynamic distances based on circumstances. Maybe rethink the whole thing.
                                int fleeDistance = 2200 - Body.GetDistance(Body.TargetObject);

                                Flee(fleeDistance);

                                return;
                            }

                            if (Body.IsTargetPositionValid)
                                return;
                            else if (TargetFleePosition != null)
                            {
                                if (Body.GetDistance(TargetFleePosition) < 5)
                                {
                                    IsFleeing = false;
                                    TargetFleePosition = null;

                                    if (Body.IsWithinRadius(Body.TargetObject, 500))
                                    {
                                        Flee(1800);
                                        return;
                                    }

                                    if (Body.TargetObject != Body)
                                        Body.TurnTo(Body.TargetObject);
                                }
                            }
                        }

                        return;
                    }

                    if (Body.TargetObject != LastTargetObject)
                        ResetFlanking();

                    if (MimicBody.CanUsePositionalStyles && !IsMainTank && Body.ActiveWeapon != null && Body.ActiveWeapon.Item_Type != (int)eInventorySlot.DistanceWeapon)
                    {
                        if (Body.TargetObject is GameLiving livingTarget)
                        {
                            if (livingTarget.IsMoving || livingTarget.TargetObject == Body)
                                ResetFlanking();

                            if (TargetFlankPosition == null && !IsFlanking && !livingTarget.IsMoving && livingTarget.TargetObject != Body)
                            {
                                LastTargetObject = Body.TargetObject;
                                TargetFlankPosition = GetStylePositionPoint(livingTarget, GetPositional());
                                Body.StopAttack();
                                Body.StopFollowing();
                                Body.PathTo(new Point3D(TargetFlankPosition.X, TargetFlankPosition.Y, livingTarget.Z), Body.MaxSpeed);
                                return;
                            }

                            if (Body.IsTargetPositionValid)
                            {
                                if (TargetFlankPosition == null)
                                    Body.Follow(Body.TargetObject, 75, 5000);
                                else
                                    return;
                            }
                            else if (TargetFlankPosition != null)
                            {
                                if (Body.GetDistance(TargetFlankPosition) < 5)
                                {
                                    IsFlanking = true;
                                    TargetFlankPosition = null;
                                }
                            }
                        }
                    }

                    if ((MimicBody.CharacterClass.ID == (int)eCharacterClass.Minstrel ||
                        (MimicBody.CharacterClass.ID == (int)eCharacterClass.Bard && Body.Group == null)) &&
                        Body.ActiveWeaponSlot != eActiveWeaponSlot.Standard)
                        Body.SwitchWeapon(eActiveWeaponSlot.Standard);

                    Body.StartAttack(Body.TargetObject);

                    LastTargetObject = Body.TargetObject;
                }
            }
        }

        private void Flee(int distance)
        {
            IsFleeing = true;
            MimicBody.Sprint(true);
            TargetFleePosition = GetFleePoint(distance);
            Body.PathTo(TargetFleePosition, Body.MaxSpeed);
        }

        public void ResetFlanking()
        {
            IsFlanking = false;
            TargetFlankPosition = null;
        }

        private GameObject CheckAssist()
        {
            if (Body.Group != null && Body.Group.MimicGroup.MainAssist.InCombat)
            {
                GameObject assistTarget = Body.Group.MimicGroup.CurrentTarget;
                GameObject target = null;

                if (assistTarget != null && CanAggroTarget((GameLiving)assistTarget))
                    target = assistTarget;

                return target;
            }

            return null;

            //if (Body.Group != null)
            //{
            //    foreach (GameLiving groupMember in Body.Group.GetMembersInTheGroup())
            //    {
            //        if (groupMember is GameLiving living)
            //            foreach (var attacker in living.attackComponent.Attackers)
            //                AddToAggroList(attacker.Key, 1);
            //    }
            //}
        }

        private Point3D GetFleePoint(int fleeDistance)
        {
            float diffx = (long)Body.TargetObject.X - Body.X;
            float diffy = (long)Body.TargetObject.Y - Body.Y;

            float distance = (float)Math.Sqrt(diffx * diffx + diffy * diffy);

            diffx = (diffx / distance) * fleeDistance;
            diffy = (diffy / distance) * fleeDistance;

            int newX = (int)(Body.TargetObject.X - diffx);
            int newY = (int)(Body.TargetObject.Y - diffy);

            Vector3? target = PathingMgr.Instance.GetClosestPointAsync(Body.CurrentZone, new Vector3(newX, newY, 0));

            return new Point3D((int)target?.X, (int)target?.Y, (int)target?.Z);
        }

        private ePositional GetPositional()
        {
            ePositional positional = ePositional.None;

            if (MimicBody.CanUseSideStyles && MimicBody.CanUseBackStyles)
            {
                if (Util.RandomBool())
                    positional = ePositional.Back;
                else
                    positional = ePositional.Side;
            }
            else if (MimicBody.CanUseSideStyles)
                positional = ePositional.Side;
            else if (MimicBody.CanUseBackStyles)
                positional = ePositional.Back;

            return positional;
        }

        private Point2D GetStylePositionPoint(GameLiving living, ePositional positional)
        {
            ushort heading = 0;

            switch (positional)
            {
                case ePositional.Side:
                    if (Util.RandomBool())
                        heading = (ushort)(living.Heading - 1024);
                    else
                        heading = (ushort)(living.Heading + 1024);
                    break;

                case ePositional.Back:
                    heading = (ushort)(living.Heading - 2048);
                    break;

                case ePositional.Front:
                    heading = living.Heading;
                    break;
            }

            if (heading < 0)
                heading += 4096;

            if (heading > 4096)
                heading -= 4096;

            Point2D point = living.GetPointFromHeading(heading, 75);

            return point;
        }

        protected virtual void LosCheckForAggroCallback(GamePlayer player, ushort response, ushort targetOID)
        {
            // If we kept adding to the aggro list it would make mobs go from one target immediately to another.
            if (HasAggro || targetOID == 0)
                return;

            if ((response & 0x100) == 0x100)
            {
                GameObject gameObject = Body.CurrentRegion.GetObject(targetOID);

                if (gameObject is GameLiving gameLiving)
                    AddToAggroList(gameLiving, 0);
            }
        }

        /// <summary>
        /// Returns whether or not 'living' is still a valid target.
        /// </summary>
        protected virtual bool ShouldThisLivingBeFilteredOutFromAggroList(GameLiving living)
        {
            return !living.IsAlive ||
                   living.ObjectState != GameObject.eObjectState.Active ||
                   living.IsStealthed ||
                   living.CurrentRegion != Body.CurrentRegion ||
                   !Body.IsWithinRadius(living, MAX_AGGRO_LIST_DISTANCE) ||
                   !GameServer.ServerRules.IsAllowedToAttack(Body, living, true);
        }

        /// <summary>
        /// Returns a copy of 'aggroList' ordered by aggro amount (descending), modified by range.
        /// </summary>
        protected virtual List<KeyValuePair<GameLiving, long>> OrderAggroListByModifiedAggroAmount(Dictionary<GameLiving, long> aggroList)
        {
            return aggroList.OrderByDescending(x => x.Value * Math.Min(500.0 / Body.GetDistanceTo(x.Key), 1)).ToList();
        }

        /// <summary>
        /// Filters out invalid targets from the current aggro list and returns a copy.
        /// </summary>
        protected virtual Dictionary<GameLiving, long> FilterOutInvalidLivingsFromAggroList()
        {
            Dictionary<GameLiving, long> tempAggroList;
            bool modified = false;

            lock ((AggroTable as ICollection).SyncRoot)
            {
                tempAggroList = new Dictionary<GameLiving, long>(AggroTable);
            }

            foreach (KeyValuePair<GameLiving, long> pair in tempAggroList.ToList())
            {
                GameLiving living = pair.Key;

                if (living == null)
                    continue;

                // Check to make sure this living is still a valid target.
                if (ShouldThisLivingBeFilteredOutFromAggroList(living))
                {
                    // Keep Necromancer shades so that we can attack them if their pets die.
                    if (EffectListService.GetEffectOnTarget(living, eEffect.Shade) != null)
                        continue;

                    modified = true;
                    tempAggroList.Remove(living);
                }
            }

            if (modified)
            {
                // Body.attackComponent.RemoveAttacker(removable.Key); ???

                lock ((AggroTable as ICollection).SyncRoot)
                {
                    AggroTable = tempAggroList.ToDictionary(x => x.Key, x => x.Value);
                }
            }

            return tempAggroList;
        }

        /// <summary>
        /// Returns the best target to attack from the current aggro list.
        /// </summary>
        protected virtual GameLiving CalculateNextAttackTarget()
        {
            // Filter out invalid entities (updates the list), then order the returned copy by (modified) aggro amount.
            List<KeyValuePair<GameLiving, long>> aggroList = OrderAggroListByModifiedAggroAmount(FilterOutInvalidLivingsFromAggroList());

            // We keep shades in aggro lists so that mobs attack them after their pet dies, but we must never return one.
            GameLiving nextTarget = aggroList.Find(x => EffectListService.GetEffectOnTarget(x.Key, eEffect.Shade) == null).Key;

            // TODO: Make target selection a little more random in PvP.
            //var random = aggroList.FindAll(x => EffectListService.GetEffectOnTarget(x.Key, eEffect.Shade) == null);
            //GameLiving nextTarget = null;
            //if (random.Any())
            // nextTarget = random[Util.Random(random.Count - 1)].Key;

            if (nextTarget != null)
                return nextTarget;

            // The list is either empty or full of shades.
            // If it's empty, return null.
            // If we found a shade, return the pet instead (if there's one). Ideally this should never happen.
            // If it does, it means we added the shade to the aggro list instead of the pet.
            // Which is currently the case due to the way 'AddToAggroList' propagates aggro to group members, and maybe other places.
            return aggroList.FirstOrDefault().Key?.ControlledBrain?.Body;
        }

        public virtual bool CanAggroTarget(GameLiving target)
        {
            if (!GS.GameServer.ServerRules.IsAllowedToAttack(Body, target, true))
            {
                //log.Info(Body.Name + " Is not allowed to attack " + target.Name);
                return false;
            }

            // Get owner if target is pet or subpet
            GameLiving realTarget = target;

            if (realTarget is GameNPC npcTarget && npcTarget.Brain is IControlledBrain npcTargetBrain)
                realTarget = npcTargetBrain.GetLivingOwner();

            // Only attack if green+ to target
            if (realTarget.IsObjectGreyCon(Body))
                return false;

            // If this npc have Faction return the AggroAmount to Player
            if (Body.Faction != null)
            {
                if (realTarget is GameNPC && realTarget is not MimicNPC && realTarget is not GameKeepGuard && PvPMode)
                    return false;

                if (realTarget is GamePlayer && realTarget.Realm != Body.Realm)
                    return true;
                else if (realTarget is GameNPC realTargetNpc && Body.Faction.EnemyFactions.Contains(realTargetNpc.Faction))
                    return true;
            }

            // We put this here to prevent aggroing non-factions npcs
            return (Body.Realm != eRealm.None || realTarget is not GameNPC) && AggroLevel > 0;
        }

        protected virtual void OnFollowLostTarget(GameObject target)
        {
            AttackMostWanted();

            if (!Body.attackComponent.AttackState)
                Body.ReturnToSpawnPoint(NpcMovementComponent.DEFAULT_WALK_SPEED);
        }

        public virtual void OnAttackedByEnemy(AttackData ad)
        {
            if (!Body.IsAlive || Body.ObjectState != GameObject.eObjectState.Active)
                return;

            if (FSM.GetCurrentState() == FSM.GetState(eFSMStateType.PASSIVE))
                return;

            int damage = ad.Damage + ad.CriticalDamage + Math.Abs(ad.Modifier);
            ConvertDamageToAggroAmount(ad.Attacker, Math.Max(1, damage));

            if (!Body.attackComponent.AttackState && FSM.GetCurrentState() != FSM.GetState(eFSMStateType.AGGRO))
            {
                FSM.SetCurrentState(eFSMStateType.AGGRO);
                Think();
            }
        }

        /// <summary>
        /// Converts a damage amount into an aggro amount, and splits it between the pet and its owner if necessary.
        /// Assumes damage to be superior than 0.
        /// </summary>
        protected virtual void ConvertDamageToAggroAmount(GameLiving attacker, int damage)
        {
            if (attacker is GameNPC NpcAttacker && NpcAttacker.Brain is ControlledNpcBrain controlledBrain)
            {
                damage = controlledBrain.ModifyDamageWithTaunt(damage);

                // Aggro is split between the owner (15%) and their pet (85%).
                int aggroForOwner = (int)(damage * 0.15);

                // We must ensure that the same amount of aggro isn't added for both, otherwise an out-of-combat mob could attack the owner when their pet engages it.
                // The owner must also always generate at least 1 aggro.
                // This works as long as the split isn't 50 / 50.
                if (aggroForOwner == 0)
                {
                    AddToAggroList(controlledBrain.Owner, 1);
                    AddToAggroList(NpcAttacker, Math.Max(2, damage));
                }
                else
                {
                    AddToAggroList(controlledBrain.Owner, aggroForOwner);
                    AddToAggroList(NpcAttacker, damage - aggroForOwner);
                }
            }
            else
                AddToAggroList(attacker, damage);
        }

        #endregion Aggro

        #region Bring a Friend

        /// <summary>
        /// Initial range to try to get BAFs from.
        /// May be overloaded for specific brain types, ie. dragons or keep guards
        /// </summary>
        protected virtual ushort BAFInitialRange => 250;

        /// <summary>
        /// Max range to try to get BAFs from.
        /// May be overloaded for specific brain types, ie.dragons or keep guards
        /// </summary>
        protected virtual ushort BAFMaxRange => 2000;

        /// <summary>
        /// Max range to try to look for nearby players.
        /// May be overloaded for specific brain types, ie.dragons or keep guards
        /// </summary>
        protected virtual ushort BAFPlayerRange => 5000;

        /// <summary>
        /// Can the mob bring a friend?
        /// Set to false when a mob BAFs or is brought by a friend.
        /// </summary>
        public virtual bool CanBAF { get; set; } = true;

        /// <summary>
        /// Bring friends when this mob aggros
        /// </summary>
        /// <param name="attacker">Whoever triggered the BAF</param>
        protected virtual void BringFriends(GameLiving attacker)
        {
            if (!CanBAF)
                return;

            GamePlayer puller;  // player that triggered the BAF
            GameLiving actualPuller;

            // Only BAF on players and pets of players
            if (attacker is GamePlayer)
            {
                puller = (GamePlayer)attacker;
                actualPuller = puller;
            }
            else if (attacker is GameSummonedPet pet && pet.Owner is GamePlayer owner)
            {
                puller = owner;
                actualPuller = attacker;
            }
            else if (attacker is BDSubPet bdSubPet && bdSubPet.Owner is GameSummonedPet bdPet && bdPet.Owner is GamePlayer bdOwner)
            {
                puller = bdOwner;
                actualPuller = bdPet;
            }
            else
                return;

            CanBAF = false; // Mobs only BAF once per fight

            int numAttackers = 0;

            List<GamePlayer> victims = null; // Only instantiated if we're tracking potential victims

            // These are only used if we have to check for duplicates
            HashSet<string> countedVictims = null;
            HashSet<string> countedAttackers = null;

            BattleGroup bg = puller.TempProperties.GetProperty<BattleGroup>(BattleGroup.BATTLEGROUP_PROPERTY, null);

            // Check group first to minimize the number of HashSet.Add() calls
            if (puller.Group is Group group)
            {
                if (Properties.BAF_MOBS_COUNT_BG_MEMBERS && bg != null)
                    countedAttackers = new HashSet<string>(); // We have to check for duplicates when counting attackers

                if (!Properties.BAF_MOBS_ATTACK_PULLER)
                {
                    if (Properties.BAF_MOBS_ATTACK_BG_MEMBERS && bg != null)
                    {
                        // We need a large enough victims list for group and BG, and also need to check for duplicate victims
                        victims = new List<GamePlayer>(group.MemberCount + bg.PlayerCount - 1);
                        countedVictims = new HashSet<string>();
                    }
                    else
                        victims = new List<GamePlayer>(group.MemberCount);
                }

                foreach (GamePlayer player in group.GetPlayersInTheGroup())
                {
                    if (player != null && (player.InternalID == puller.InternalID || player.IsWithinRadius(puller, BAFPlayerRange, true)))
                    {
                        numAttackers++;
                        countedAttackers?.Add(player.InternalID);

                        if (victims != null)
                        {
                            victims.Add(player);
                            countedVictims?.Add(player.InternalID);
                        }
                    }
                }
            }

            // Do we have to count BG members, or add them to victims list?
            if (bg != null && (Properties.BAF_MOBS_COUNT_BG_MEMBERS || (Properties.BAF_MOBS_ATTACK_BG_MEMBERS && !Properties.BAF_MOBS_ATTACK_PULLER)))
            {
                if (victims == null && Properties.BAF_MOBS_ATTACK_BG_MEMBERS && !Properties.BAF_MOBS_ATTACK_PULLER)
                    // Puller isn't in a group, so we have to create the victims list for the BG
                    victims = new List<GamePlayer>(bg.PlayerCount);

                foreach (GamePlayer player2 in bg.Members.Keys)
                {
                    if (player2 != null && (player2.InternalID == puller.InternalID || player2.IsWithinRadius(puller, BAFPlayerRange, true)))
                    {
                        if (Properties.BAF_MOBS_COUNT_BG_MEMBERS && (countedAttackers == null || !countedAttackers.Contains(player2.InternalID)))
                            numAttackers++;

                        if (victims != null && (countedVictims == null || !countedVictims.Contains(player2.InternalID)))
                            victims.Add(player2);
                    }
                }
            }

            if (numAttackers == 0)
                // Player is alone
                numAttackers = 1;

            int percentBAF = Properties.BAF_INITIAL_CHANCE
                + ((numAttackers - 1) * Properties.BAF_ADDITIONAL_CHANCE);

            int maxAdds = percentBAF / 100; // Multiple of 100 are guaranteed BAFs

            // Calculate chance of an addition add based on the remainder
            if (Util.Chance(percentBAF % 100))
                maxAdds++;

            if (maxAdds > 0)
            {
                int numAdds = 0; // Number of mobs currently BAFed
                ushort range = BAFInitialRange; // How far away to look for friends

                // Try to bring closer friends before distant ones.
                while (numAdds < maxAdds && range <= BAFMaxRange)
                {
                    foreach (GameNPC npc in Body.GetNPCsInRadius(range))
                    {
                        if (numAdds >= maxAdds)
                            break;

                        // If it's a friend, have it attack
                        if (npc.IsFriend(Body) && npc.IsAggressive && npc.IsAvailable && npc.Brain is StandardMobBrain brain)
                        {
                            brain.CanBAF = false; // Mobs brought cannot bring friends of their own
                            GameLiving target;

                            if (victims != null && victims.Count > 0)
                                target = victims[Util.Random(0, victims.Count - 1)];
                            else
                                target = actualPuller;

                            brain.AddToAggroList(target, 0);
                            brain.AttackMostWanted();
                            numAdds++;
                        }
                    }

                    // Increase the range for finding friends to join the fight.
                    range *= 2;
                }
            }
        }

        #endregion Bring a Friend

        #region Spells

        public enum eCheckSpellType
        {
            Offensive,
            Defensive,
            CrowdControl
        }

        /// <summary>
        /// Checks if any spells need casting
        /// </summary>
        /// <param name="type">Which type should we go through and check for?</param>
        public virtual bool CheckSpells(eCheckSpellType type)
        {
            if (Body == null || Body.Spells == null || Body.Spells.Count <= 0)
                return false;

            bool casted = false;
            List<Spell> spellsToCast = new();

            //if (QueuedOffensiveSpell != null)
            //{
            //    if (CheckOffensiveSpells(QueuedOffensiveSpell))
            //    {
            //        casted = true;
            //        QueuedOffensiveSpell = null;
            //    }
            //}

            // Healers should heal whether in combat or out of it.
            if (Body.CanCastHealSpells)
            {
                GameLiving livingToHeal = null;

                int healThreshold = Properties.NPC_HEAL_THRESHOLD;
                int emergencyThreshold = healThreshold / 2;

                short numNeedHealing = 0;
                bool singleEmergency = false;
                bool groupEmergency = false;

                if (Body.Group != null)
                {
                    short healthPercent = 100;
                    short numEmergency = 0;

                    foreach (GameLiving groupMember in Body.Group.GetMembersInTheGroup())
                    {
                        if (groupMember.HealthPercent < healThreshold)
                        {
                            if (groupMember.HealthPercent < healthPercent)
                            {
                                healthPercent = groupMember.HealthPercent;
                                livingToHeal = groupMember;
                                numNeedHealing++;

                                if (groupMember.HealthPercent < emergencyThreshold)
                                    numEmergency++;
                            }
                        }
                    }

                    if (numEmergency == 1)
                        singleEmergency = true;
                    else if (numEmergency > Body.Group.GetMembersInTheGroup().Count / 2)
                        groupEmergency = true;
                }
                else
                {
                    if (Body.HealthPercent < healThreshold)
                    {
                        if (Body.HealthPercent < emergencyThreshold)
                            singleEmergency = true;

                        livingToHeal = Body;
                    }
                }
                // Just pick a rando for now.
                Spell spellToCast = Body.HealSpells[Util.Random(Body.HealSpells.Count - 1)];
                if (CheckHealSpells(spellToCast, numNeedHealing, singleEmergency, groupEmergency, livingToHeal))
                {
                    casted = true;
                }

            }

            if (!casted && type == eCheckSpellType.CrowdControl)
            {
                if (MimicBody.CanCastCrowdControlSpells)
                {
                    Body.TargetObject = MimicBody.Group.MimicGroup.CCTargets[Util.Random(MimicBody.Group.MimicGroup.CCTargets.Count - 1)];

                    foreach (Spell spell in MimicBody.CrowdControlSpells)
                    {
                        if (CanCastOffensiveSpell(spell) && !LivingHasEffect((GameLiving)Body.TargetObject, spell))
                            spellsToCast.Add(spell);
                    }

                    if (spellsToCast.Count > 0)
                    {
                        Spell spell = spellsToCast[Util.Random(spellsToCast.Count - 1)];

                        Body.TurnTo(Body.TargetObject);

                        casted = Body.CastSpell(spell, m_mobSpellLine);

                        if (casted)
                        {
                            MimicBody.Group.MimicGroup.CCTargets.Remove((GameLiving)Body.TargetObject);

                            if (spell.CastTime > 0)
                                Body.StopFollowing();
                            else if (Body.FollowTarget != Body.TargetObject)
                            {
                                Body.Follow(Body.TargetObject, spell.Range - 30, 5000);
                            }
                        }
                    }
                }
            }
            else if (!casted && type == eCheckSpellType.Defensive)
            {
                if (Body.CanCastMiscSpells)
                {
                    foreach (Spell spell in Body.MiscSpells)
                    {
                        if (CheckDefensiveSpells(spell))
                        {
                            casted = true;
                            break;
                        }
                    }
                }
            }
            else if (!casted && type == eCheckSpellType.Offensive)
            {
                if (MimicBody.CharacterClass.ID == (int)eCharacterClass.Cleric)
                {
                    if (!Util.Chance(Math.Max(5, Body.ManaPercent - 50)))
                        return false;
                }

                // Check instant spells, but only cast one to prevent spamming
                if (Body.CanCastInstantHarmfulSpells)
                {
                    foreach (Spell spell in Body.InstantHarmfulSpells)
                    {
                        if (CheckInstantOffensiveSpells(spell))
                            break;
                    }
                }

                if (Body.CanCastInstantMiscSpells)
                {
                    foreach (Spell spell in Body.InstantMiscSpells)
                    {
                        if (CheckInstantDefensiveSpells(spell))
                            break;
                    }
                }

                // TODO: Better nightshade casting logic. For now just make them melee but still use instants.
                if (MimicBody.CharacterClass.ID == (int)eCharacterClass.Nightshade)
                    return false;

                // TODO: This makes Thane and Valewalker use melee when in range rather than cast in all situations.
                //        but still use instants. Need to include other exceptions like maybe low health or endurance.
                if ((MimicBody.CanUsePositionalStyles || MimicBody.CanUseAnytimeStyles) && Body.IsWithinRadius(Body.TargetObject, 550))
                    return false;

                if (MimicBody.CanCastCrowdControlSpells)
                {
                    int ccChance = 50;

                    GameLiving livingTarget = Body.TargetObject as GameLiving;

                    if (livingTarget?.TargetObject == Body && Body.IsWithinRadius(Body.TargetObject, 500))
                        ccChance = 95;

                    if (Body.Group?.MimicGroup.CurrentTarget == Body.TargetObject)
                        ccChance = 0;

                    if (Util.Chance(ccChance))
                    {
                        foreach (Spell spell in MimicBody.CrowdControlSpells)
                        {
                            if (CanCastOffensiveSpell(spell) && !LivingHasEffect((GameLiving)Body.TargetObject, spell))
                                spellsToCast.Add(spell);
                        }
                    }
                }

                if (MimicBody.CanCastBolts && spellsToCast.Count < 1)
                {
                    foreach (Spell spell in MimicBody.BoltSpells)
                    {
                        if (CanCastOffensiveSpell(spell))
                            spellsToCast.Add(spell);
                    }
                }

                if (spellsToCast.Count < 1)
                {
                    foreach (Spell spell in Body.Spells)
                    {
                        if (CanCastOffensiveSpell(spell))
                            spellsToCast.Add(spell);
                    }
                }

                if (spellsToCast.Count > 0)
                {
                    Spell spellToCast = spellsToCast[Util.Random(spellsToCast.Count - 1)];

                    if (spellToCast.Uninterruptible || !Body.IsBeingInterrupted)
                        casted = CheckOffensiveSpells(spellToCast);
                    else if (!spellToCast.Uninterruptible && Body.IsBeingInterrupted)
                    {
                        if (MimicBody.CharacterClass.ClassType == eClassType.ListCaster)
                        {
                            Ability quickCast = Body.GetAbility(Abilities.Quickcast);

                            if (quickCast != null)
                            {
                                if (Body.GetSkillDisabledDuration(quickCast) <= 0)
                                {
                                    Body.DisableSkill(quickCast, 180000);

                                    // Give mimics a small bump in duration, they don't use it as well as humans.
                                    new QuickCastECSGameEffect(new ECSGameEffectInitParams(Body, QuickCastECSGameEffect.DURATION + 1000, 1));

                                    casted = CheckOffensiveSpells(spellToCast);

                                    //QueuedOffensiveSpell = spellToCast;
                                }
                            }
                        }
                    }
                }
            }

            return casted || Body.IsCasting;
        }

        protected bool CanCastOffensiveSpell(Spell spell)
        {
            if (Body.GetSkillDisabledDuration(spell) == 0)
            {
                if (spell.CastTime > 0)
                {
                    if (spell.Target is eSpellTarget.ENEMY or eSpellTarget.AREA or eSpellTarget.CONE)
                        return true;
                }
            }

            return false;
        }

        protected bool CanCastDefensiveSpell(Spell spell)
        {
            if (spell == null || spell.IsHarmful)
                return false;

            // Make sure we're currently able to cast the spell.
            if (spell.CastTime > 0 && Body.IsBeingInterrupted && !spell.Uninterruptible)
                return false;

            // Make sure the spell isn't disabled.
            if (spell.HasRecastDelay || Body.GetSkillDisabledDuration(spell) > 0)
                return false;

            return true;
        }

        protected bool CheckHealSpells(Spell spell, short numNeedHealing, bool singleEmergency, bool groupEmergency, GameLiving livingToHeal)
        {
            if (!CanCastDefensiveSpell(spell))
                return false;

            GameObject lastTarget = Body.TargetObject;
            Body.TargetObject = null;

            if (livingToHeal != null)
            {
                switch (spell.SpellType)
                {
                    case eSpellType.CombatHeal:
                    case eSpellType.Heal:
                    case eSpellType.HealOverTime:
                    case eSpellType.MercHeal:
                    case eSpellType.OmniHeal:
                    case eSpellType.PBAoEHeal:
                    case eSpellType.SpreadHeal:

                        if (spell.IsInstantCast)
                        {
                            if (groupEmergency || singleEmergency)
                            {
                                if (Body.IsWithinRadius(livingToHeal, spell.Range))
                                    Body.TargetObject = livingToHeal;
                                break;
                            }
                        }

                        if (spell.Target == eSpellTarget.GROUP && numNeedHealing < 2)
                            break;

                        if (spell.Target == eSpellTarget.SELF && numNeedHealing < 2)
                            break;

                        if (!LivingHasEffect(livingToHeal, spell) && Body.IsWithinRadius(livingToHeal, spell.Range))
                        {
                            Body.TargetObject = livingToHeal;
                            break;
                        }

                        break;
                }
            }

            if (Body.TargetObject != null)
            {

                Body.CastSpell(spell, m_mobSpellLine, false);
                return true;
            }

            Body.TargetObject = lastTarget;
            return false;
        }

        /// <summary>
        /// Checks defensive spells. Handles buffs, heals, etc.
        /// </summary>
        protected bool CheckDefensiveSpells(Spell spell)
        {
            if (!CanCastDefensiveSpell(spell))
                return false;

            bool casted = false;

            Body.TargetObject = null;

            // TODO: Instrument classes need special logic.
            if (spell.NeedInstrument)
            {
                switch (spell.SpellType)
                {
                    case eSpellType.PowerRegenBuff:
                        {
                            if (!Body.InCombat && !Body.IsMoving)
                            {
                                if (Body.Group != null)
                                {
                                    if (Body.Group.GetMembersInTheGroup().Any(groupMember => groupMember.MaxMana > 0 && groupMember.ManaPercent < 80) && !LivingHasEffect(Body, spell))
                                    {
                                        // log.Info($"Power Regen Buff Group: {spell.Name}, Type: {spell.SpellType}");
                                        Body.SwitchWeapon(eActiveWeaponSlot.Distance);
                                        Body.TargetObject = Body;
                                    }
                                }
                                else if (Body.ManaPercent < 75 && !LivingHasEffect(Body, spell))
                                {
                                    // log.Info($"Self Power Regen Buff Group: {spell.Name}, Type: {spell.SpellType}");
                                    Body.SwitchWeapon(eActiveWeaponSlot.Distance);
                                    Body.TargetObject = Body;
                                }
                                else if (LivingHasEffect(Body, spell))
                                {
                                    Body.TargetObject = Body;
                                }
                            }
                        }
                        break;

                    case eSpellType.HealthRegenBuff:

                        if (!Body.InCombat && !Body.IsMoving && !LivingHasEffect(Body, spell))
                        {
                            ECSGameEffect powerRegen = EffectListService.GetEffectOnTarget(Body, eEffect.Pulse, eSpellType.PowerRegenBuff);

                            if (powerRegen == null)
                            {
                                if (!Body.InCombat && !Body.IsMoving)
                                {
                                    if (Body.Group != null)
                                    {
                                        if (Body.Group.GetMembersInTheGroup().Any(groupMember => groupMember.HealthPercent < 80) && !LivingHasEffect(Body, spell))
                                        {
                                            // log.Info($"Health Regen Buff Group: {spell.Name}, Type: {spell.SpellType}");
                                            Body.SwitchWeapon(eActiveWeaponSlot.Distance);
                                            Body.TargetObject = Body;
                                        }
                                    }
                                    else if (Body.HealthPercent < 80 && !LivingHasEffect(Body, spell))
                                    {
                                        // log.Info($"Self Health Regen Buff Group: {spell.Name}, Type: {spell.SpellType}");
                                        Body.SwitchWeapon(eActiveWeaponSlot.Distance);
                                        Body.TargetObject = Body;
                                    }
                                    else if (LivingHasEffect(Body, spell))
                                    {
                                        Body.TargetObject = Body;
                                    }
                                }
                            }
                        }
                        break;

                    case eSpellType.EnduranceRegenBuff:

                        if (Body.InCombat)
                        {
                            if (Body.Group != null)
                            {
                                if (Body.Group.GetMembersInTheGroup().Any(groupMember => groupMember.EndurancePercent < 95) && !LivingHasEffect(Body, spell))
                                {
                                    // log.Info($"Endurance Regen Buff Group: {spell.Name}, Type: {spell.SpellType}");
                                    Body.SwitchWeapon(eActiveWeaponSlot.Distance);
                                    Body.TargetObject = Body;
                                }
                            }
                        }

                        break;

                    case eSpellType.SpeedEnhancement:
                        {
                            if (!Body.InCombat && Body.IsMoving && !LivingHasEffect(Body, spell))
                            {
                                // log.Info($"Speed Enhance: {spell.Name}, Type: {spell.SpellType}");
                                Body.SwitchWeapon(eActiveWeaponSlot.Distance);
                                Body.TargetObject = Body;
                            }
                        }
                        break;
                }
            }
            else

                switch (spell.SpellType)
                {

                    #region Summon

                    case eSpellType.SummonMinion:
                    case eSpellType.SummonUnderhill:
                    case eSpellType.SummonDruidPet:
                    case eSpellType.SummonCommander:
                    case eSpellType.SummonSimulacrum:
                    case eSpellType.SummonSpiritFighter:
                        if (Body.ControlledBrain != null)
                            break;

                        if (Body.ControlledNpcList == null)
                            Body.SetControlledBrain(Body.ControlledBrain);
                        else
                        {
                            //Let's check to see if the list is full - if it is, we can't cast another minion.
                            //If it isn't, let them cast.
                            IControlledBrain[] icb = Body.ControlledNpcList;
                            int numberofpets = 0;

                            for (int i = 0; i < icb.Length; i++)
                            {
                                if (icb[i] != null)
                                    numberofpets++;
                            }

                            if (numberofpets >= icb.Length)
                                break;
                        }

                        Body.TargetObject = Body;

                        break;

                    case eSpellType.PetSpell:
                        if (Body.ControlledBrain != null)
                        {
                            if (Body.ControlledBrain.Body != null)
                            {
                                if (spell.Target == eSpellTarget.PET && !LivingHasEffect(Body.ControlledBrain.Body, spell))
                                    Body.TargetObject = Body.ControlledBrain.Body;
                            }
                        }
                        break;

                    case eSpellType.Pet:
                        break;

                    #endregion Summon

                    #region Pulse

                    case eSpellType.SpeedEnhancement:

                        //checking for cabalist pet speed buff
                        if (spell.Target == eSpellTarget.PET)
                        {
                            //BRENT TODO this isn't casting on the pet for some reason, will need to check later
                            //if (Body.ControlledBrain != null)
                            //{
                            //    if (Body.ControlledBrain.Body != null)
                            //    {
                            //        if (!Body.ControlledBrain.Body.InCombat && !LivingHasEffect(Body.ControlledBrain.Body, spell))
                            //            Body.TargetObject = Body.ControlledBrain.Body;
                            //    }
                            //}
                            break;

                        }
                        if (Body.Group != null)
                        {
                            if (spell.Target == eSpellTarget.REALM || spell.Target == eSpellTarget.GROUP)
                            {
                                foreach (GameLiving groupMember in Body.Group.GetMembersInTheGroup())
                                {
                                    if (groupMember != Body)
                                    {
                                        if (!LivingHasEffect(groupMember, spell) && Body.IsWithinRadius(groupMember, spell.Range) && groupMember.IsAlive)
                                        {
                                            Body.TargetObject = groupMember;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                        else
                        //if the body is not a cab, cast on self
                        if (Body is not MimicCabalist)
                        {
                            if (!Body.InCombat && !LivingHasEffect(Body, spell))
                            {
                                Body.TargetObject = Body;
                            }
                        }
                        break;

                    #endregion Pulse

                    #region Buffs

                    case eSpellType.AblativeArmor:
                    case eSpellType.AcuityBuff:
                    case eSpellType.AFHitsBuff:
                    case eSpellType.AllMagicResistBuff:
                    case eSpellType.ArmorAbsorptionBuff:
                    case eSpellType.ArmorFactorBuff:
                    case eSpellType.BodyResistBuff:
                    case eSpellType.BodySpiritEnergyBuff:
                    case eSpellType.Buff:
                    case eSpellType.CelerityBuff:
                    case eSpellType.ColdResistBuff:
                    case eSpellType.CombatSpeedBuff:
                    case eSpellType.ConstitutionBuff:
                    case eSpellType.CourageBuff:
                    case eSpellType.CrushSlashTrustBuff:
                    case eSpellType.DexterityBuff:
                    case eSpellType.DexterityQuicknessBuff:
                    case eSpellType.EffectivenessBuff:
                    case eSpellType.EnduranceRegenBuff:
                    case eSpellType.EnergyResistBuff:
                    case eSpellType.FatigueConsumptionBuff:
                    case eSpellType.FlexibleSkillBuff:
                    case eSpellType.HasteBuff:
                    case eSpellType.HealthRegenBuff:
                    case eSpellType.HeatColdMatterBuff:
                    case eSpellType.HeatResistBuff:
                    case eSpellType.HeroismBuff:
                    case eSpellType.KeepDamageBuff:
                    case eSpellType.MagicResistBuff:
                    case eSpellType.MatterResistBuff:
                    case eSpellType.MeleeDamageBuff:
                    case eSpellType.MesmerizeDurationBuff:
                    case eSpellType.MLABSBuff:
                    case eSpellType.PaladinArmorFactorBuff:
                    case eSpellType.ParryBuff:
                    case eSpellType.PowerHealthEnduranceRegenBuff:
                    case eSpellType.PowerRegenBuff:
                    case eSpellType.SpiritResistBuff:
                    case eSpellType.StrengthBuff:
                    case eSpellType.StrengthConstitutionBuff:
                    case eSpellType.SuperiorCourageBuff:
                    case eSpellType.ToHitBuff:
                    case eSpellType.WeaponSkillBuff:
                    case eSpellType.DamageAdd:
                    case eSpellType.OffensiveProc:
                    case eSpellType.DefensiveProc:
                    case eSpellType.DamageShield:
                    case eSpellType.Bladeturn:
                        {
                            if (spell.Target == eSpellTarget.PET)
                                break;

                            // Buff self
                            if (!LivingHasEffect(Body, spell))
                            {
                                Body.TargetObject = Body;
                                break;
                            }

                            if (Body.Group != null)
                            {
                                if (spell.Target == eSpellTarget.REALM || spell.Target == eSpellTarget.GROUP)
                                {
                                    foreach (GameLiving groupMember in Body.Group.GetMembersInTheGroup())
                                    {
                                        if (groupMember != Body)
                                        {
                                            if (!LivingHasEffect(groupMember, spell) && Body.IsWithinRadius(groupMember, spell.Range) && groupMember.IsAlive)
                                            {
                                                Body.TargetObject = groupMember;
                                                break;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        break;

                    #endregion Buffs

                    #region Cure Disease/Poison/Mezz

                    case eSpellType.CureDisease:

                        //Cure self
                        if (Body.IsDiseased)
                        {
                            Body.TargetObject = Body;
                            break;
                        }

                        // Cure group members
                        if (Body.Group != null)
                        {
                            foreach (GameLiving groupMember in Body.Group.GetMembersInTheGroup())
                            {
                                if (groupMember != Body)
                                {
                                    if (groupMember.IsDiseased && Body.IsWithinRadius(groupMember, spell.Range))
                                    {
                                        Body.TargetObject = groupMember;
                                        break;
                                    }
                                }
                            }
                        }
                        break;

                    case eSpellType.CurePoison:
                        //Cure self
                        if (LivingIsPoisoned(Body))
                        {
                            Body.TargetObject = Body;
                            break;
                        }

                        // Cure group members
                        if (Body.Group != null)
                        {
                            foreach (GameLiving groupMember in Body.Group.GetMembersInTheGroup())
                            {
                                if (groupMember != Body)
                                {
                                    if (LivingIsPoisoned(groupMember) && Body.IsWithinRadius(groupMember, spell.Range))
                                    {
                                        Body.TargetObject = groupMember;
                                        break;
                                    }
                                }
                            }
                        }
                        break;

                    case eSpellType.CureMezz:
                        if (Body.Group != null)
                        {
                            foreach (GameLiving groupMember in Body.Group.GetMembersInTheGroup())
                            {
                                if (groupMember != Body)
                                {
                                    if (groupMember.IsMezzed && Body.IsWithinRadius(groupMember, spell.Range))
                                    {
                                        Body.TargetObject = groupMember;
                                        break;
                                    }
                                }
                            }
                        }
                        break;

                    case eSpellType.CureNearsightCustom:
                        if (Body.Group != null)
                        {
                            foreach (GameLiving groupMember in Body.Group.GetMembersInTheGroup())
                            {
                                if (groupMember != Body)
                                {
                                    if (LivingHasEffect(groupMember, spell) && Body.IsWithinRadius(groupMember, spell.Range))
                                    {
                                        Body.TargetObject = groupMember;
                                        break;
                                    }
                                }
                            }
                        }
                        break;

                    #endregion Cure Disease/Poison/Mezz

                    #region Charms

                    case eSpellType.Charm:
                        break;

                    #endregion Charms

                    case eSpellType.Resurrect:

                        if (Body.Group != null)
                        {
                            foreach (GameLiving groupMember in Body.Group.GetMembersInTheGroup())
                            {
                                if (!groupMember.IsAlive && Body.IsWithinRadius(groupMember, spell.Range))
                                {
                                    Body.TargetObject = groupMember;
                                    break;
                                }
                            }
                        }
                        break;

                    case eSpellType.LifeTransfer:

                        if (Body.Group != null)
                        {
                            if (Body.HealthPercent > 50)
                            {
                                GameLiving livingToHeal = null;
                                int threshold = Properties.NPC_HEAL_THRESHOLD / 2;
                                int lowestHealth = 100;

                                foreach (GameLiving groupMember in Body.Group.GetMembersInTheGroup())
                                {
                                    if (groupMember.HealthPercent < threshold)
                                    {
                                        if (groupMember.HealthPercent < lowestHealth)
                                        {
                                            livingToHeal = groupMember;
                                            lowestHealth = groupMember.HealthPercent;
                                        }
                                    }
                                }

                                if (livingToHeal != null && livingToHeal.IsAlive)
                                    Body.TargetObject = livingToHeal;
                            }
                        }

                        break;

                    case eSpellType.PetConversion:
                        break;

                    default:
                        log.Warn($"CheckDefensiveSpells() encountered an unknown spell type [{spell.SpellType}] for {Body?.Name}");
                        break;
                }


            if (Body?.TargetObject != null)
            {
                casted = Body.CastSpell(spell, m_mobSpellLine, false);
            }

            return casted;
        }

        /// <summary>
        /// Checks offensive spells.  Handles dds, debuffs, etc.
        /// </summary>
        protected virtual bool CheckOffensiveSpells(Spell spell, bool quickCast = false)
        {
            if (spell.SpellType == eSpellType.Charm || spell.SpellType == eSpellType.Amnesia || spell.SpellType == eSpellType.Confusion)
                return false;

            if (spell.SpellType == eSpellType.Taunt)
                return false;

            if (spell.NeedInstrument && Body.ActiveWeaponSlot != eActiveWeaponSlot.Distance)
                Body.SwitchWeapon(eActiveWeaponSlot.Distance);

            if (!Body.IsWithinRadius(Body.TargetObject, spell.Range))
            {
                Body.Follow(Body.TargetObject, spell.Range - 30, 5000);
                QueuedOffensiveSpell = spell;
                return false;
            }

            bool casted = false;

            if (Body.TargetObject is GameLiving living && (spell.Duration == 0 || !LivingHasEffect(living, spell) || spell.SpellType == eSpellType.DirectDamageWithDebuff || spell.SpellType == eSpellType.DamageSpeedDecrease))
            {
                if (Body.TargetObject != Body)
                    Body.TurnTo(Body.TargetObject);

                casted = Body.CastSpell(spell, m_mobSpellLine);

                if (casted)
                {
                    if (spell.CastTime > 0)
                        Body.StopFollowing();
                    else if (Body.FollowTarget != Body.TargetObject)
                    {
                        Body.Follow(Body.TargetObject, spell.Range - 30, spell.Range + 500);
                    }
                }
            }

            return casted;
        }

        protected virtual bool CheckInstantDefensiveSpells(Spell spell)
        {
            if (spell.HasRecastDelay && Body.GetSkillDisabledDuration(spell) > 0)
                return false;

            GameObject lastTarget = Body.TargetObject;
            Body.TargetObject = null;

            switch (spell.SpellType)
            {
                case eSpellType.SavageCrushResistanceBuff:
                case eSpellType.SavageSlashResistanceBuff:
                case eSpellType.SavageThrustResistanceBuff:
                case eSpellType.SavageCombatSpeedBuff:
                case eSpellType.SavageDPSBuff:
                case eSpellType.SavageParryBuff:
                case eSpellType.SavageEvadeBuff:

                    //if (Body.HealthPercent < 10)
                    //    break;

                    if (spell.SpellType == eSpellType.SavageCrushResistanceBuff ||
                        spell.SpellType == eSpellType.SavageSlashResistanceBuff ||
                        spell.SpellType == eSpellType.SavageThrustResistanceBuff &&
                        !CheckSavageResistSpell(spell.SpellType))
                        break;

                    if (!LivingHasEffect(Body, spell))
                        Body.TargetObject = Body;

                    break;

                case eSpellType.CombatHeal:
                case eSpellType.DamageAdd:
                case eSpellType.ArmorFactorBuff:
                case eSpellType.DexterityQuicknessBuff:
                case eSpellType.EnduranceRegenBuff:
                case eSpellType.CombatSpeedBuff:
                case eSpellType.AblativeArmor:
                case eSpellType.Bladeturn:
                case eSpellType.OffensiveProc:
                case eSpellType.MatterResistBuff:
                case eSpellType.ColdResistBuff:
                case eSpellType.HeatResistBuff:
                case eSpellType.EnergyResistBuff:
                case eSpellType.SpiritResistBuff:
                case eSpellType.BodyResistBuff:
                case eSpellType.SummonHunterPet:

                    if (spell.SpellType == eSpellType.CombatSpeedBuff)
                    {
                        if (!Body.IsWithinRadius(lastTarget, Body.AttackRange))
                            break;
                    }

                    if (!LivingHasEffect(Body, spell))
                    {
                        Body.TargetObject = Body;
                    }

                    break;
            }

            if (Body.TargetObject != null)
            {
                Body.CastSpell(spell, m_mobSpellLine);
                Body.TargetObject = lastTarget;
                return true;
            }

            Body.TargetObject = lastTarget;
            return false;
        }

        /// <summary>
        /// Checks Instant Spells.  Handles Taunts, shouts, stuns, etc.
        /// </summary>
        protected virtual bool CheckInstantOffensiveSpells(Spell spell)
        {
            if (spell.HasRecastDelay && Body.GetSkillDisabledDuration(spell) > 0)
                return false;

            GameObject lastTarget = Body.TargetObject;
            Body.TargetObject = null;

            switch (spell.SpellType)
            {
                #region Enemy Spells

                case eSpellType.Taunt:

                    if (Body.Group?.MimicGroup.MainTank == Body)
                        Body.TargetObject = lastTarget;

                    break;

                case eSpellType.DirectDamage:
                case eSpellType.Lifedrain:
                case eSpellType.DexterityDebuff:
                case eSpellType.DexterityQuicknessDebuff:
                case eSpellType.StrengthDebuff:
                case eSpellType.StrengthConstitutionDebuff:
                case eSpellType.CombatSpeedDebuff:
                case eSpellType.DamageOverTime:
                case eSpellType.MeleeDamageDebuff:
                case eSpellType.AllStatsPercentDebuff:
                case eSpellType.CrushSlashThrustDebuff:
                case eSpellType.EffectivenessDebuff:
                case eSpellType.Disease:
                case eSpellType.Stun:
                case eSpellType.Mez:
                case eSpellType.Mesmerize:

                    if (spell.IsPBAoE && !Body.IsWithinRadius(lastTarget, spell.Radius))
                        break;

                    // Try to limit the debuffs cast to save mana and time spent doing so.
                    if (spell.IsInstantCast && MimicBody.CharacterClass.ClassType == eClassType.ListCaster)
                    {
                        if (!Util.Chance(20))
                            break;
                    }

                    if (!LivingHasEffect(lastTarget as GameLiving, spell))
                    {
                        Body.TargetObject = lastTarget;
                    }
                    break;

                    #endregion Enemy Spells
            }

            ECSGameEffect pulseEffect = EffectListService.GetPulseEffectOnTarget(Body, spell);

            if (pulseEffect != null)
                return false;

            if (Body.TargetObject != null && (spell.Duration == 0 || (Body.TargetObject is GameLiving living && !(LivingHasEffect(living, spell)))))
            {
                if (Body.TargetObject != Body)
                    Body.TurnTo(Body.TargetObject);

                Body.CastSpell(spell, m_mobSpellLine, true);
                Body.TargetObject = lastTarget;
                return true;
            }

            Body.TargetObject = lastTarget;
            return false;
        }

        protected virtual bool CheckSavageResistSpell(eSpellType spellType)
        {
            eDamageType damageType = eDamageType.Natural;

            switch (spellType)
            {
                case eSpellType.SavageCrushResistanceBuff:
                    damageType = eDamageType.Crush;
                    break;

                case eSpellType.SavageSlashResistanceBuff:
                    damageType = eDamageType.Slash;
                    break;

                case eSpellType.SavageThrustResistanceBuff:
                    damageType = eDamageType.Thrust;
                    break;
            }

            if (Body.attackComponent.Attackers.Count > 0)
            {
                foreach (var attacker in Body.attackComponent.Attackers)
                {
                    if (attacker.Key.ActiveWeapon != null)
                    {
                        if (attacker.Key.ActiveWeapon.Type_Damage != 0 && (int)damageType == attacker.Key.ActiveWeapon.Type_Damage)
                            return true;
                    }
                    else if (attacker.Key is GameNPC npc)
                    {
                        if (npc.MeleeDamageType == damageType)
                            return true;
                    }
                }
            }

            return false;
        }

        protected static SpellLine m_mobSpellLine = SkillBase.GetSpellLine(GlobalSpellsLines.Mob_Spells);
        //protected static SpellLine m_MimicSpellLine = SkillBase.GetSpellLine("MimicSpellLine");

        /// <summary>
        /// Checks if the living target has a spell effect.
        /// Only to be used for spell casting purposes.
        /// </summary>
        /// <returns>True if the living has the effect of will receive it by our current spell.</returns>
        public bool LivingHasEffect(GameLiving target, Spell spell)
        {
            if (target == null)
                return true;

            ISpellHandler spellHandler = Body.castingComponent.SpellHandler;

            // If we're currently casting 'spell' on 'target', assume it already has the effect.
            // This allows spell queuing while preventing casting on the same target more than once.
            if (spellHandler != null && spellHandler.Spell.ID == spell.ID && spellHandler.Target == target)
                return true;

            // May not be the right place for that, but without that check NPCs with more than one offensive or defensive proc will only buff themselves once.
            if (spell.SpellType is eSpellType.OffensiveProc or eSpellType.DefensiveProc)
            {
                if (target.effectListComponent.Effects.TryGetValue(EffectService.GetEffectFromSpell(spell, m_mobSpellLine.IsBaseLine), out List<ECSGameEffect> existingEffects))
                {
                    if (existingEffects.FirstOrDefault(e => e.SpellHandler.Spell.ID == spell.ID || (spell.EffectGroup > 0 && e.SpellHandler.Spell.EffectGroup == spell.EffectGroup)) != null)
                        return true;
                }

                return false;
            }

            ECSGameEffect pulseEffect = EffectListService.GetPulseEffectOnTarget(target, spell);

            if (pulseEffect != null)
                return true;

            eEffect spellEffect = EffectService.GetEffectFromSpell(spell, m_mobSpellLine.IsBaseLine);
            ECSGameEffect effect = EffectListService.GetEffectOnTarget(target, spellEffect);

            if (effect != null)
                return true;

            eEffect immunityToCheck = eEffect.Unknown;

            switch (spellEffect)
            {
                case eEffect.Stun:
                    {
                        immunityToCheck = eEffect.StunImmunity;
                        break;
                    }
                case eEffect.Mez:
                    {
                        immunityToCheck = eEffect.MezImmunity;
                        break;
                    }
                case eEffect.Snare:
                case eEffect.MovementSpeedDebuff:
                case eEffect.MeleeSnare:
                    {
                        immunityToCheck = eEffect.SnareImmunity;
                        break;
                    }
                case eEffect.Nearsight:
                    {
                        immunityToCheck = eEffect.NearsightImmunity;
                        break;
                    }
            }

            return immunityToCheck != eEffect.Unknown && EffectListService.GetEffectOnTarget(target, immunityToCheck) != null;
        }

        protected static bool LivingIsPoisoned(GameLiving target)
        {
            foreach (IGameEffect effect in target.EffectList)
            {
                //If the effect we are checking is not a gamespelleffect keep going
                if (effect is not GameSpellEffect)
                    continue;

                GameSpellEffect spellEffect = effect as GameSpellEffect;

                // if this is a DOT then target is poisoned
                if (spellEffect.Spell.SpellType == eSpellType.DamageOverTime)
                    return true;
            }

            return false;
        }

        #endregion Spells

        #region DetectDoor

        public virtual void DetectDoor()
        {
            ushort range = (ushort)(ThinkInterval / 800 * Body.CurrentWaypoint.MaxSpeed);

            foreach (GameDoorBase door in Body.CurrentRegion.GetDoorsInRadius(Body, range))
            {
                if (door is GameKeepDoor)
                {
                    if (Body.Realm != door.Realm)
                        return;

                    door.Open();
                    //Body.Say("GameKeep Door is near by");
                    //somebody can insert here another action for GameKeep Doors
                    return;
                }
                else
                {
                    door.Open();
                    return;
                }
            }

            return;
        }

        #endregion DetectDoor
    }
}