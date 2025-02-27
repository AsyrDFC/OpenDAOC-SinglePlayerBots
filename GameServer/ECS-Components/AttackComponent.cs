﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS.Effects;
using DOL.GS.Keeps;
using DOL.GS.PacketHandler;
using DOL.GS.RealmAbilities;
using DOL.GS.Scripts;
using DOL.GS.ServerProperties;
using DOL.GS.SkillHandler;
using DOL.GS.Spells;
using DOL.GS.Styles;
using DOL.Language;
using static DOL.GS.GameLiving;
using static DOL.GS.GameObject;

namespace DOL.GS
{
    public class AttackComponent : IManagedEntity
    {
        private static int CHECK_ATTACKERS_INTERVAL = 1000;

        public GameLiving owner;
        public MimicWeaponAction weaponAction;
        public AttackAction attackAction;
        public EntityManagerId EntityManagerId { get; set; } = new(EntityManager.EntityType.AttackComponent, false);

        /// <summary>
        /// Returns the list of attackers
        /// </summary>
        public ConcurrentDictionary<GameLiving, long> Attackers { get; private set; } = new();

        private ECSGameTimer _attackersCheckTimer;
        private object _attackersCheckTimerLock = new();

        public void AddAttacker(GameLiving target)
        {
            if (target == owner)
                return;

            lock (_attackersCheckTimerLock)
            {
                if (!_attackersCheckTimer.IsAlive)
                {
                   _attackersCheckTimer.Interval = CHECK_ATTACKERS_INTERVAL;
                   _attackersCheckTimer.Start();
                }
            }

            long until = GameLoop.GameLoopTime + 5000; // Use interrupt duration instead?
            Attackers.AddOrUpdate(target, until, (key, oldValue) => until);
        }

        private int CheckAttackers(ECSGameTimer timer)
        {
            foreach (var pair in Attackers)
            {
                if (pair.Value < GameLoop.GameLoopTime)
                    Attackers.TryRemove(pair);
            }

            return Attackers.IsEmpty ? 0 : CHECK_ATTACKERS_INTERVAL;
        }

        /// <summary>
        /// The target that was passed when 'StartAttackRequest' was called and the request accepted.
        /// </summary>
        private GameObject m_startAttackTarget;

        /// <summary>
        /// Actually a boolean. Use 'StartAttackRequested' to preserve thread safety.
        /// </summary>
        private long m_startAttackRequested;

        public bool StartAttackRequested
        {
            get => Interlocked.Read(ref m_startAttackRequested) == 1;
            set => Interlocked.Exchange(ref m_startAttackRequested, Convert.ToInt64(value));
        }

        public AttackComponent(GameLiving owner)
        {
            this.owner = owner;
            _attackersCheckTimer = new(owner, CheckAttackers);
        }

        public void Tick(long time)
        {
            if (StartAttackRequested)
            {
                StartAttackRequested = false;
                StartAttack();
            }

            attackAction?.Tick(time);

            if (weaponAction?.AttackFinished == true)
                weaponAction = null;

            if (weaponAction is null && attackAction is null && !owner.InCombat)
                EntityManager.Remove(this);
        }

        /// <summary>
        /// The chance for a critical hit
        /// </summary>
        /// <param name="weapon">attack weapon</param>
        public virtual int AttackCriticalChance(WeaponAction action, DbInventoryItem weapon)
        {
            if (owner is GamePlayer playerOwner)
            {
                if (weapon != null)
                {
                    if (weapon.Item_Type != Slot.RANGED)
                        return playerOwner.GetModified(eProperty.CriticalMeleeHitChance);
                    else
                    {
                        if (action.RangedAttackType == eRangedAttackType.Critical)
                            return 0;
                        else
                            return playerOwner.GetModified(eProperty.CriticalArcheryHitChance);
                    }
                }

                // Base of 10% critical chance.
                return 10;
            }

            /// [Atlas - Takii] Wild Minion Implementation. We don't want any non-pet NPCs to crit.
            /// We cannot reliably check melee vs ranged here since archer pets don't necessarily have a proper weapon with the correct slot type assigned.
            /// Since Wild Minion is the only way for pets to crit and we (currently) want it to affect melee/ranged/spells, we can just rely on the Melee crit chance even for archery attacks
            /// and as a result we don't actually need to detect melee vs ranged to end up with the correct behavior since all attack types will have the same % chance to crit in the end.
            if (owner is GameNPC npc)
            {
                // Player-Summoned pet.
                if (npc is GameSummonedPet summonedPet && summonedPet.Owner is GamePlayer)
                    return npc.GetModified(eProperty.CriticalMeleeHitChance);

                // Charmed Pet.
                if (npc.Brain is IControlledBrain charmedPetBrain && charmedPetBrain.GetPlayerOwner() != null)
                    return npc.GetModified(eProperty.CriticalMeleeHitChance);
            }

            return 0;
        }

        /// <summary>
        /// Returns the damage type of the current attack
        /// </summary>
        /// <param name="weapon">attack weapon</param>
        public virtual eDamageType AttackDamageType(DbInventoryItem weapon)
        {
            if (owner is GamePlayer || owner is CommanderPet)
            {
                var p = owner as GamePlayer;

                if (weapon == null)
                    return eDamageType.Natural;

                switch ((eObjectType) weapon.Object_Type)
                {
                    case eObjectType.Crossbow:
                    case eObjectType.Longbow:
                    case eObjectType.CompositeBow:
                    case eObjectType.RecurvedBow:
                    case eObjectType.Fired:
                        DbInventoryItem ammo = p.rangeAttackComponent.Ammo;

                        if (ammo == null)
                            return (eDamageType) weapon.Type_Damage;

                        return (eDamageType) ammo.Type_Damage;
                    case eObjectType.Shield:
                        return eDamageType.Crush; // TODO: shields do crush damage (!) best is if Type_Damage is used properly
                    default:
                        return (eDamageType) weapon.Type_Damage;
                }
            }
            else if (owner is GameNPC)
                return (owner as GameNPC).MeleeDamageType;
            else
                return eDamageType.Natural;
        }

        /// <summary>
        /// Gets the attack-state of this living
        /// </summary>
        public virtual bool AttackState { get; set; }

        /// <summary>
        /// Gets which weapon was used for the last dual wield attack
        /// 0: right (or non dual wield user), 1: left, 2: both
        /// </summary>
        public int UsedHandOnLastDualWieldAttack { get; set; }

        /// <summary>
        /// Returns this attack's range
        /// </summary>
        public virtual int AttackRange
        {
            /* tested with:
            staff					= 125-130
            sword			   		= 126-128.06
            shield (Numb style)		= 127-129
            polearm	(Impale style)	= 127-130
            mace (Daze style)		= 127.5-128.7
            Think it's safe to say that it never changes; different with mobs. */

            get
            {
                if (owner is GamePlayer)
                {
                    DbInventoryItem weapon = owner.ActiveWeapon;

                    if (weapon == null)
                        return 0;

                    var player = owner as GamePlayer;
                    GameLiving target = player.TargetObject as GameLiving;

                    // TODO: Change to real distance of bows.
                    if (weapon.SlotPosition == (int)eInventorySlot.DistanceWeapon)
                    {
                        double range;

                        switch ((eObjectType) weapon.Object_Type)
                        {
                            case eObjectType.Longbow:
                                range = 1760;
                                break;
                            case eObjectType.RecurvedBow:
                                range = 1680;
                                break;
                            case eObjectType.CompositeBow:
                                range = 1600;
                                break;
                            case eObjectType.Thrown:
                                range = 1160;
                                if (weapon.Name.ToLower().Contains("weighted"))
                                    range = 1450;
                                break;
                            default:
                                range = 1200;
                                break; // Shortbow, crossbow, throwing.
                        }

                        range = Math.Max(32, range * player.GetModified(eProperty.ArcheryRange) * 0.01);
                        DbInventoryItem ammo = player.rangeAttackComponent.Ammo;

                        if (ammo != null)
                            switch ((ammo.SPD_ABS >> 2) & 0x3)
                            {
                                case 0:
                                    range *= 0.85;
                                    break; // Clout -15%
                                //case 1:
                                //  break; // (none) 0%
                                case 2:
                                    range *= 1.15;
                                    break; // Doesn't exist on live
                                case 3:
                                    range *= 1.25;
                                    break; // Flight +25%
                            }

                        if (target != null)
                            range += Math.Min((player.Z - target.Z) / 2.0, 500);
                        if (range < 32)
                            range = 32;

                        return (int)range;
                    }


                    // int meleeRange = 128;
                    int meleeRange = 150; // Increase default melee range to 150 to help with higher latency players.

                    if (target is GameKeepComponent)
                        meleeRange += 150;
                    else
                    {
                        if (target != null && target.IsMoving)
                            meleeRange += 32;
                        if (player.IsMoving)
                            meleeRange += 32;
                    }

                    return meleeRange;
                }
                else
                {
                    if (owner.ActiveWeaponSlot == eActiveWeaponSlot.Distance)
                        return Math.Max(32, (int) (2000.0 * owner.GetModified(eProperty.ArcheryRange) * 0.01));

                    return 200;
                }
            }
        }

        /// <summary>
        /// Gets the current attackspeed of this living in milliseconds
        /// </summary>
        /// <returns>effective speed of the attack. average if more than one weapon.</returns>
        public virtual int AttackSpeed(DbInventoryItem mainWeapon, DbInventoryItem leftWeapon = null)
        {
            if (owner is GamePlayer player)
            {
                if (mainWeapon == null)
                    return 0;

                double speed = 0;
                bool bowWeapon = false;

                // If leftWeapon is null even on a dual wield attack, use the mainWeapon instead
                switch (UsedHandOnLastDualWieldAttack)
                {
                    case 2:
                        speed = mainWeapon.SPD_ABS;
                        if (leftWeapon != null)
                        {
                            speed += leftWeapon.SPD_ABS;
                            speed /= 2;
                        }
                        break;
                    case 1:
                        speed = leftWeapon != null ? leftWeapon.SPD_ABS : mainWeapon.SPD_ABS;
                        break;
                    case 0:
                        speed = mainWeapon.SPD_ABS;
                        break;
                }

                if (speed == 0)
                    return 0;

                switch (mainWeapon.Object_Type)
                {
                    case (int) eObjectType.Fired:
                    case (int) eObjectType.Longbow:
                    case (int) eObjectType.Crossbow:
                    case (int) eObjectType.RecurvedBow:
                    case (int) eObjectType.CompositeBow:
                        bowWeapon = true;
                        break;
                }

                int qui = Math.Min(250, player.Quickness); //250 soft cap on quickness

                if (bowWeapon)
                {
                    if (Properties.ALLOW_OLD_ARCHERY)
                    {
                        //Draw Time formulas, there are very many ...
                        //Formula 2: y = iBowDelay * ((100 - ((iQuickness - 50) / 5 + iMasteryofArcheryLevel * 3)) / 100)
                        //Formula 1: x = (1 - ((iQuickness - 60) / 500 + (iMasteryofArcheryLevel * 3) / 100)) * iBowDelay
                        //Table a: Formula used: drawspeed = bowspeed * (1-(quickness - 50)*0.002) * ((1-MoA*0.03) - (archeryspeedbonus/100))
                        //Table b: Formula used: drawspeed = bowspeed * (1-(quickness - 50)*0.002) * (1-MoA*0.03) - ((archeryspeedbonus/100 * basebowspeed))

                        //For now use the standard weapon formula, later add ranger haste etc.
                        speed *= (1.0 - (qui - 60) * 0.002);
                        double percent = 0;
                        // Calcul ArcherySpeed bonus to substract
                        percent = speed * 0.01 * player.GetModified(eProperty.ArcherySpeed);
                        // Apply RA difference
                        speed -= percent;
                        //log.Debug("speed = " + speed + " percent = " + percent + " eProperty.archeryspeed = " + GetModified(eProperty.ArcherySpeed));

                        if (owner.rangeAttackComponent.RangedAttackType == eRangedAttackType.Critical) 
                            speed = speed * 2 - (player.GetAbilityLevel(Abilities.Critical_Shot) - 1) * speed / 10;
                    }
                    else
                    {
                        // no archery bonus
                        speed *= (1.0 - (qui - 60) * 0.002);
                    }
                }
                else
                {
                    // TODO use haste
                    //Weapon Speed*(1-(Quickness-60)/500]*(1-Haste)
                    speed *= ((1.0 - (qui - 60) * 0.002) * 0.01 * player.GetModified(eProperty.MeleeSpeed));
                    //Console.WriteLine($"Speed after {speed} quiMod {(1.0 - (qui - 60) * 0.002)} melee speed {0.01 * p.GetModified(eProperty.MeleeSpeed)} together {(1.0 - (qui - 60) * 0.002) * 0.01 * p.GetModified(eProperty.MeleeSpeed)}");
                }

                // apply speed cap
                if (speed < 15)
                {
                    speed = 15;
                }

                return (int) (speed * 100);
            }
            else
            {
                double speed = NpcWeaponSpeed() * 100 * (1.0 - (owner.GetModified(eProperty.Quickness) - 60) / 500.0);
                if (owner is GameSummonedPet pet)
                {
                    if (pet != null)
                    {
                        switch(pet.Name)
                        {
                            case "amber simulacrum": speed *= (owner.GetModified(eProperty.MeleeSpeed) * 0.01) * 1.45; break;
                            case "emerald simulacrum": speed *= (owner.GetModified(eProperty.MeleeSpeed) * 0.01) * 1.45; break;
                            case "ruby simulacrum": speed *= (owner.GetModified(eProperty.MeleeSpeed) * 0.01) * 0.95; break;
                            case "sapphire simulacrum": speed *= (owner.GetModified(eProperty.MeleeSpeed) * 0.01) * 0.95; break;
                            case "jade simulacrum": speed *= (owner.GetModified(eProperty.MeleeSpeed) * 0.01) * 0.95; break;
                            default: speed *= owner.GetModified(eProperty.MeleeSpeed) * 0.01; break;
                        }
                        //return (int)speed;
                    }
                }
                else
                {
                    if (owner.ActiveWeaponSlot == eActiveWeaponSlot.Distance)
                    {
                        // Old archery uses archery speed, but new archery uses casting speed
                        if (Properties.ALLOW_OLD_ARCHERY)
                            speed *= 1.0 - owner.GetModified(eProperty.ArcherySpeed) * 0.01;
                        else
                            speed *= 1.0 - owner.GetModified(eProperty.CastingSpeed) * 0.01;
                    }
                    else
                    {
                        speed *= owner.GetModified(eProperty.MeleeSpeed) * 0.01;
                    }
                }

                return (int) Math.Max(500.0, speed);
            }
        }

        /// <summary>
        /// Gets the speed of a NPC's weapon, based on its ActiveWeaponSlot.
        /// InventoryItem.SPD_ABS isn't set for NPCs, so this method must be used instead.
        /// </summary>
        public int NpcWeaponSpeed()
        {
            switch (owner.ActiveWeaponSlot)
            {
                default:
                case eActiveWeaponSlot.Standard:
                    return 30;
                case eActiveWeaponSlot.TwoHanded:
                    return 40;
                case eActiveWeaponSlot.Distance:
                    return 45;
            }
        }

        public virtual double AttackDamage(DbInventoryItem weapon, out double damageCap)
        {
            double effectiveness = 1;
            damageCap = 0;

            if (owner is GamePlayer player)
            {
                if (weapon == null)
                    return 0;

                damageCap = player.WeaponDamageWithoutQualityAndCondition(weapon) * weapon.SPD_ABS * 0.1 * CalculateSlowWeaponDamageModifier(weapon);

                if (weapon.Item_Type == Slot.RANGED)
                {
                    damageCap *= CalculateTwoHandedDamageModifier(weapon);
                    DbInventoryItem ammo = player.rangeAttackComponent.Ammo;

                    if (ammo != null)
                    {
                        switch ((ammo.SPD_ABS) & 0x3)
                        {
                            case 0:
                                damageCap *= 0.85;
                                break; // Blunt (light) -15%.
                            case 1:
                                break; // Bodkin (medium) 0%.
                            case 2:
                                damageCap *= 1.15;
                                break; // Doesn't exist on live.
                            case 3:
                                damageCap *= 1.25;
                                break; // Broadhead (X-heavy) +25%.
                        }
                    }

                    if (weapon.Object_Type is ((int) eObjectType.Longbow) or ((int) eObjectType.RecurvedBow) or ((int) eObjectType.CompositeBow))
                    {
                        if (Properties.ALLOW_OLD_ARCHERY)
                            effectiveness += player.GetModified(eProperty.RangedDamage) * 0.01;
                        else
                        {
                            effectiveness += owner.GetModified(eProperty.RangedDamage) * 0.01;
                            effectiveness += owner.GetModified(eProperty.SpellDamage) * 0.01;
                        }
                    }
                    else
                        effectiveness += player.GetModified(eProperty.RangedDamage) * 0.01;
                }
                else if (weapon.Item_Type is Slot.RIGHTHAND or Slot.LEFTHAND or Slot.TWOHAND)
                {
                    effectiveness += player.GetModified(eProperty.MeleeDamage) * 0.01;

                    if (weapon.Item_Type == Slot.TWOHAND)
                        damageCap *= CalculateTwoHandedDamageModifier(weapon);
                    else if (player.Inventory?.GetItem(eInventorySlot.LeftHandWeapon) != null)
                        damageCap *= CalculateLeftAxeModifier();
                }

                damageCap *= effectiveness;
                double damage = player.ApplyWeaponQualityAndConditionToDamage(weapon, damageCap);
                damageCap *= 3;
                return damage *= effectiveness;
            }
            else
            {
                double damage = (1.0 + owner.Level / Properties.PVE_MOB_DAMAGE_F1 + owner.Level * owner.Level / Properties.PVE_MOB_DAMAGE_F2) * NpcWeaponSpeed() * 0.1;

                if (weapon == null ||
                    weapon.SlotPosition == Slot.RIGHTHAND ||
                    weapon.SlotPosition == Slot.LEFTHAND ||
                    weapon.SlotPosition == Slot.TWOHAND)
                {
                    effectiveness += owner.GetModified(eProperty.MeleeDamage) * 0.01;
                }
                else if (weapon.SlotPosition == Slot.RANGED)
                {
                    if (weapon.Object_Type is ((int) eObjectType.Longbow) or ((int) eObjectType.RecurvedBow) or ((int) eObjectType.CompositeBow))
                    {
                        if (Properties.ALLOW_OLD_ARCHERY)
                            effectiveness += owner.GetModified(eProperty.RangedDamage) * 0.01;
                        else
                        {
                            effectiveness += owner.GetModified(eProperty.RangedDamage) * 0.01;
                            effectiveness += owner.GetModified(eProperty.SpellDamage) * 0.01;
                        }
                    }
                    else
                        effectiveness += owner.GetModified(eProperty.RangedDamage) * 0.01;
                }

                damage *= effectiveness;

                if (owner is GameEpicBoss epicBoss)
                    damageCap = damage * epicBoss.Empathy / 100.0 * Properties.SET_EPIC_ENCOUNTER_WEAPON_DAMAGE_CAP;
                else
                    damageCap = damage * 3;

                return damage;
            }
        }

        //public double AttackDamage(InventoryItem weapon)
        //{
        //    return AttackDamage(weapon, out _);
        //}

        public void RequestStartAttack(GameObject attackTarget)
        {
            if (!StartAttackRequested)
            {
                m_startAttackTarget = attackTarget;
                StartAttackRequested = true;
                EntityManager.Add(this);
            }
        }

        private void StartAttack()
        {
            if (owner is GamePlayer player)
            {
                if (player.CharacterClass.StartAttack(m_startAttackTarget) == false)
                {
                    return;
                }

                if (!player.IsAlive)
                {
                    player.Out.SendMessage(
                        LanguageMgr.GetTranslation(player.Client.Account.Language, "GamePlayer.StartAttack.YouCantCombat"),
                        eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                    return;
                }

                // Necromancer with summoned pet cannot attack
                if (player.ControlledBrain?.Body is NecromancerPet)
                {
                    player.Out.SendMessage(
                        LanguageMgr.GetTranslation(player.Client.Account.Language,
                            "GamePlayer.StartAttack.CantInShadeMode"), eChatType.CT_YouHit,
                        eChatLoc.CL_SystemWindow);
                    return;
                }

                if (player.IsStunned)
                {
                    player.Out.SendMessage(
                        LanguageMgr.GetTranslation(player.Client.Account.Language,
                            "GamePlayer.StartAttack.CantAttackStunned"), eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                    return;
                }

                if (player.IsMezzed)
                {
                    player.Out.SendMessage(
                        LanguageMgr.GetTranslation(player.Client.Account.Language,
                            "GamePlayer.StartAttack.CantAttackmesmerized"), eChatType.CT_YouHit,
                        eChatLoc.CL_SystemWindow);
                    return;
                }

                long vanishTimeout = player.TempProperties.GetProperty<long>(VanishEffect.VANISH_BLOCK_ATTACK_TIME_KEY);
                if (vanishTimeout > 0 && vanishTimeout > GameLoop.GameLoopTime)
                {
                    player.Out.SendMessage(
                        LanguageMgr.GetTranslation(player.Client.Account.Language, "GamePlayer.StartAttack.YouMustWaitAgain",
                            (vanishTimeout - GameLoop.GameLoopTime + 1000) / 1000), eChatType.CT_YouHit,
                        eChatLoc.CL_SystemWindow);
                    return;
                }

                long VanishTick = player.TempProperties.GetProperty<long>(VanishEffect.VANISH_BLOCK_ATTACK_TIME_KEY);
                long changeTime = GameLoop.GameLoopTime - VanishTick;
                if (changeTime < 30000 && VanishTick > 0)
                {
                    player.Out.SendMessage(
                        LanguageMgr.GetTranslation(player.Client.Account.Language, "GamePlayer.StartAttack.YouMustWait",
                            ((30000 - changeTime) / 1000).ToString()), eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                    return;
                }

                if (player.IsOnHorse)
                    player.IsOnHorse = false;

                if (player.Steed != null && player.Steed is GameSiegeRam)
                {
                    player.Out.SendMessage("You can't enter combat mode while riding a siegeram!.", eChatType.CT_YouHit,eChatLoc.CL_SystemWindow);
                    return;
                }

                if (player.IsDisarmed)
                {
                    player.Out.SendMessage(
                        LanguageMgr.GetTranslation(player.Client.Account.Language, "GamePlayer.StartAttack.CantDisarmed"),
                        eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                    return;
                }

                if (player.IsSitting)
                {
                    player.Sit(false);
                }

                DbInventoryItem attackWeapon = owner.ActiveWeapon;

                if (attackWeapon == null)
                {
                    player.Out.SendMessage(
                        LanguageMgr.GetTranslation(player.Client.Account.Language,
                            "GamePlayer.StartAttack.CannotWithoutWeapon"), eChatType.CT_YouHit,
                        eChatLoc.CL_SystemWindow);
                    return;
                }

                if (attackWeapon.Object_Type == (int) eObjectType.Instrument)
                {
                    player.Out.SendMessage(
                        LanguageMgr.GetTranslation(player.Client.Account.Language, "GamePlayer.StartAttack.CannotMelee"),
                        eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                    return;
                }

                if (player.ActiveWeaponSlot == eActiveWeaponSlot.Distance)
                {
                    if (ServerProperties.Properties.ALLOW_OLD_ARCHERY == false)
                    {
                        if ((eCharacterClass) player.CharacterClass.ID == eCharacterClass.Scout ||
                            (eCharacterClass) player.CharacterClass.ID == eCharacterClass.Hunter ||
                            (eCharacterClass) player.CharacterClass.ID == eCharacterClass.Ranger)
                        {
                            // There is no feedback on live when attempting to fire a bow with arrows
                            return;
                        }
                    }

                    // Check arrows for ranged attack
                    if (player.rangeAttackComponent.UpdateAmmo(attackWeapon) == null)
                    {
                        player.Out.SendMessage(
                            LanguageMgr.GetTranslation(player.Client.Account.Language,
                                "GamePlayer.StartAttack.SelectQuiver"), eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                        return;
                    }

                    // Check if selected ammo is compatible for ranged attack
                    if (!player.rangeAttackComponent.IsAmmoCompatible)
                    {
                        player.Out.SendMessage(
                            LanguageMgr.GetTranslation(player.Client.Account.Language,
                                "GamePlayer.StartAttack.CantUseQuiver"), eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                        return;
                    }

                    if (EffectListService.GetAbilityEffectOnTarget(player, eEffect.SureShot) != null)
                        player.rangeAttackComponent.RangedAttackType = eRangedAttackType.SureShot;
                    if (EffectListService.GetAbilityEffectOnTarget(player, eEffect.RapidFire) != null)
                        player.rangeAttackComponent.RangedAttackType = eRangedAttackType.RapidFire;
                    if (EffectListService.GetAbilityEffectOnTarget(player, eEffect.TrueShot) != null)
                        player.rangeAttackComponent.RangedAttackType = eRangedAttackType.Long;


                    if (player.rangeAttackComponent?.RangedAttackType == eRangedAttackType.Critical &&
                        player.Endurance < RangeAttackComponent.CRITICAL_SHOT_ENDURANCE_COST)
                    {
                        player.Out.SendMessage(
                            LanguageMgr.GetTranslation(player.Client.Account.Language, "GamePlayer.StartAttack.TiredShot"),
                            eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                        return;
                    }

                    if (player.Endurance < RangeAttackComponent.DEFAULT_ENDURANCE_COST)
                    {
                        player.Out.SendMessage(
                            LanguageMgr.GetTranslation(player.Client.Account.Language, "GamePlayer.StartAttack.TiredUse",
                                attackWeapon.Name), eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                        return;
                    }

                    if (player.IsStealthed)
                    {
                        // -Chance to unstealth while nocking an arrow = stealth spec / level
                        // -Chance to unstealth nocking a crit = stealth / level  0.20
                        int stealthSpec = player.GetModifiedSpecLevel(Specs.Stealth);
                        int stayStealthed = stealthSpec * 100 / player.Level;
                        if (player.rangeAttackComponent?.RangedAttackType == eRangedAttackType.Critical)
                            stayStealthed -= 20;

                        if (!Util.Chance(stayStealthed))
                            player.Stealth(false);
                    }
                }
                else
                {
                    if (m_startAttackTarget == null)
                        player.Out.SendMessage(
                            LanguageMgr.GetTranslation(player.Client.Account.Language,
                                "GamePlayer.StartAttack.CombatNoTarget"), eChatType.CT_YouHit,
                            eChatLoc.CL_SystemWindow);
                    else if (m_startAttackTarget is GameNPC)
                    {
                        player.Out.SendMessage(LanguageMgr.GetTranslation(player.Client.Account.Language,
                                "GamePlayer.StartAttack.CombatTarget",
                                m_startAttackTarget.GetName(0, false, player.Client.Account.Language, (m_startAttackTarget as GameNPC))),
                            eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                    }
                    else
                    {
                        player.Out.SendMessage(
                            LanguageMgr.GetTranslation(player.Client.Account.Language, "GamePlayer.StartAttack.CombatTarget",
                                m_startAttackTarget.GetName(0, false)), eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                    }
                }

                /*
                if (p.CharacterClass is PlayerClass.ClassVampiir)
                {
                    GameSpellEffect removeEffect = SpellHandler.FindEffectOnTarget(p, "VampiirSpeedEnhancement");
                    if (removeEffect != null)
                        removeEffect.Cancel(false);
                }
                else
                {
                    // Bard RR5 ability must drop when the player starts a melee attack
                    IGameEffect DreamweaverRR5 = p.EffectList.GetOfType<DreamweaverEffect>();
                    if (DreamweaverRR5 != null)
                        DreamweaverRR5.Cancel(false);
                }*/
                if (LivingStartAttack())
                {
                    if (player.castingComponent.SpellHandler?.Spell.Uninterruptible == false)
                    {
                        player.StopCurrentSpellcast();
                        player.Out.SendMessage(LanguageMgr.GetTranslation(player.Client.Account.Language, "GamePlayer.StartAttack.SpellCancelled"), eChatType.CT_SpellResisted, eChatLoc.CL_SystemWindow);
                    }

                    if (player.ActiveWeaponSlot != eActiveWeaponSlot.Distance)
                        player.Out.SendAttackMode(AttackState);
                    else
                    {
                        player.TempProperties.SetProperty(RangeAttackComponent.RANGED_ATTACK_START, GameLoop.GameLoopTime);

                        string typeMsg = "shot";
                        if (attackWeapon.Object_Type == (int) eObjectType.Thrown)
                            typeMsg = "throw";

                        string targetMsg = "";
                        if (m_startAttackTarget != null)
                        {
                            if (player.IsWithinRadius(m_startAttackTarget, AttackRange))
                                targetMsg = LanguageMgr.GetTranslation(player.Client.Account.Language,
                                    "GamePlayer.StartAttack.TargetInRange");
                            else
                                targetMsg = LanguageMgr.GetTranslation(player.Client.Account.Language,
                                    "GamePlayer.StartAttack.TargetOutOfRange");
                        }

                        int speed = AttackSpeed(attackWeapon) / 100;
                        if (player.rangeAttackComponent.RangedAttackType == eRangedAttackType.RapidFire)
                            speed = Math.Max(15, speed / 2);

                        if (!player.effectListComponent.ContainsEffectForEffectType(eEffect.Volley))//volley check
                            player.Out.SendMessage(
                            LanguageMgr.GetTranslation(player.Client.Account.Language, "GamePlayer.StartAttack.YouPrepare",
                                typeMsg, speed / 10, speed % 10, targetMsg), eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                    }
                }
            }
            else if (owner is GameNPC && m_startAttackTarget != null)
                NpcStartAttack(m_startAttackTarget);
            else
                LivingStartAttack();
        }

        private bool LivingStartAttack()
        {
            if (owner.IsIncapacitated)
                return false;

            if (owner.IsEngaging)
                owner.CancelEngageEffect();

            AttackState = true;
            DbInventoryItem attackWeapon = owner.ActiveWeapon;

            int speed = AttackSpeed(attackWeapon);

            if (speed <= 0)
                return false;

            // NPCs aren't allowed to prepare their ranged attack while moving or out of range.
            if (owner is GameNPC npcOwner && owner.ActiveWeaponSlot == eActiveWeaponSlot.Distance)
            {
                if (!npcOwner.IsWithinRadius(npcOwner.TargetObject, npcOwner.attackComponent.AttackRange))
                    return false;
                else if (npcOwner.IsMoving)
                    npcOwner.StopMoving();
            }

            attackAction = owner.CreateAttackAction();

            if (owner.ActiveWeaponSlot == eActiveWeaponSlot.Distance)
            {
                // Only start another attack action if we aren't already aiming to shoot.
                if (owner.rangeAttackComponent.RangedAttackState != eRangedAttackState.Aim)
                {
                    if (attackAction.CheckInterruptTimer())
                        return false;

                    owner.rangeAttackComponent.RangedAttackState = eRangedAttackState.Aim;

                    if (owner is not GamePlayer || !owner.effectListComponent.ContainsEffectForEffectType(eEffect.Volley))
                    {
                        // The 'stance' parameter appears to be used to tell whether or not the animation should be held, and doesn't seem to be related to the weapon speed.
                        foreach (GamePlayer player in owner.GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
                            player.Out.SendCombatAnimation(owner, null, (ushort)(attackWeapon != null ? attackWeapon.Model : 0), 0, player.Out.BowPrepare, 0x1A, 0x00, 0x00);
                    }

                    attackAction.StartTime = owner.rangeAttackComponent?.RangedAttackType == eRangedAttackType.RapidFire ? Math.Max(1500, speed / 2) : speed;
                }
            }

            return true;
        }

        private void NpcStartAttack(GameObject attackTarget)
        {
            GameNPC npc = owner as GameNPC;
            npc.TargetObject = attackTarget;
            npc.StopMovingOnPath();

            if (npc.Brain != null && npc.Brain is IControlledBrain)
            {
                if ((npc.Brain as IControlledBrain).AggressionState == eAggressionState.Passive)
                    return;
            }

            LivingStartAttack();

            if (AttackState)
            {
                // Archer mobs sometimes bug and keep trying to fire at max range unsuccessfully so force them to get just a tad closer.
                if (npc.ActiveWeaponSlot == eActiveWeaponSlot.Distance)
                    npc.Follow(attackTarget, AttackRange - 30, GameNPC.STICK_MAXIMUM_RANGE);
                else
                    npc.Follow(attackTarget, GameNPC.STICK_MINIMUM_RANGE, GameNPC.STICK_MAXIMUM_RANGE);
            }
        }

        public void StopAttack()
        {
            if (owner.ActiveWeaponSlot == eActiveWeaponSlot.Distance)
            {
                // Only cancel the animation if the ranged ammo isn't released already.
                if (AttackState && weaponAction?.AttackFinished != true)
                {
                    foreach (GamePlayer player in owner.GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
                        player.Out.SendInterruptAnimation(owner);
                }

                owner.rangeAttackComponent.RangedAttackState = eRangedAttackState.None;
                owner.rangeAttackComponent.RangedAttackType = eRangedAttackType.Normal;
            }

            AttackState = false;
            owner.CancelEngageEffect();
            owner.styleComponent.NextCombatStyle = null;
            owner.styleComponent.NextCombatBackupStyle = null;

            if (owner is GamePlayer playerOwner && playerOwner.IsAlive)
                playerOwner.Out.SendAttackMode(AttackState);
            else if (owner is MimicNPC mimic && !mimic.MimicBrain.HasAggro)
            {
                if (mimic.CharacterClass.ID == (int)eCharacterClass.Hunter ||
                    mimic.CharacterClass.ID == (int)eCharacterClass.Ranger ||
                    mimic.CharacterClass.ID == (int)eCharacterClass.Scout)
                {
                    mimic.SwitchWeapon(eActiveWeaponSlot.Distance);
                }
            }
            else if (owner is not MimicNPC && owner is GameNPC npcOwner && npcOwner.Inventory?.GetItem(eInventorySlot.DistanceWeapon) != null && npcOwner.ActiveWeaponSlot != eActiveWeaponSlot.Distance)
                npcOwner.SwitchWeapon(eActiveWeaponSlot.Distance);
        }

        /// <summary>
        /// Called whenever a single attack strike is made
        /// </summary>
        public virtual AttackData MakeAttack(WeaponAction action, GameObject target, DbInventoryItem weapon, Style style, double effectiveness, int interruptDuration, bool dualWield)
        {
            if (owner is GamePlayer playerOwner)
            {
                if (playerOwner.IsCrafting)
                {
                    playerOwner.Out.SendMessage(LanguageMgr.GetTranslation(playerOwner.Client.Account.Language, "GamePlayer.Attack.InterruptedCrafting"), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    playerOwner.craftComponent.StopCraft();
                    playerOwner.CraftTimer = null;
                    playerOwner.Out.SendCloseTimerWindow();
                }

                if (playerOwner.IsSalvagingOrRepairing)
                {
                    playerOwner.Out.SendMessage(LanguageMgr.GetTranslation(playerOwner.Client.Account.Language, "GamePlayer.Attack.InterruptedCrafting"), eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    playerOwner.CraftTimer.Stop();
                    playerOwner.CraftTimer = null;
                    playerOwner.Out.SendCloseTimerWindow();
                }

                AttackData ad = LivingMakeAttack(action, target, weapon, style, effectiveness * playerOwner.Effectiveness, interruptDuration, dualWield);

                switch (ad.AttackResult)
                {
                    case eAttackResult.HitStyle:
                    case eAttackResult.HitUnstyled:
                    {
                        // Keep component.
                        if ((ad.Target is GameKeepComponent || ad.Target is GameKeepDoor || ad.Target is GameSiegeWeapon) &&
                            ad.Attacker is GamePlayer && ad.Attacker.GetModified(eProperty.KeepDamage) > 0)
                        {
                            int keepdamage = (int) Math.Floor(ad.Damage * ((double) ad.Attacker.GetModified(eProperty.KeepDamage) / 100));
                            int keepstyle = (int) Math.Floor(ad.StyleDamage * ((double) ad.Attacker.GetModified(eProperty.KeepDamage) / 100));
                            ad.Damage += keepdamage;
                            ad.StyleDamage += keepstyle;
                        }

                        // Vampiir.
                        if (playerOwner.CharacterClass is PlayerClass.ClassVampiir &&
                            target is not GameKeepComponent and not GameKeepDoor and not GameSiegeWeapon)
                        {
                            int perc = Convert.ToInt32((double) (ad.Damage + ad.CriticalDamage) / 100 * (55 - playerOwner.Level));
                            perc = (perc < 1) ? 1 : ((perc > 15) ? 15 : perc);
                            playerOwner.Mana += Convert.ToInt32(Math.Ceiling((decimal) (perc * playerOwner.MaxMana) / 100));
                        }

                        break;
                    }
                }

                switch (ad.AttackResult)
                {
                    case eAttackResult.Blocked:
                    case eAttackResult.Fumbled:
                    case eAttackResult.HitStyle:
                    case eAttackResult.HitUnstyled:
                    case eAttackResult.Missed:
                    case eAttackResult.Parried:
                    {
                        // Condition percent can reach 70%.
                        // Durability percent can reach 0%.

                        if (weapon is GameInventoryItem weaponItem)
                            weaponItem.OnStrikeTarget(playerOwner, target);

                        // Camouflage will be disabled only when attacking a GamePlayer or ControlledNPC of a GamePlayer.
                        if ((target is GamePlayer && playerOwner.HasAbility(Abilities.Camouflage)) ||
                            (target is GameNPC targetNpc && targetNpc.Brain is IControlledBrain targetNpcBrain && targetNpcBrain.GetPlayerOwner() != null))
                        {
                            CamouflageECSGameEffect camouflage = (CamouflageECSGameEffect) EffectListService.GetAbilityEffectOnTarget(playerOwner, eEffect.Camouflage);

                            if (camouflage != null)
                                EffectService.RequestImmediateCancelEffect(camouflage, false);

                            playerOwner.DisableSkill(SkillBase.GetAbility(Abilities.Camouflage), CamouflageSpecHandler.DISABLE_DURATION);
                        }

                        // Multiple Hit check.
                        if (ad.AttackResult == eAttackResult.HitStyle)
                        {
                            List<GameObject> extraTargets = new();
                            List<GameObject> listAvailableTargets = new();
                            DbInventoryItem attackWeapon = owner.ActiveWeapon;
                            DbInventoryItem leftWeapon = playerOwner.Inventory?.GetItem(eInventorySlot.LeftHandWeapon);

                            int numTargetsCanHit = style.ID switch
                            {
                                374 => 1, // Tribal Assault: Hits 2 targets.
                                377 => 1, // Clan's Might: Hits 2 targets.
                                379 => 2, // Totemic Wrath: Hits 3 targets.
                                384 => 3, // Totemic Sacrifice: Hits 4 targets.
                                600 => 255, // Shield Swipe: No cap.
                                _ => 0
                            };

                            if (numTargetsCanHit <= 0)
                                break;

                            bool IsNotShieldSwipe = style.ID != 600;

                            if (IsNotShieldSwipe)
                            {
                                foreach (GamePlayer playerInRange in owner.GetPlayersInRadius((ushort) AttackRange))
                                {
                                    if (GameServer.ServerRules.IsAllowedToAttack(owner, playerInRange, true))
                                        listAvailableTargets.Add(playerInRange);
                                }
                            }

                            foreach (GameNPC npcInRange in owner.GetNPCsInRadius((ushort) AttackRange))
                            {
                                if (GameServer.ServerRules.IsAllowedToAttack(owner, npcInRange, true))
                                    listAvailableTargets.Add(npcInRange);
                            }

                            // Remove primary target.
                            listAvailableTargets.Remove(target);

                            if (numTargetsCanHit >= listAvailableTargets.Count)
                                extraTargets = listAvailableTargets;
                            else
                            {
                                int index;
                                GameObject availableTarget;

                                for (int i = numTargetsCanHit; i > 0; i--)
                                {
                                    index = Util.Random(listAvailableTargets.Count - 1);
                                    availableTarget = listAvailableTargets[index];
                                    listAvailableTargets.RemoveAt(index);
                                    extraTargets.Add(availableTarget);
                                }
                            }

                            foreach (GameObject extraTarget in extraTargets)
                            {
                                if (extraTarget is GamePlayer player && player.IsSitting)
                                    effectiveness *= 2;

                                // TODO: Figure out why Shield Swipe is handled differently here.
                                if (IsNotShieldSwipe)
                                {
                                    weaponAction = new MimicWeaponAction(playerOwner, extraTarget, attackWeapon, leftWeapon, effectiveness, AttackSpeed(attackWeapon), null);
                                    weaponAction.Execute();
                                }
                                else
                                    LivingMakeAttack(action, extraTarget, attackWeapon, null, 1, Properties.SPELL_INTERRUPT_DURATION, false);
                            }
                        }

                        break;
                    }
                }

                return ad;
            }
            else
            {
                if (owner is NecromancerPet necromancerPet)
                    ((NecromancerPetBrain)necromancerPet.Brain).CheckAttackSpellQueue();
                else
                    effectiveness = 1;

                return LivingMakeAttack(action, target, weapon, style, effectiveness, interruptDuration, dualWield);
            }
        }

        /// <summary>
        /// This method is called to make an attack, it is called from the
        /// attacktimer and should not be called manually
        /// </summary>
        /// <returns>the object where we collect and modifiy all parameters about the attack</returns>
        public virtual AttackData LivingMakeAttack(WeaponAction action, GameObject target, DbInventoryItem weapon, Style style, double effectiveness, int interruptDuration, bool dualWield, bool ignoreLOS = false)
        {
            AttackData ad = new()
            {
                Attacker = owner,
                Target = target as GameLiving,
                Damage = 0,
                CriticalDamage = 0,
                Style = style,
                WeaponSpeed = AttackSpeed(weapon) / 100,
                DamageType = AttackDamageType(weapon),
                ArmorHitLocation = eArmorSlot.NOTSET,
                Weapon = weapon,
                IsOffHand = weapon != null && weapon.SlotPosition == Slot.LEFTHAND
            };

            // Asp style range add.
            IEnumerable<(Spell, int, int)> rangeProc = style?.Procs.Where(x => x.Item1.SpellType == eSpellType.StyleRange);
            int addRange = rangeProc?.Any() == true ? (int) (rangeProc.First().Item1.Value - AttackRange) : 0;

            if (dualWield && (ad.Attacker is GamePlayer gPlayer) && gPlayer.CharacterClass.ID != (int) eCharacterClass.Savage)
                ad.AttackType = AttackData.eAttackType.MeleeDualWield;
            else if (weapon == null)
                ad.AttackType = AttackData.eAttackType.MeleeOneHand;
            else
            {
                ad.AttackType = weapon.SlotPosition switch
                {
                    Slot.TWOHAND => AttackData.eAttackType.MeleeTwoHand,
                    Slot.RANGED => AttackData.eAttackType.Ranged,
                    _ => AttackData.eAttackType.MeleeOneHand,
                };
            }

            // No target.
            if (ad.Target == null)
            {
                ad.AttackResult = (target == null) ? eAttackResult.NoTarget : eAttackResult.NoValidTarget;
                SendAttackingCombatMessages(action, ad);
                return ad;
            }

            // Region / state check.
            if (ad.Target.CurrentRegionID != owner.CurrentRegionID || ad.Target.ObjectState != eObjectState.Active)
            {
                ad.AttackResult = eAttackResult.NoValidTarget;
                SendAttackingCombatMessages(action, ad);
                return ad;
            }

            // LoS / in front check.
            if (!ignoreLOS && ad.AttackType != AttackData.eAttackType.Ranged && owner is GamePlayer &&
                !(ad.Target is GameKeepComponent) &&
                !(owner.IsObjectInFront(ad.Target, 120) && owner.TargetInView))
            {
                ad.AttackResult = eAttackResult.TargetNotVisible;
                SendAttackingCombatMessages(action, ad);
                return ad;
            }

            // Target is already dead.
            if (!ad.Target.IsAlive)
            {
                ad.AttackResult = eAttackResult.TargetDead;
                SendAttackingCombatMessages(action, ad);
                return ad;
            }

            // Melee range check (ranged is already done at this point).
            if (ad.AttackType != AttackData.eAttackType.Ranged)
            {
                if (!owner.IsWithinRadius(ad.Target, AttackRange + addRange))
                {
                    ad.AttackResult = eAttackResult.OutOfRange;
                    SendAttackingCombatMessages(action, ad);
                    return ad;
                }
            }

            if (!GameServer.ServerRules.IsAllowedToAttack(ad.Attacker, ad.Target, attackAction != null && GameLoop.GameLoopTime - attackAction.RoundWithNoAttackTime <= 1500))
            {
                ad.AttackResult = eAttackResult.NotAllowed_ServerRules;
                SendAttackingCombatMessages(action, ad);
                return ad;
            }

            if (ad.Target.IsSitting)
                effectiveness *= 2;

            // Apply Mentalist RA5L.
            SelectiveBlindnessEffect SelectiveBlindness = owner.EffectList.GetOfType<SelectiveBlindnessEffect>();
            if (SelectiveBlindness != null)
            {
                GameLiving EffectOwner = SelectiveBlindness.EffectSource;
                if (EffectOwner == ad.Target)
                {
                    if (owner is GamePlayer)
                        ((GamePlayer) owner).Out.SendMessage(
                            string.Format(
                                LanguageMgr.GetTranslation(((GamePlayer) owner).Client.Account.Language,
                                    "GameLiving.AttackData.InvisibleToYou"), ad.Target.GetName(0, true)),
                            eChatType.CT_Missed, eChatLoc.CL_SystemWindow);
                    ad.AttackResult = eAttackResult.NoValidTarget;
                    SendAttackingCombatMessages(action, ad);
                    return ad;
                }
            }

            // DamageImmunity Ability.
            if ((GameLiving) target != null && ((GameLiving) target).HasAbility(Abilities.DamageImmunity))
            {
                //if (ad.Attacker is GamePlayer) ((GamePlayer)ad.Attacker).Out.SendMessage(string.Format("{0} can't be attacked!", ad.Target.GetName(0, true)), eChatType.CT_Missed, eChatLoc.CL_SystemWindow);
                ad.AttackResult = eAttackResult.NoValidTarget;
                SendAttackingCombatMessages(action, ad);
                return ad;
            }

            // Calculate our attack result and attack damage.
            ad.AttackResult = ad.Target.attackComponent.CalculateEnemyAttackResult(action, ad, weapon);

            GamePlayer playerOwner = owner as GamePlayer;

            // Strafing miss.
            if (playerOwner != null && playerOwner.IsStrafing && ad.Target is GamePlayer && Util.Chance(30))
            {
                // Used to tell the difference between a normal miss and a strafing miss.
                // Ugly, but we shouldn't add a new field to 'AttackData' just for that purpose.
                ad.MissRate = 0;
                ad.AttackResult = eAttackResult.Missed;
            }

            switch (ad.AttackResult)
            {
                // Calculate damage only if we hit the target.
                case eAttackResult.HitUnstyled:
                case eAttackResult.HitStyle:
                {
                    double damage = AttackDamage(weapon, out double damageCap) * effectiveness;
                    DbInventoryItem armor = null;

                    if (ad.Target.Inventory != null)
                        armor = ad.Target.Inventory.GetItem((eInventorySlot) ad.ArmorHitLocation);

                    DbInventoryItem weaponForSpecModifier = null;

                    if (weapon != null)
                    {
                        weaponForSpecModifier = new DbInventoryItem();
                        weaponForSpecModifier.Object_Type = weapon.Object_Type;
                        weaponForSpecModifier.SlotPosition = weapon.SlotPosition;

                        if (owner is GamePlayer && owner.Realm == eRealm.Albion && Properties.ENABLE_ALBION_ADVANCED_WEAPON_SPEC &&
                            (GameServer.ServerRules.IsObjectTypesEqual((eObjectType) weapon.Object_Type, eObjectType.TwoHandedWeapon) ||
                            GameServer.ServerRules.IsObjectTypesEqual((eObjectType) weapon.Object_Type, eObjectType.PolearmWeapon)))
                        {
                            // Albion dual spec penalty, which sets minimum damage to the base damage spec.
                            if (weapon.Type_Damage == (int) eDamageType.Crush)
                                weaponForSpecModifier.Object_Type = (int) eObjectType.CrushingWeapon;
                            else if (weapon.Type_Damage == (int) eDamageType.Slash)
                                weaponForSpecModifier.Object_Type = (int) eObjectType.SlashingWeapon;
                            else
                                weaponForSpecModifier.Object_Type = (int) eObjectType.ThrustWeapon;
                        }
                    }

                    double specModifier = CalculateSpecModifier(ad.Target, weaponForSpecModifier);
                    double weaponSkill = CalculateWeaponSkill(ad.Target, weapon, specModifier, out double baseWeaponSkill);
                    double armorMod = CalculateTargetArmor(ad.Target, ad.ArmorHitLocation, out double bonusArmorFactor, out double armorFactor, out double absorb);
                    double damageMod = weaponSkill / armorMod;

                    if (action.RangedAttackType == eRangedAttackType.Critical)
                        damageCap *= 2; // This may be incorrect. Critical shot doesn't double damage on >yellow targets.

                    if (playerOwner != null)
                    {
                        if (playerOwner.UseDetailedCombatLog)
                            PrintDetailedCombatLog(playerOwner, armorFactor, absorb, armorMod, baseWeaponSkill, specModifier, weaponSkill, damageMod, damageCap);

                        // Badge Of Valor Calculation 1+ absorb or 1- absorb
                        // if (ad.Attacker.EffectList.GetOfType<BadgeOfValorEffect>() != null)
                        //     damage *= 1.0 + Math.Min(0.85, ad.Target.GetArmorAbsorb(ad.ArmorHitLocation));
                        // else
                        //     damage *= 1.0 - Math.Min(0.85, ad.Target.GetArmorAbsorb(ad.ArmorHitLocation));
                    }
                    else
                    {
                        if (owner is GameEpicBoss boss)
                            damageMod += boss.Strength / 200;

                        // Badge Of Valor Calculation 1+ absorb or 1- absorb
                        // if (ad.Attacker.EffectList.GetOfType<BadgeOfValorEffect>() != null)
                        //     damage *= 1.0 + Math.Min(0.85, ad.Target.GetArmorAbsorb(ad.ArmorHitLocation));
                        // else
                        //     damage *= 1.0 - Math.Min(0.85, ad.Target.GetArmorAbsorb(ad.ArmorHitLocation));
                    }

                    if (target is GamePlayer targetPlayer && targetPlayer.UseDetailedCombatLog)
                        PrintDetailedCombatLog(targetPlayer, armorFactor, absorb, armorMod, baseWeaponSkill, specModifier, weaponSkill, damageMod, damageCap);

                    if (ad.IsOffHand)
                        damage *= 1 + owner.GetModified(eProperty.OffhandDamage) * 0.01;

                    // If the target is another player's pet, shouldn't 'PVP_MELEE_DAMAGE' be used?
                    if (owner is GamePlayer || (owner is GameNPC npcOwner && npcOwner.Brain is IControlledBrain && owner.Realm != 0))
                    {
                        if (target is GamePlayer)
                            damage = (int) (damage * Properties.PVP_MELEE_DAMAGE);
                        else if (target is GameNPC)
                            damage = (int) (damage * Properties.PVE_MELEE_DAMAGE);
                    }

                    damage *= damageMod;
                    double preResistDamage = damage;
                    double primarySecondaryResistMod = CalculateTargetResistance(ad.Target, ad.DamageType, armor);
                    double preConversionDamage = preResistDamage * primarySecondaryResistMod;
                    double conversionMod = CalculateTargetConversion(ad.Target, damage * primarySecondaryResistMod);

                    if (StyleProcessor.ExecuteStyle(owner, ad.Target, ad.Style, weapon, preResistDamage, damageCap, ad.ArmorHitLocation, ad.StyleEffects, out int styleDamage, out int animationId))
                    {
                        double preResistStyleDamage = styleDamage;
                        double preConversionStyleDamage = preResistStyleDamage * primarySecondaryResistMod;
                        ad.StyleDamage = (int) (preConversionStyleDamage * conversionMod);

                        preResistDamage += preResistStyleDamage;
                        preConversionDamage += preConversionStyleDamage;
                        damage += ad.StyleDamage;

                        ad.AnimationId = animationId;
                        ad.AttackResult = eAttackResult.HitStyle;
                    }

                    damage = preConversionDamage * conversionMod;
                    ad.Modifier = (int) (damage - preResistDamage);
                    damage = (int) Math.Min(damage, damageCap);

                    if (conversionMod < 1)
                    {
                        double conversionAmount = conversionMod > 0 ? damage / conversionMod - damage : damage;
                        ApplyTargetConversionRegen(ad.Target, (int) conversionAmount);
                    }

                    ad.Damage = (int) Math.Min(damage, damageCap);
                    ad.CriticalDamage = CalculateMeleeCriticalDamage(ad, action, weapon);
                    break;
                }
                case eAttackResult.Blocked:
                case eAttackResult.Evaded:
                case eAttackResult.Parried:
                case eAttackResult.Missed:
                {
                    // Reduce endurance by half the style's cost if we missed.
                    if (ad.Style != null && playerOwner != null && weapon != null)
                        playerOwner.Endurance -= StyleProcessor.CalculateEnduranceCost(playerOwner, ad.Style, weapon.SPD_ABS) / 2;

                    break;
                }

                static void PrintDetailedCombatLog(GamePlayer player, double armorFactor, double absorb, double armorMod, double baseWeaponSkill, double specModifier, double weaponSkill, double damageMod, double damageCap)
                {
                    StringBuilder stringBuilder = new();
                    stringBuilder.Append($"BaseWS: {baseWeaponSkill:0.00} | SpecMod: {specModifier:0.00} | WS: {weaponSkill:0.00}\n");
                    stringBuilder.Append($"AF: {armorFactor:0.00} | ABS: {absorb * 100:0.00}% | AF/ABS: {armorMod:0.00}\n");
                    stringBuilder.Append($"DamageMod: {damageMod:0.00} | DamageCap: {damageCap:0.00}");
                    player.Out.SendMessage(stringBuilder.ToString(), eChatType.CT_DamageAdd, eChatLoc.CL_SystemWindow);
                }
            }

            // Attacked living may modify the attack data. Primarily used for keep doors and components.
            ad.Target.ModifyAttack(ad);

            string message = "";
            bool broadcast = true;

            ArrayList excludes = new()
            {
                ad.Attacker,
                ad.Target
            };

            switch (ad.AttackResult)
            {
                case eAttackResult.Parried:
                    message = string.Format("{0} attacks {1} and is parried!", ad.Attacker.GetName(0, true), ad.Target.GetName(0, false));
                    break;
                case eAttackResult.Evaded:
                    message = string.Format("{0} attacks {1} and is evaded!", ad.Attacker.GetName(0, true), ad.Target.GetName(0, false));
                    break;
                case eAttackResult.Fumbled:
                    message = string.Format("{0} fumbled!", ad.Attacker.GetName(0, true), ad.Target.GetName(0, false));
                    break;
                case eAttackResult.Missed:
                    message = string.Format("{0} attacks {1} and misses!", ad.Attacker.GetName(0, true), ad.Target.GetName(0, false));
                    break;
                case eAttackResult.Blocked:
                {
                    message = string.Format("{0} attacks {1} and is blocked!", ad.Attacker.GetName(0, true),
                        ad.Target.GetName(0, false));
                    // guard messages
                    if (target != null && target != ad.Target)
                    {
                        excludes.Add(target);

                        // another player blocked for real target
                        if (target is GamePlayer)
                            ((GamePlayer) target).Out.SendMessage(
                                string.Format(
                                    LanguageMgr.GetTranslation(((GamePlayer) target).Client.Account.Language,
                                        "GameLiving.AttackData.BlocksYou"), ad.Target.GetName(0, true),
                                    ad.Attacker.GetName(0, false)), eChatType.CT_Missed, eChatLoc.CL_SystemWindow);

                        // blocked for another player
                        if (ad.Target is GamePlayer)
                        {
                            ((GamePlayer) ad.Target).Out.SendMessage(
                                string.Format(
                                    LanguageMgr.GetTranslation(((GamePlayer) ad.Target).Client.Account.Language,
                                        "GameLiving.AttackData.YouBlock") +
                                        $" ({ad.BlockChance:0.0}%)", ad.Attacker.GetName(0, false),
                                    target.GetName(0, false)), eChatType.CT_Missed, eChatLoc.CL_SystemWindow);
                            ((GamePlayer) ad.Target).Stealth(false);
                        }
                    }
                    else if (ad.Target is GamePlayer)
                    {
                        ((GamePlayer) ad.Target).Out.SendMessage(
                            string.Format(
                                LanguageMgr.GetTranslation(((GamePlayer) ad.Target).Client.Account.Language,
                                    "GameLiving.AttackData.AttacksYou") +
                                    $" ({ad.BlockChance:0.0}%)", ad.Attacker.GetName(0, true)),
                            eChatType.CT_Missed, eChatLoc.CL_SystemWindow);
                    }

                    break;
                }
                case eAttackResult.HitUnstyled:
                case eAttackResult.HitStyle:
                {
                    if (ad.AttackResult == eAttackResult.HitStyle)
                    {
                        if (owner is GamePlayer)
                        {
                            GamePlayer player = owner as GamePlayer;

                            string damageAmount = (ad.StyleDamage > 0)
                                ? " (+" + ad.StyleDamage + ", GR: " + ad.Style.GrowthRate + ")"
                                : "";
                            player.Out.SendMessage(
                                LanguageMgr.GetTranslation(player.Client.Account.Language,
                                    "StyleProcessor.ExecuteStyle.PerformPerfectly", ad.Style.Name, damageAmount),
                                eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                        }
                        else if (owner is GameNPC)
                        {
                            ControlledNpcBrain brain = ((GameNPC) owner).Brain as ControlledNpcBrain;

                            if (brain != null)
                            {
                                GamePlayer player = brain.GetPlayerOwner();
                                if (player != null)
                                {
                                    string damageAmount = (ad.StyleDamage > 0)
                                        ? " (+" + ad.StyleDamage + ", GR: " + ad.Style.GrowthRate + ")"
                                        : "";
                                    player.Out.SendMessage(
                                        LanguageMgr.GetTranslation(player.Client.Account.Language,
                                            "StyleProcessor.ExecuteStyle.PerformsPerfectly", owner.Name, ad.Style.Name,
                                            damageAmount), eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                                }
                            }
                        }
                    }
                    
                    if (target != null && target != ad.Target)
                    {
                        message = string.Format("{0} attacks {1} but hits {2}!", ad.Attacker.GetName(0, true),
                            target.GetName(0, false), ad.Target.GetName(0, false));
                        excludes.Add(target);

                        // intercept for another player
                        if (target is GamePlayer)
                            ((GamePlayer) target).Out.SendMessage(
                                string.Format(
                                    LanguageMgr.GetTranslation(((GamePlayer) target).Client.Account.Language,
                                        "GameLiving.AttackData.StepsInFront"), ad.Target.GetName(0, true)),
                                eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);

                        // intercept by player
                        if (ad.Target is GamePlayer)
                            ((GamePlayer) ad.Target).Out.SendMessage(
                                string.Format(
                                    LanguageMgr.GetTranslation(((GamePlayer) ad.Target).Client.Account.Language,
                                        "GameLiving.AttackData.YouStepInFront"), target.GetName(0, false)),
                                eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                    }
                    else
                    {
                        if (ad.Attacker is GamePlayer)
                        {
                            string hitWeapon = "weapon";
                            if (weapon != null)
                                hitWeapon = GlobalConstants.NameToShortName(weapon.Name);
                            message = string.Format("{0} attacks {1} with {2} {3}!", ad.Attacker.GetName(0, true),
                                ad.Target.GetName(0, false), ad.Attacker.GetPronoun(1, false), hitWeapon);
                        }
                        else
                        {
                            message = string.Format("{0} attacks {1} and hits!", ad.Attacker.GetName(0, true),
                                ad.Target.GetName(0, false));
                        }
                    }

                    break;
                }
                default:
                    broadcast = false;
                    break;
            }

            SendAttackingCombatMessages(action, ad);

            #region Prevent Flight

            if (ad.Attacker is GamePlayer)
            {
                GamePlayer attacker = ad.Attacker as GamePlayer;
                if (attacker.HasAbilityType(typeof(AtlasOF_PreventFlight)) && Util.Chance(35))
                {
                    if (owner.IsObjectInFront(ad.Target, 120) && ad.Target.IsMoving)
                    {
                        bool preCheck = false;
                        float angle = ad.Target.GetAngle(ad.Attacker);
                        if (angle >= 150 && angle < 210) preCheck = true;

                        if (preCheck)
                        {
                            Spell spell = SkillBase.GetSpellByID(7083);
                            if (spell != null)
                            {
                                ISpellHandler spellHandler = ScriptMgr.CreateSpellHandler(owner, spell,
                                    SkillBase.GetSpellLine(GlobalSpellsLines.Reserved_Spells));
                                if (spellHandler != null)
                                {
                                    spellHandler.StartSpell(ad.Target);
                                }
                            }
                        }
                    }
                }
            }

            #endregion

            #region controlled messages

            if (ad.Attacker is GameNPC)
            {
                IControlledBrain brain = ((GameNPC) ad.Attacker).Brain as IControlledBrain;

                if (brain != null)
                {
                    GamePlayer owner = brain.GetPlayerOwner();

                    if (owner != null)
                    {
                        excludes.Add(owner);

                        switch (ad.AttackResult)
                        {
                            case eAttackResult.HitStyle:
                            case eAttackResult.HitUnstyled:
                            {
                                string modmessage = "";

                                if (ad.Modifier > 0)
                                    modmessage = $" (+{ad.Modifier})";
                                else if (ad.Modifier < 0)
                                    modmessage = $" ({ad.Modifier})";

                                string attackTypeMsg;

                                if (action.ActiveWeaponSlot == eActiveWeaponSlot.Distance)
                                    attackTypeMsg = "shoots";
                                else
                                    attackTypeMsg = "attacks";

                                owner.Out.SendMessage(string.Format(LanguageMgr.GetTranslation(owner.Client.Account.Language, "GameLiving.AttackData.YourHits"),
                                    ad.Attacker.Name, attackTypeMsg, ad.Target.GetName(0, false), ad.Damage, modmessage),
                                    eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);

                                if (ad.CriticalDamage > 0)
                                {
                                    owner.Out.SendMessage(string.Format(LanguageMgr.GetTranslation(owner.Client.Account.Language, "GameLiving.AttackData.YourCriticallyHits"),
                                        ad.Attacker.Name, ad.Target.GetName(0, false), ad.CriticalDamage) + $" ({AttackCriticalChance(action, ad.Weapon)}%)",
                                        eChatType.CT_YouHit,eChatLoc.CL_SystemWindow);
                                }

                                break;
                            }
                            case eAttackResult.Missed:
                            {
                                owner.Out.SendMessage(message + $" ({ad.MissRate}%)", eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                                break;
                            }
                            default:
                                owner.Out.SendMessage(message, eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                                break;
                        }
                    }
                }
            }

            if (ad.Target is GameNPC)
            {
                IControlledBrain brain = ((GameNPC) ad.Target).Brain as IControlledBrain;
                if (brain != null)
                {
                    GameLiving owner_living = brain.GetLivingOwner();
                    excludes.Add(owner_living);
                    if (owner_living != null && owner_living is GamePlayer && owner_living.ControlledBrain != null &&
                        ad.Target == owner_living.ControlledBrain.Body)
                    {
                        GamePlayer owner = owner_living as GamePlayer;
                        switch (ad.AttackResult)
                        {
                            case eAttackResult.Blocked:
                                owner.Out.SendMessage(
                                    string.Format(
                                        LanguageMgr.GetTranslation(owner.Client.Account.Language,
                                            "GameLiving.AttackData.Blocked"), ad.Attacker.GetName(0, true),
                                        ad.Target.Name), eChatType.CT_Missed, eChatLoc.CL_SystemWindow);
                                break;
                            case eAttackResult.Parried:
                                owner.Out.SendMessage(
                                    string.Format(
                                        LanguageMgr.GetTranslation(owner.Client.Account.Language,
                                            "GameLiving.AttackData.Parried"), ad.Attacker.GetName(0, true),
                                        ad.Target.Name), eChatType.CT_Missed, eChatLoc.CL_SystemWindow);
                                break;
                            case eAttackResult.Evaded:
                                owner.Out.SendMessage(
                                    string.Format(
                                        LanguageMgr.GetTranslation(owner.Client.Account.Language,
                                            "GameLiving.AttackData.Evaded"), ad.Attacker.GetName(0, true),
                                        ad.Target.Name), eChatType.CT_Missed, eChatLoc.CL_SystemWindow);
                                break;
                            case eAttackResult.Fumbled:
                                owner.Out.SendMessage(
                                    string.Format(
                                        LanguageMgr.GetTranslation(owner.Client.Account.Language,
                                            "GameLiving.AttackData.Fumbled"), ad.Attacker.GetName(0, true)),
                                    eChatType.CT_Missed, eChatLoc.CL_SystemWindow);
                                break;
                            case eAttackResult.Missed:
                                if (ad.AttackType != AttackData.eAttackType.Spell)
                                    owner.Out.SendMessage(
                                        string.Format(
                                            LanguageMgr.GetTranslation(owner.Client.Account.Language,
                                                "GameLiving.AttackData.Misses"), ad.Attacker.GetName(0, true),
                                            ad.Target.Name), eChatType.CT_Missed, eChatLoc.CL_SystemWindow);
                                break;
                            case eAttackResult.HitStyle:
                            case eAttackResult.HitUnstyled:
                            {
                                string modmessage = "";
                                if (ad.Modifier > 0) modmessage = " (+" + ad.Modifier + ")";
                                if (ad.Modifier < 0) modmessage = " (" + ad.Modifier + ")";
                                owner.Out.SendMessage(
                                    string.Format(
                                        LanguageMgr.GetTranslation(owner.Client.Account.Language,
                                            "GameLiving.AttackData.HitsForDamage"), ad.Attacker.GetName(0, true),
                                        ad.Target.Name, ad.Damage, modmessage), eChatType.CT_Damaged,
                                    eChatLoc.CL_SystemWindow);
                                if (ad.CriticalDamage > 0)
                                {
                                    owner.Out.SendMessage(
                                        string.Format(
                                            LanguageMgr.GetTranslation(owner.Client.Account.Language,
                                                "GameLiving.AttackData.CriticallyHitsForDamage"),
                                            ad.Attacker.GetName(0, true), ad.Target.Name, ad.CriticalDamage),
                                        eChatType.CT_Damaged, eChatLoc.CL_SystemWindow);
                                }

                                break;
                            }
                            default: break;
                        }
                    }
                }
            }

            #endregion

            // broadcast messages
            if (broadcast)
            {
                Message.SystemToArea(ad.Attacker, message, eChatType.CT_OthersCombat,
                    (GameObject[]) excludes.ToArray(typeof(GameObject)));
            }

            // Interrupt the target of the attack
            ad.Target.StartInterruptTimer(interruptDuration, ad.AttackType, ad.Attacker);

            // If we're attacking via melee, start an interrupt timer on ourselves so we cannot swing + immediately cast.
            if (ad.AttackType != AttackData.eAttackType.Spell && ad.AttackType != AttackData.eAttackType.Ranged && owner.StartInterruptTimerOnItselfOnMeleeAttack())
                owner.StartInterruptTimer(owner.SpellInterruptDuration, ad.AttackType, ad.Attacker);

            owner.OnAttackEnemy(ad);

            //Return the result
            return ad;
        }

        public double CalculateWeaponSkill(GameLiving target, DbInventoryItem weapon, double specModifier, out double baseWeaponSkill)
        {
            baseWeaponSkill = 1 + owner.GetWeaponSkill(weapon);
            return CalculateWeaponSkill(target, baseWeaponSkill, 1 + RelicMgr.GetRelicBonusModifier(owner.Realm, eRelicType.Strength), specModifier);
        }

        public virtual double CalculateWeaponSkill(GameLiving target, double baseWeaponSkill, double relicBonus, double specModifier)
        {
            if (owner is GamePlayer)
                return baseWeaponSkill * relicBonus * specModifier;

            baseWeaponSkill += target.Level * 65 / 50.0;

            if (owner.Level < 10)
                baseWeaponSkill *= 1 - 0.05 * (10 - owner.Level);

            return baseWeaponSkill;
        }

        public virtual double CalculateSpecModifier(GameLiving target, DbInventoryItem weapon)
        {
            double specModifier;

            if (owner is GamePlayer playerOwner)
            {
                int spec = owner.WeaponSpecLevel(weapon);

                if (owner.Level < 5 && spec < 2)
                    spec = 2;

                double lowerLimit = Math.Min(0.75 * (spec - 1) / (target.EffectiveLevel + 1) + 0.25, 1.0);

                if (lowerLimit < 0.01)
                    lowerLimit = 0.01;

                double upperLimit = Math.Min(Math.Max(1.25 + (3.0 * (spec - 1) / (target.EffectiveLevel + 1) - 2) * 0.25, 1.25), 1.50);
                int varianceRange = (int) (upperLimit * 100 - lowerLimit * 100);
                specModifier = playerOwner.SpecLock > 0 ? playerOwner.SpecLock : lowerLimit + Util.Random(varianceRange) * 0.01;
            }
            else
            {
                int minimum;
                int maximum;

                if (owner is GameEpicBoss)
                {
                    minimum = 95;
                    maximum = 105;
                }
                else
                {
                    minimum = 75;
                    maximum = 125;
                }

                specModifier = (Util.Random(maximum - minimum) + minimum) * 0.01;
            }

            return specModifier;
        }

        private const int ARMOR_FACTOR_LEVEL_SCALAR = 25;

        public double CalculateTargetArmor(GameLiving target, eArmorSlot armorSlot)
        {
            return CalculateTargetArmor(target, armorSlot, out _, out _, out _);
        }

        public virtual double CalculateTargetArmor(GameLiving target, eArmorSlot armorSlot, out double bonusArmorFactor, out double armorFactor, out double absorb)
        {
            bonusArmorFactor = owner is GamePlayer && target is not GamePlayer ? 2 : target.Level * ARMOR_FACTOR_LEVEL_SCALAR / 50.0;
            armorFactor = bonusArmorFactor + target.GetArmorAF(armorSlot);
            absorb = target.GetArmorAbsorb(armorSlot);
            return absorb >= 1 ? double.MaxValue : armorFactor / (1 - absorb);
        }

        public static double CalculateTargetResistance(GameLiving target, eDamageType damageType, DbInventoryItem armor)
        {
            double damageModifier = 1.0;
            damageModifier *= 1.0 - (target.GetResist(damageType) + SkillBase.GetArmorResist(armor, damageType)) * 0.01;
            return damageModifier;
        }

        public static double CalculateTargetConversion(GameLiving target, double damage)
        {
            if (target is not GamePlayer)
                return 1.0;

            double conversionMod = 1 - target.GetModified(eProperty.Conversion) / 100.0;

            if (conversionMod > 1.0)
                return 1.0;

            return conversionMod;
        }

        public static void ApplyTargetConversionRegen(GameLiving target, int conversionAmount)
        {
            if (target is not GamePlayer playerTarget)
                return;

            int powerConversion = conversionAmount;
            int enduranceConversion = conversionAmount;

            if (target.Mana + conversionAmount > target.MaxMana)
                powerConversion = target.MaxMana - target.Mana;

            if (target.Endurance + conversionAmount > target.MaxEndurance)
                enduranceConversion = target.MaxEndurance - target.Endurance;

            if (powerConversion > 0)
                playerTarget.Out.SendMessage(string.Format(LanguageMgr.GetTranslation(playerTarget.Client.Account.Language, "GameLiving.AttackData.GainPowerPoints"), powerConversion), eChatType.CT_Spell, eChatLoc.CL_SystemWindow);

            if (enduranceConversion > 0)
                playerTarget.Out.SendMessage(string.Format(LanguageMgr.GetTranslation(playerTarget.Client.Account.Language, "GameLiving.AttackData.GainEndurancePoints"), enduranceConversion), eChatType.CT_Spell, eChatLoc.CL_SystemWindow);

            target.Mana = Math.Min(target.MaxMana, target.Mana + powerConversion);
            target.Endurance = Math.Min(target.MaxEndurance, target.Endurance + enduranceConversion);
        }

        public virtual bool CheckBlock(AttackData ad, double attackerConLevel)
        {
            double blockChance = owner.TryBlock(ad, attackerConLevel, Attackers.Count);
            ad.BlockChance = blockChance;
            double blockRoll;

            if (!Properties.OVERRIDE_DECK_RNG && owner is GamePlayer player)
                blockRoll = player.RandomNumberDeck.GetPseudoDouble();
            else
                blockRoll = Util.CryptoNextDouble();

            if (blockChance > 0)
            {
                if (ad.Attacker is GamePlayer blockAttk && blockAttk.UseDetailedCombatLog)
                    blockAttk.Out.SendMessage($"target block%: {blockChance * 100:0.##} rand: {blockRoll * 100:0.##}", eChatType.CT_DamageAdd, eChatLoc.CL_SystemWindow);

                if (ad.Target is GamePlayer blockTarg && blockTarg.UseDetailedCombatLog)
                    blockTarg.Out.SendMessage($"your block%: {blockChance * 100:0.##} rand: {blockRoll * 100:0.##}", eChatType.CT_DamageAdd, eChatLoc.CL_SystemWindow);

                if (blockChance > blockRoll)
                    return true;
            }

            if (ad.AttackType is AttackData.eAttackType.Ranged or AttackData.eAttackType.Spell)
            {
                // Nature's shield, 100% block chance, 120° frontal angle.
                if (owner.IsObjectInFront(ad.Attacker, 120) && (owner.styleComponent.NextCombatStyle?.ID == 394 || owner.styleComponent.NextCombatBackupStyle?.ID == 394))
                {
                    ad.BlockChance = 1;
                    return true;
                }
            }

            return false;
        }

        public virtual bool CheckGuard(AttackData ad, bool stealthStyle, double attackerConLevel)
        {
            GuardECSGameEffect guard = EffectListService.GetAbilityEffectOnTarget(owner, eEffect.Guard) as GuardECSGameEffect;

            if (guard?.GuardTarget != owner)
                return false;

            GameLiving guardSource = guard.GuardSource;

            if (guardSource == null ||
                guardSource.ObjectState != eObjectState.Active ||
                guardSource.IsStunned != false ||
                guardSource.IsMezzed != false ||
                guardSource.ActiveWeaponSlot == eActiveWeaponSlot.Distance ||
                !guardSource.IsAlive ||
                guardSource.IsSitting ||
                stealthStyle ||
                !guard.GuardSource.IsWithinRadius(guard.GuardTarget, GuardAbilityHandler.GUARD_DISTANCE))
                return false;

            DbInventoryItem leftHand = guard.GuardSource.Inventory.GetItem(eInventorySlot.LeftHandWeapon);
            DbInventoryItem rightHand = guard.GuardSource.ActiveWeapon;

            if (((rightHand != null && rightHand.Hand == 1) || leftHand == null || leftHand.Object_Type != (int) eObjectType.Shield) && guard.GuardSource is not GameNPC)
                return false;

            // TODO: Insert actual formula for guarding here, this is just a guessed one based on block.
            int guardLevel = guard.GuardSource.GetAbilityLevel(Abilities.Guard);
            double guardChance;

            if (guard.GuardSource is GameNPC)
                guardChance = guard.GuardSource.GetModified(eProperty.BlockChance);
            else
                guardChance = guard.GuardSource.GetModified(eProperty.BlockChance) * (leftHand.Quality * 0.01) * (leftHand.Condition / (double) leftHand.MaxCondition);

            guardChance *= 0.001;
            guardChance += guardLevel * 5 * 0.01; // 5% additional chance to guard with each Guard level.
            guardChance += attackerConLevel * 0.05;
            int shieldSize = 1;

            if (leftHand != null)
            {
                shieldSize = Math.Max(leftHand.Type_Damage, 1);

                if (guardSource is GamePlayer)
                    guardChance += (double) (leftHand.Level - 1) / 50 * 0.15; // Up to 15% extra block chance based on shield level.
            }

            if (Attackers.Count > shieldSize)
                guardChance *= shieldSize / (double) Attackers.Count;

            // Reduce chance by attacker's defense penetration.
            guardChance *= 1 - ad.Attacker.GetAttackerDefensePenetration(ad.Attacker, ad.Weapon) / 100;

            if (guardChance < 0.01)
                guardChance = 0.01;
            else if (guardChance > Properties.BLOCK_CAP && ad.Attacker is GamePlayer && ad.Target is GamePlayer)
                guardChance = Properties.BLOCK_CAP;

            // Possibly intended to be applied in RvR only.
            if (shieldSize == 1 && guardChance > 0.8)
                guardChance = 0.8;
            else if (shieldSize == 2 && guardChance > 0.9)
                guardChance = 0.9;
            else if (shieldSize == 3 && guardChance > 0.99)
                guardChance = 0.99;

            if (ad.AttackType == AttackData.eAttackType.MeleeDualWield)
                guardChance *= 0.5;

            double guardRoll;

            if (!Properties.OVERRIDE_DECK_RNG && owner is GamePlayer player)
                guardRoll = player.RandomNumberDeck.GetPseudoDouble();
            else
                guardRoll = Util.CryptoNextDouble();

            bool success = guardChance > guardRoll;

            if (guard.GuardSource is GamePlayer blockAttk && blockAttk.UseDetailedCombatLog)
                blockAttk.Out.SendMessage($"Chance to guard: {guardChance * 100:0.##} rand: {guardRoll * 100:0.##} success? {success}", eChatType.CT_DamageAdd, eChatLoc.CL_SystemWindow);

            if (guard.GuardTarget is GamePlayer blockTarg && blockTarg.UseDetailedCombatLog)
                blockTarg.Out.SendMessage($"Chance to be guarded: {guardChance * 100:0.##} rand: {guardRoll * 100:0.##} success? {success}", eChatType.CT_DamageAdd, eChatLoc.CL_SystemWindow);

            if (success)
            {
                ad.Target = guard.GuardSource;
                return true;
            }

            return false;
        }

        public bool CheckDashingDefense(AttackData ad, bool stealthStyle, double attackerConLevel, out eAttackResult result)
        {
            // Not implemented.
            result = eAttackResult.Any;
            return false;
            DashingDefenseEffect dashing = null;

            if (dashing == null ||
                dashing.GuardSource.ObjectState != eObjectState.Active ||
                dashing.GuardSource.IsStunned != false ||
                dashing.GuardSource.IsMezzed != false ||
                dashing.GuardSource.ActiveWeaponSlot == eActiveWeaponSlot.Distance ||
                !dashing.GuardSource.IsAlive ||
                stealthStyle)
                return false;

            if (!dashing.GuardSource.IsWithinRadius(dashing.GuardTarget, DashingDefenseEffect.GUARD_DISTANCE))
                return false;

            DbInventoryItem leftHand = dashing.GuardSource.Inventory.GetItem(eInventorySlot.LeftHandWeapon);
            DbInventoryItem rightHand = dashing.GuardSource.ActiveWeapon;

            if ((rightHand == null || rightHand.Hand != 1) && leftHand != null && leftHand.Object_Type == (int) eObjectType.Shield)
            {
                int guardLevel = dashing.GuardSource.GetAbilityLevel(Abilities.Guard);
                double guardchance = dashing.GuardSource.GetModified(eProperty.BlockChance) * leftHand.Quality * 0.00001;
                guardchance *= guardLevel * 0.25 + 0.05;
                guardchance += attackerConLevel * 0.05;

                if (guardchance > 0.99)
                    guardchance = 0.99;
                else if (guardchance < 0.01)
                    guardchance = 0.01;

                int shieldSize = 0;

                if (leftHand != null)
                    shieldSize = leftHand.Type_Damage;

                if (Attackers.Count > shieldSize)
                    guardchance *= shieldSize / (double) Attackers.Count;

                if (ad.AttackType == AttackData.eAttackType.MeleeDualWield)
                    guardchance /= 2;

                double parrychance = dashing.GuardSource.GetModified(eProperty.ParryChance);

                if (parrychance != double.MinValue)
                {
                    parrychance *= 0.001;
                    parrychance += 0.05 * attackerConLevel;

                    if (parrychance > 0.99)
                        parrychance = 0.99;
                    else if (parrychance < 0.01)
                        parrychance = 0.01;

                    if (Attackers.Count > 1)
                        parrychance /= Attackers.Count / 2;
                }

                if (Util.ChanceDouble(guardchance))
                {
                    ad.Target = dashing.GuardSource;
                    result = eAttackResult.Blocked;
                    return true;
                }
                else if (Util.ChanceDouble(parrychance))
                {
                    ad.Target = dashing.GuardSource;
                    result = eAttackResult.Parried;
                    return true;
                }
            }
            else
            {
                double parrychance = dashing.GuardSource.GetModified(eProperty.ParryChance);

                if (parrychance != double.MinValue)
                {
                    parrychance *= 0.001;
                    parrychance += 0.05 * attackerConLevel;

                    if (parrychance > 0.99)
                        parrychance = 0.99;
                    else if (parrychance < 0.01)
                        parrychance = 0.01;

                    if (Attackers.Count > 1)
                        parrychance /= Attackers.Count / 2;
                }

                if (Util.ChanceDouble(parrychance))
                {
                    ad.Target = dashing.GuardSource;
                    result = eAttackResult.Parried;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Returns the result of an enemy attack
        /// </summary>
        public virtual eAttackResult CalculateEnemyAttackResult(WeaponAction action, AttackData ad, DbInventoryItem attackerWeapon)
        {
            if (owner.EffectList.CountOfType<NecromancerShadeEffect>() > 0)
                return eAttackResult.NoValidTarget;

            //1.To-Hit modifiers on styles do not any effect on whether your opponent successfully Evades, Blocks, or Parries.  Grab Bag 2/27/03
            //2.The correct Order of Resolution in combat is Intercept, Evade, Parry, Block (Shield), Guard, Hit/Miss, and then Bladeturn.  Grab Bag 2/27/03, Grab Bag 4/4/03
            //3.For every person attacking a monster, a small bonus is applied to each player's chance to hit the enemy. Allowances are made for those who don't technically hit things when they are participating in the raid  for example, a healer gets credit for attacking a monster when he heals someone who is attacking the monster, because that's what he does in a battle.  Grab Bag 6/6/03
            //4.Block, parry, and bolt attacks are affected by this code, as you know. We made a fix to how the code counts people as "in combat." Before this patch, everyone grouped and on the raid was counted as "in combat." The guy AFK getting Mountain Dew was in combat, the level five guy hovering in the back and hoovering up some exp was in combat  if they were grouped with SOMEONE fighting, they were in combat. This was a bad thing for block, parry, and bolt users, and so we fixed it.  Grab Bag 6/6/03
            //5.Positional degrees - Side Positional combat styles now will work an extra 15 degrees towards the rear of an opponent, and rear position styles work in a 60 degree arc rather than the original 90 degree standard. This change should even out the difficulty between side and rear positional combat styles, which have the same damage bonus. Please note that front positional styles are not affected by this change. 1.62
            //http://daoc.catacombs.com/forum.cfm?ThreadKey=511&DefMessage=681444&forum=DAOCMainForum#Defense

            InterceptECSGameEffect intercept = null;
            ECSGameSpellEffect bladeturn = null;
            // ML effects
            GameSpellEffect phaseshift = null;
            GameSpellEffect grapple = null;
            GameSpellEffect brittleguard = null;

            AttackData lastAttackData = owner.TempProperties.GetProperty<AttackData>(LAST_ATTACK_DATA, null);
            bool defenseDisabled = ad.Target.IsMezzed | ad.Target.IsStunned | ad.Target.IsSitting;

            GamePlayer playerOwner = owner as GamePlayer;
            GamePlayer playerAttacker = ad.Attacker as GamePlayer;

            // If berserk is on, no defensive skills may be used: evade, parry, ...
            // unfortunately this as to be check for every action itself to kepp oder of actions the same.
            // Intercept and guard can still be used on berserked
            // BerserkEffect berserk = null;

            if (EffectListService.GetAbilityEffectOnTarget(owner, eEffect.Berserk) != null)
                defenseDisabled = true;

            if (EffectListService.GetSpellEffectOnTarget(owner, eEffect.Bladeturn) is ECSGameSpellEffect bladeturnEffect)
            {
                if (bladeturn == null)
                    bladeturn = bladeturnEffect;
            }

            // We check if interceptor can intercept.
            if (EffectListService.GetAbilityEffectOnTarget(owner, eEffect.Intercept) is InterceptECSGameEffect inter)
            {
                if (intercept == null && inter != null && inter.InterceptTarget == owner && !inter.InterceptSource.IsStunned && !inter.InterceptSource.IsMezzed
                    && !inter.InterceptSource.IsSitting && inter.InterceptSource.ObjectState == eObjectState.Active && inter.InterceptSource.IsAlive
                    && owner.IsWithinRadius(inter.InterceptSource, InterceptAbilityHandler.INTERCEPT_DISTANCE)) // && Util.Chance(inter.InterceptChance))
                {
                    int interceptRoll;

                    if (!Properties.OVERRIDE_DECK_RNG && playerOwner != null)
                        interceptRoll = playerOwner.RandomNumberDeck.GetInt();
                    else
                        interceptRoll = Util.Random(100);

                    if (inter.InterceptChance > interceptRoll)
                        intercept = inter;
                }
            }

            bool stealthStyle = false;

            if (ad.Style != null && ad.Style.StealthRequirement && ad.Attacker is GamePlayer && StyleProcessor.CanUseStyle(lastAttackData, (GamePlayer) ad.Attacker, ad.Style, attackerWeapon))
            {
                stealthStyle = true;
                defenseDisabled = true;
                intercept = null;
                brittleguard = null;
            }

            if (playerOwner != null)
            {
                GameLiving attacker = ad.Attacker;
                GamePlayer tempPlayerAttacker = playerAttacker ?? ((attacker as GameNPC)?.Brain as IControlledBrain)?.GetPlayerOwner();

                if (tempPlayerAttacker != null && action.ActiveWeaponSlot != eActiveWeaponSlot.Distance)
                {
                    GamePlayer bodyguard = playerOwner.Bodyguard;

                    if (bodyguard != null)
                    {
                        playerOwner.Out.SendMessage(string.Format(LanguageMgr.GetTranslation(playerOwner.Client.Account.Language, "GameLiving.CalculateEnemyAttackResult.YouWereProtected"), bodyguard.Name, attacker.Name), eChatType.CT_Missed, eChatLoc.CL_SystemWindow);
                        bodyguard.Out.SendMessage(string.Format(LanguageMgr.GetTranslation(bodyguard.Client.Account.Language, "GameLiving.CalculateEnemyAttackResult.YouHaveProtected"), playerOwner.Name, attacker.Name), eChatType.CT_Missed, eChatLoc.CL_SystemWindow);

                        if (attacker == tempPlayerAttacker)
                            tempPlayerAttacker.Out.SendMessage(string.Format(LanguageMgr.GetTranslation(tempPlayerAttacker.Client.Account.Language, "GameLiving.CalculateEnemyAttackResult.YouAttempt"), playerOwner.Name, playerOwner.Name, bodyguard.Name), eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                        else
                            tempPlayerAttacker.Out.SendMessage(string.Format(LanguageMgr.GetTranslation(tempPlayerAttacker.Client.Account.Language, "GameLiving.CalculateEnemyAttackResult.YourPetAttempts"), playerOwner.Name, playerOwner.Name, bodyguard.Name), eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);

                        return eAttackResult.Bodyguarded;
                    }
                }
            }

            if (phaseshift != null)
                return eAttackResult.Missed;

            if (grapple != null)
                return eAttackResult.Grappled;

            if (brittleguard != null)
            {
                playerOwner?.Out.SendMessage(LanguageMgr.GetTranslation(playerOwner.Client.Account.Language, "GameLiving.CalculateEnemyAttackResult.BlowIntercepted"), eChatType.CT_SpellResisted, eChatLoc.CL_SystemWindow);
                playerAttacker?.Out.SendMessage(LanguageMgr.GetTranslation(playerAttacker.Client.Account.Language, "GameLiving.CalculateEnemyAttackResult.StrikeIntercepted"), eChatType.CT_SpellResisted, eChatLoc.CL_SystemWindow);
                brittleguard.Cancel(false);
                return eAttackResult.Missed;
            }

            if (intercept != null && !stealthStyle)
            {
                ad.Target = intercept.InterceptSource;

                if (intercept.InterceptSource is GamePlayer)
                    intercept.Cancel(false);

                return eAttackResult.HitUnstyled;
            }

            double attackerConLevel = -owner.GetConLevel(ad.Attacker);

            if (!defenseDisabled)
            {
                if (lastAttackData != null && lastAttackData.AttackResult != eAttackResult.HitStyle)
                    lastAttackData = null;

                double evadeChance = owner.TryEvade(ad, lastAttackData, attackerConLevel, Attackers.Count);
                ad.EvadeChance = evadeChance;
                double evadeRoll;

                if (!Properties.OVERRIDE_DECK_RNG && playerOwner != null)
                    evadeRoll = playerOwner.RandomNumberDeck.GetPseudoDouble();
                else
                    evadeRoll = Util.CryptoNextDouble();

                if (evadeChance > 0)
                {
                    if (ad.Attacker is GamePlayer evadeAtk && evadeAtk.UseDetailedCombatLog)
                        evadeAtk.Out.SendMessage($"target evade%: {evadeChance * 100:0.##} rand: {evadeRoll * 100:0.##}", eChatType.CT_DamageAdd, eChatLoc.CL_SystemWindow);

                    if (ad.Target is GamePlayer evadeTarg && evadeTarg.UseDetailedCombatLog)
                        evadeTarg.Out.SendMessage($"your evade%: {evadeChance * 100:0.##} rand: {evadeRoll * 100:0.##}", eChatType.CT_DamageAdd, eChatLoc.CL_SystemWindow);

                    if (evadeChance > evadeRoll)
                        return eAttackResult.Evaded;
                }

                if (ad.IsMeleeAttack)
                {
                    double parryChance = owner.TryParry(ad, lastAttackData, attackerConLevel, Attackers.Count);
                    ad.ParryChance = parryChance;
                    double parryRoll;

                    if (!Properties.OVERRIDE_DECK_RNG && playerOwner != null)
                        parryRoll = playerOwner.RandomNumberDeck.GetPseudoDouble();
                    else
                        parryRoll = Util.CryptoNextDouble();

                    if (parryChance > 0)
                    {
                        if (ad.Attacker is GamePlayer parryAtk && parryAtk.UseDetailedCombatLog)
                            parryAtk.Out.SendMessage($"target parry%: {parryChance * 100:0.##} rand: {parryRoll * 100:0.##}", eChatType.CT_DamageAdd, eChatLoc.CL_SystemWindow);

                        if (ad.Target is GamePlayer parryTarg && parryTarg.UseDetailedCombatLog)
                            parryTarg.Out.SendMessage($"your parry%: {parryChance * 100:0.##} rand: {parryRoll * 100:0.##}", eChatType.CT_DamageAdd, eChatLoc.CL_SystemWindow);

                        if (parryChance > parryRoll)
                            return eAttackResult.Parried;
                    }
                }

                if (CheckBlock(ad, attackerConLevel))
                    return eAttackResult.Blocked;
            }

            if (CheckGuard(ad, stealthStyle, attackerConLevel))
                return eAttackResult.Blocked;

            // Not implemented.
            // if (CheckDashingDefense(ad, stealthStyle, attackerConLevel, out eAttackResult result)
            //     return result;

            // Miss chance.
            int missChance = GetMissChance(action, ad, lastAttackData, attackerWeapon);

            // Check for dirty trick fumbles before misses.
            DirtyTricksDetrimentalECSGameEffect dt = (DirtyTricksDetrimentalECSGameEffect)EffectListService.GetAbilityEffectOnTarget(ad.Attacker, eEffect.DirtyTricksDetrimental);

            if (dt != null && ad.IsRandomFumble)
                return eAttackResult.Fumbled;

            ad.MissRate = missChance;

            if (missChance > 0)
            {
                double missRoll;

                if (!Properties.OVERRIDE_DECK_RNG && playerAttacker != null)
                    missRoll = playerAttacker.RandomNumberDeck.GetPseudoDouble();
                else
                    missRoll = Util.CryptoNextDouble();

                if (ad.Attacker is GamePlayer misser && misser.UseDetailedCombatLog)
                {
                    misser.Out.SendMessage($"miss rate on target: {missChance}% rand: {missRoll * 100:0.##}", eChatType.CT_DamageAdd, eChatLoc.CL_SystemWindow);
                    misser.Out.SendMessage($"Your chance to fumble: {100 * ad.Attacker.ChanceToFumble:0.##}% rand: {100 * missRoll:0.##}", eChatType.CT_DamageAdd, eChatLoc.CL_SystemWindow);
                }

                if (ad.Target is GamePlayer missee && missee.UseDetailedCombatLog)
                    missee.Out.SendMessage($"chance to be missed: {missChance}% rand: {missRoll * 100:0.##}", eChatType.CT_DamageAdd, eChatLoc.CL_SystemWindow);

                // Check for normal fumbles.
                // NOTE: fumbles are a subset of misses, and a player can only fumble if the attack would have been a miss anyways.
                if (missChance > missRoll * 100)
                {
                    if (ad.Attacker.ChanceToFumble > missRoll)
                        return eAttackResult.Fumbled;

                    return eAttackResult.Missed;
                }
            }

            // Bladeturn
            // TODO: high level mob attackers penetrate bt, players are tested and do not penetrate (lv30 vs lv20)
            /*
             * http://www.camelotherald.com/more/31.shtml
             * - Bladeturns can now be penetrated by attacks from higher level monsters and
             * players. The chance of the bladeturn deflecting a higher level attack is
             * approximately the caster's level / the attacker's level.
             * Please be aware that everything in the game is
             * level/chance based - nothing works 100% of the time in all cases.
             * It was a bug that caused it to work 100% of the time - now it takes the
             * levels of the players involved into account.
             */
            if (bladeturn != null)
            {
                bool penetrate = false;

                if (stealthStyle)
                    return eAttackResult.HitUnstyled; // Exit early for stealth to prevent breaking bubble but still register a hit.

                if (ad.AttackType == AttackData.eAttackType.Ranged)
                {
                    // 1.62: Penetrating Arrow penetrate only if the caster == target. Longshot and Volley always penetrate BTs.
                    if ((ad.Target != bladeturn.SpellHandler.Caster && playerAttacker != null && playerAttacker.HasAbility(Abilities.PenetratingArrow)) ||
                        action.RangedAttackType == eRangedAttackType.Long ||
                        action.RangedAttackType == eRangedAttackType.Volley)
                    {
                        penetrate = true;
                    }
                }
                else if (ad.IsMeleeAttack && !Util.ChanceDouble(bladeturn.SpellHandler.Caster.Level / (double) ad.Attacker.Level))
                    penetrate = true;

                if (penetrate)
                {
                    if (playerOwner != null)
                    {
                        playerOwner.Out.SendMessage(LanguageMgr.GetTranslation(playerOwner.Client.Account.Language, "GameLiving.CalculateEnemyAttackResult.BlowPenetrated"), eChatType.CT_SpellResisted, eChatLoc.CL_SystemWindow);
                        EffectService.RequestImmediateCancelEffect(bladeturn);
                    }
                }
                else
                {
                    if (playerOwner != null)
                    {
                        playerOwner.Out.SendMessage(LanguageMgr.GetTranslation(playerOwner.Client.Account.Language, "GameLiving.CalculateEnemyAttackResult.BlowAbsorbed"), eChatType.CT_SpellResisted, eChatLoc.CL_SystemWindow);
                        playerOwner.Stealth(false);
                    }

                    playerAttacker?.Out.SendMessage(LanguageMgr.GetTranslation(playerAttacker.Client.Account.Language, "GameLiving.CalculateEnemyAttackResult.StrikeAbsorbed"), eChatType.CT_SpellResisted, eChatLoc.CL_SystemWindow);
                    EffectService.RequestImmediateCancelEffect(bladeturn);
                    return eAttackResult.Missed;
                }
            }

            if (playerOwner?.IsOnHorse == true)
                playerOwner.IsOnHorse = false;

            return eAttackResult.HitUnstyled;
        }

        private int GetBonusCapForLevel(int level)
        {
            int bonusCap = 0;
            if (level < 15) bonusCap = 0;
            else if (level < 20) bonusCap = 5;
            else if (level < 25) bonusCap = 10;
            else if (level < 30) bonusCap = 15;
            else if (level < 35) bonusCap = 20;
            else if (level < 40) bonusCap = 25;
            else if (level < 45) bonusCap = 30;
            else bonusCap = 35;

            return bonusCap;
        }

        /// <summary>
        /// Send the messages to the GamePlayer
        /// </summary>
        public void SendAttackingCombatMessages(WeaponAction action, AttackData ad)
        {
            // Used to prevent combat log spam when the target is out of range, dead, not visible, etc.
            // A null attackAction means it was cleared up before we had a chance to send combat messages.
            // This typically happens when a ranged weapon is shot once without auto reloading.
            // In this case, we simply assume the last round should show a combat message.
            if (attackAction != null)
            {
                if (ad.AttackResult is not eAttackResult.Missed
                    and not eAttackResult.HitUnstyled
                    and not eAttackResult.HitStyle
                    and not eAttackResult.Evaded
                    and not eAttackResult.Blocked
                    and not eAttackResult.Parried)
                {
                    if (GameLoop.GameLoopTime - attackAction.RoundWithNoAttackTime <= 1500)
                        return;

                    attackAction.RoundWithNoAttackTime = 0;
                }
            }

            if (owner is GamePlayer)
            {
                var p = owner as GamePlayer;

                GameObject target = ad.Target;
                DbInventoryItem weapon = ad.Weapon;
                if (ad.Target is GameNPC)
                {
                    switch (ad.AttackResult)
                    {
                        case eAttackResult.TargetNotVisible:
                            p.Out.SendMessage(LanguageMgr.GetTranslation(p.Client.Account.Language,
                                    "GamePlayer.Attack.NotInView",
                                    ad.Target.GetName(0, true, p.Client.Account.Language, (ad.Target as GameNPC))),
                                eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                            break;
                        case eAttackResult.OutOfRange:
                            p.Out.SendMessage(LanguageMgr.GetTranslation(p.Client.Account.Language,
                                    "GamePlayer.Attack.TooFarAway",
                                    ad.Target.GetName(0, true, p.Client.Account.Language, (ad.Target as GameNPC))),
                                eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                            break;
                        case eAttackResult.TargetDead:
                            p.Out.SendMessage(LanguageMgr.GetTranslation(p.Client.Account.Language,
                                    "GamePlayer.Attack.AlreadyDead",
                                    ad.Target.GetName(0, true, p.Client.Account.Language, (ad.Target as GameNPC))),
                                eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                            break;
                        case eAttackResult.Blocked:
                            p.Out.SendMessage(LanguageMgr.GetTranslation(p.Client.Account.Language,
                                    "GamePlayer.Attack.Blocked",
                                    ad.Target.GetName(0, true, p.Client.Account.Language, (ad.Target as GameNPC))),
                                eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                            break;
                        case eAttackResult.Parried:
                            p.Out.SendMessage(LanguageMgr.GetTranslation(p.Client.Account.Language,
                                    "GamePlayer.Attack.Parried",
                                    ad.Target.GetName(0, true, p.Client.Account.Language, (ad.Target as GameNPC))),
                                eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                            break;
                        case eAttackResult.Evaded:
                            p.Out.SendMessage(LanguageMgr.GetTranslation(p.Client.Account.Language,
                                    "GamePlayer.Attack.Evaded",
                                    ad.Target.GetName(0, true, p.Client.Account.Language, (ad.Target as GameNPC))),
                                eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                            break;
                        case eAttackResult.NoTarget:
                            p.Out.SendMessage(
                                LanguageMgr.GetTranslation(p.Client.Account.Language, "GamePlayer.Attack.NeedTarget"),
                                eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                            break;
                        case eAttackResult.NoValidTarget:
                            p.Out.SendMessage(
                                LanguageMgr.GetTranslation(p.Client.Account.Language,
                                    "GamePlayer.Attack.CantBeAttacked"), eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                            break;
                        case eAttackResult.Missed:
                            string message;
                            if (ad.MissRate > 0)
                                message = LanguageMgr.GetTranslation(p.Client.Account.Language, "GamePlayer.Attack.Miss") + $" ({ad.MissRate}%)";
                            else
                                message = LanguageMgr.GetTranslation(p.Client.Account.Language, "GamePlayer.Attack.StrafMiss");
                            p.Out.SendMessage(message, eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                            break;
                        case eAttackResult.Fumbled:
                            p.Out.SendMessage(
                                LanguageMgr.GetTranslation(p.Client.Account.Language, "GamePlayer.Attack.Fumble"),
                                eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                            break;
                        case eAttackResult.HitStyle:
                        case eAttackResult.HitUnstyled:
                            string modmessage = "";
                            if (ad.Modifier > 0) modmessage = " (+" + ad.Modifier + ")";
                            if (ad.Modifier < 0) modmessage = " (" + ad.Modifier + ")";

                            string hitWeapon = "";

                            switch (p.Client.Account.Language)
                            {
                                case "DE":
                                    if (weapon != null)
                                        hitWeapon = weapon.Name;
                                    break;
                                default:
                                    if (weapon != null)
                                        hitWeapon = GlobalConstants.NameToShortName(weapon.Name);
                                    break;
                            }

                            if (hitWeapon.Length > 0)
                                hitWeapon = " " +
                                            LanguageMgr.GetTranslation(p.Client.Account.Language,
                                                "GamePlayer.Attack.WithYour") + " " + hitWeapon;

                            string attackTypeMsg = LanguageMgr.GetTranslation(p.Client.Account.Language,
                                "GamePlayer.Attack.YouAttack");
                            if (action.ActiveWeaponSlot == eActiveWeaponSlot.Distance)
                                attackTypeMsg = LanguageMgr.GetTranslation(p.Client.Account.Language,
                                    "GamePlayer.Attack.YouShot");

                            // intercept messages
                            if (target != null && target != ad.Target)
                            {
                                p.Out.SendMessage(
                                    LanguageMgr.GetTranslation(p.Client.Account.Language,
                                        "GamePlayer.Attack.Intercepted", ad.Target.GetName(0, true),
                                        target.GetName(0, false)), eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                                p.Out.SendMessage(
                                    LanguageMgr.GetTranslation(p.Client.Account.Language,
                                        "GamePlayer.Attack.InterceptedHit", attackTypeMsg, target.GetName(0, false),
                                        hitWeapon, ad.Target.GetName(0, false), ad.Damage, modmessage),
                                    eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                            }
                            else
                                p.Out.SendMessage(LanguageMgr.GetTranslation(p.Client.Account.Language,
                                    "GamePlayer.Attack.InterceptHit", attackTypeMsg,
                                    ad.Target.GetName(0, false, p.Client.Account.Language, (ad.Target as GameNPC)),
                                    hitWeapon, ad.Damage, modmessage), eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);

                            // critical hit
                            if (ad.CriticalDamage > 0)
                                p.Out.SendMessage(LanguageMgr.GetTranslation(p.Client.Account.Language,
                                        "GamePlayer.Attack.Critical",
                                        ad.Target.GetName(0, false, p.Client.Account.Language, (ad.Target as GameNPC)),
                                        ad.CriticalDamage) + $" ({AttackCriticalChance(action, ad.Weapon)}%)",
                                    eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                            break;
                    }
                }
                else
                {
                    switch (ad.AttackResult)
                    {
                        case eAttackResult.TargetNotVisible:
                            p.Out.SendMessage(
                                LanguageMgr.GetTranslation(p.Client.Account.Language, "GamePlayer.Attack.NotInView",
                                    ad.Target.GetName(0, true)), eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                            break;
                        case eAttackResult.OutOfRange:
                            p.Out.SendMessage(
                                LanguageMgr.GetTranslation(p.Client.Account.Language, "GamePlayer.Attack.TooFarAway",
                                    ad.Target.GetName(0, true)), eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                            break;
                        case eAttackResult.TargetDead:
                            p.Out.SendMessage(
                                LanguageMgr.GetTranslation(p.Client.Account.Language, "GamePlayer.Attack.AlreadyDead",
                                    ad.Target.GetName(0, true)), eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                            break;
                        case eAttackResult.Blocked:
                            p.Out.SendMessage(
                                LanguageMgr.GetTranslation(p.Client.Account.Language, "GamePlayer.Attack.Blocked",
                                    ad.Target.GetName(0, true)), eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                            break;
                        case eAttackResult.Parried:
                            p.Out.SendMessage(
                                LanguageMgr.GetTranslation(p.Client.Account.Language, "GamePlayer.Attack.Parried",
                                    ad.Target.GetName(0, true)), eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                            break;
                        case eAttackResult.Evaded:
                            p.Out.SendMessage(
                                LanguageMgr.GetTranslation(p.Client.Account.Language, "GamePlayer.Attack.Evaded",
                                    ad.Target.GetName(0, true)), eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                            break;
                        case eAttackResult.NoTarget:
                            p.Out.SendMessage(
                                LanguageMgr.GetTranslation(p.Client.Account.Language, "GamePlayer.Attack.NeedTarget"),
                                eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                            break;
                        case eAttackResult.NoValidTarget:
                            p.Out.SendMessage(
                                LanguageMgr.GetTranslation(p.Client.Account.Language,
                                    "GamePlayer.Attack.CantBeAttacked"), eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                            break;
                        case eAttackResult.Missed:
                            string message;
                            if (ad.MissRate > 0)
                                message = LanguageMgr.GetTranslation(p.Client.Account.Language, "GamePlayer.Attack.Miss") + $" ({ad.MissRate}%)";
                            else
                                message = LanguageMgr.GetTranslation(p.Client.Account.Language, "GamePlayer.Attack.StrafMiss");
                            p.Out.SendMessage(message, eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                            break;
                        case eAttackResult.Fumbled:
                            p.Out.SendMessage(
                                LanguageMgr.GetTranslation(p.Client.Account.Language, "GamePlayer.Attack.Fumble"),
                                eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                            break;
                        case eAttackResult.HitStyle:
                        case eAttackResult.HitUnstyled:
                            string modmessage = "";
                            if (ad.Modifier > 0) modmessage = " (+" + ad.Modifier + ")";
                            if (ad.Modifier < 0) modmessage = " (" + ad.Modifier + ")";

                            string hitWeapon = "";

                            switch (p.Client.Account.Language)
                            {
                                case "DE":
                                    if (weapon != null)
                                        hitWeapon = weapon.Name;
                                    break;
                                default:
                                    if (weapon != null)
                                        hitWeapon = GlobalConstants.NameToShortName(weapon.Name);
                                    break;
                            }

                            if (hitWeapon.Length > 0)
                                hitWeapon = " " +
                                            LanguageMgr.GetTranslation(p.Client.Account.Language,
                                                "GamePlayer.Attack.WithYour") + " " + hitWeapon;

                            string attackTypeMsg = LanguageMgr.GetTranslation(p.Client.Account.Language,
                                "GamePlayer.Attack.YouAttack");
                            if (action.ActiveWeaponSlot == eActiveWeaponSlot.Distance)
                                attackTypeMsg = LanguageMgr.GetTranslation(p.Client.Account.Language,
                                    "GamePlayer.Attack.YouShot");

                            // intercept messages
                            if (target != null && target != ad.Target)
                            {
                                p.Out.SendMessage(
                                    LanguageMgr.GetTranslation(p.Client.Account.Language,
                                        "GamePlayer.Attack.Intercepted", ad.Target.GetName(0, true),
                                        target.GetName(0, false)), eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                                p.Out.SendMessage(
                                    LanguageMgr.GetTranslation(p.Client.Account.Language,
                                        "GamePlayer.Attack.InterceptedHit", attackTypeMsg, target.GetName(0, false),
                                        hitWeapon, ad.Target.GetName(0, false), ad.Damage, modmessage),
                                    eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
                            }
                            else
                                p.Out.SendMessage(
                                    LanguageMgr.GetTranslation(p.Client.Account.Language,
                                        "GamePlayer.Attack.InterceptHit", attackTypeMsg, ad.Target.GetName(0, false),
                                        hitWeapon, ad.Damage, modmessage), eChatType.CT_YouHit,
                                    eChatLoc.CL_SystemWindow);

                            // critical hit
                            if (ad.CriticalDamage > 0)
                                p.Out.SendMessage(
                                    LanguageMgr.GetTranslation(p.Client.Account.Language,
                                        "GamePlayer.Attack.Critical", ad.Target.GetName(0, false),
                                        ad.CriticalDamage) + $" ({AttackCriticalChance(action, ad.Weapon)}%)", eChatType.CT_YouHit,
                                    eChatLoc.CL_SystemWindow);
                            break;
                    }
                }
            }
        }

        public virtual int CalculateMeleeCriticalDamage(AttackData ad, WeaponAction action, DbInventoryItem weapon)
        {
            if (!Util.Chance(AttackCriticalChance(action, weapon)))
                return 0;

            if (owner is GamePlayer)
            {
                // triple wield prevents critical hits
                if (EffectListService.GetAbilityEffectOnTarget(ad.Target, eEffect.TripleWield) != null)
                    return 0;

                int critMin;
                int critMax;
                ECSGameEffect berserk = EffectListService.GetEffectOnTarget(owner, eEffect.Berserk);

                if (berserk != null)
                {
                    int level = owner.GetAbilityLevel(Abilities.Berserk);
                    // According to : http://daoc.catacombs.com/forum.cfm?ThreadKey=10833&DefMessage=922046&forum=37
                    // Zerk 1 = 1-25%
                    // Zerk 2 = 1-50%
                    // Zerk 3 = 1-75%
                    // Zerk 4 = 1-99%
                    critMin = (int) (0.01 * ad.Damage);
                    critMax = (int) (Math.Min(0.99, level * 0.25) * ad.Damage);
                }
                else
                {
                    // Min crit damage is 10%.
                    critMin = ad.Damage / 10;
                    // Max crit damage to players is 50%. Berzerkers go up to 99% in Berserk mode.

                    if (ad.Target is GamePlayer)
                        critMax = ad.Damage / 2;
                    else
                        critMax = ad.Damage;
                }

                critMin = Math.Max(critMin, 0);
                critMax = Math.Max(critMin, critMax);
                return ad.CriticalDamage = Util.Random(critMin, critMax);
            }
            else
            {
                int maxCriticalDamage = ad.Target is GamePlayer ? ad.Damage / 2 : ad.Damage;
                int minCriticalDamage = (int) (ad.Damage * MinMeleeCriticalDamage);

                if (minCriticalDamage > maxCriticalDamage)
                    minCriticalDamage = maxCriticalDamage;

                return Util.Random(minCriticalDamage, maxCriticalDamage);
            }
        }

        public virtual int GetMissChance(WeaponAction action, AttackData ad, AttackData lastAD, DbInventoryItem weapon)
        {
            // No miss if the target is sitting or for Volley attacks.
             if ((owner is GamePlayer player && player.IsSitting) || action.RangedAttackType == eRangedAttackType.Volley)
                return 0;

            int missChance = ad.Attacker is GamePlayer or GameSummonedPet ? 18 : 25;
            missChance -= ad.Attacker.GetModified(eProperty.ToHitBonus);

            // PVE group miss rate.
            if (owner is GameNPC && ad.Attacker is GamePlayer playerAttacker && playerAttacker.Group != null && (int) (0.90 * playerAttacker.Group.Leader.Level) >= ad.Attacker.Level && ad.Attacker.IsWithinRadius(playerAttacker.Group.Leader, 3000))
                missChance -= (int) (5 * playerAttacker.Group.Leader.GetConLevel(owner));
            else if (owner is GameNPC || ad.Attacker is GameNPC)
            {
                GameLiving misscheck = ad.Attacker;

                if (ad.Attacker is GameSummonedPet petAttacker && petAttacker.Level < petAttacker.Owner.Level)
                    misscheck = petAttacker.Owner;

                missChance += (int) (5 * misscheck.GetConLevel(owner));
            }

            // Experimental miss rate adjustment for number of attackers.
            if ((owner is GamePlayer && ad.Attacker is GamePlayer) == false)
                missChance -= Math.Max(0, Attackers.Count - 1) * Properties.MISSRATE_REDUCTION_PER_ATTACKERS;

            // Weapon and armor bonuses.
            int armorBonus = 0;

            if (ad.Target is GamePlayer p)
            {
                ad.ArmorHitLocation = ((GamePlayer) ad.Target).CalculateArmorHitLocation(ad);

                if (ad.Target.Inventory != null)
                {
                    DbInventoryItem armor = ad.Target.Inventory.GetItem((eInventorySlot) ad.ArmorHitLocation);

                    if (armor != null)
                        armorBonus = armor.Bonus;
                }

                int bonusCap = GetBonusCapForLevel(p.Level);

                if (armorBonus > bonusCap)
                    armorBonus = bonusCap;
            }

            if (weapon != null)
            {
                int bonusCap = GetBonusCapForLevel(ad.Attacker.Level);
                int weaponBonus = weapon.Bonus;

                if (weaponBonus > bonusCap)
                    weaponBonus = bonusCap;

                armorBonus -= weaponBonus;
            }

            if (ad.Target is GamePlayer && ad.Attacker is GamePlayer)
                missChance += armorBonus;
            else
                missChance += missChance * armorBonus / 100;

            // Style bonuses.
            if (ad.Style != null)
                missChance -= ad.Style.BonusToHit;

            if (lastAD != null && lastAD.AttackResult == eAttackResult.HitStyle && lastAD.Style != null)
                missChance += lastAD.Style.BonusToDefense;

            if (owner is GamePlayer && ad.Attacker is GamePlayer && weapon != null)
                missChance -= (int) ((ad.Attacker.WeaponSpecLevel(weapon) - 1) * 0.1);

            if (action.ActiveWeaponSlot == eActiveWeaponSlot.Distance)
            {
                DbInventoryItem ammo = ad.Attacker.rangeAttackComponent.Ammo;

                if (ammo != null)
                {
                    switch ((ammo.SPD_ABS >> 4) & 0x3)
                    {
                        // http://rothwellhome.org/guides/archery.htm
                        case 0:
                            missChance += (int) Math.Round(missChance * 0.15);
                            break; // Rough
                        //case 1:
                        //  missrate -= 0;
                        //  break;
                        case 2:
                            missChance -= (int) Math.Round(missChance * 0.15);
                            break; // doesn't exist (?)
                        case 3:
                            missChance -= (int) Math.Round(missChance * 0.25);
                            break; // Footed
                    }
                }
            }

            return missChance;
        }

        /// <summary>
        /// Minimum melee critical damage as a percentage of the
        /// raw damage.
        /// </summary>
        protected float MinMeleeCriticalDamage => 0.1f;

        public static double CalculateSlowWeaponDamageModifier(DbInventoryItem weapon)
        {
            // Slow weapon bonus as found here: https://www2.uthgard.net/tracker/issue/2753/@/Bow_damage_variance_issue_(taking_item_/_spec_???)
            return 1 + (weapon.SPD_ABS - 20) * 0.003;
        }

        public double CalculateTwoHandedDamageModifier(DbInventoryItem weapon)
        {
            return 1.1 + (owner.WeaponSpecLevel(weapon) - 1) * 0.005;
        }

        /// <summary>
        /// Whether the living is actually attacking something.
        /// </summary>
        public virtual bool IsAttacking => AttackState && (attackAction != null);

        /// <summary>
        /// Checks whether Living has ability to use lefthanded weapons
        /// </summary>
        public virtual bool CanUseLefthandedWeapon
        {
            get
            {
                if (owner is GamePlayer)
                    return (owner as GamePlayer).CharacterClass.CanUseLefthandedWeapon;
                else
                    return false;
            }
        }

        /// <summary>
        /// Calculates how many times left hand swings
        /// </summary>
        public int CalculateLeftHandSwingCount()
        {
            if (CanUseLefthandedWeapon == false)
                return 0;

            if (owner.GetBaseSpecLevel(Specs.Left_Axe) > 0)
            {
                if (owner is GamePlayer player && player.UseDetailedCombatLog)
                {
                    int spec = owner.GetModifiedSpecLevel(Specs.Left_Axe);
                    double effectiveness = CalculateLeftAxeModifier();
;
                    player.Out.SendMessage($"{Math.Round(effectiveness * 100, 2)}% dmg (after LA penalty) \n", eChatType.CT_DamageAdd, eChatLoc.CL_SystemWindow);
                }

                return 1; // always use left axe
            }

            int specLevel = Math.Max(owner.GetModifiedSpecLevel(Specs.Celtic_Dual), owner.GetModifiedSpecLevel(Specs.Dual_Wield));
            specLevel = Math.Max(specLevel, owner.GetModifiedSpecLevel(Specs.Fist_Wraps));

            decimal tmpOffhandChance = 25 + (specLevel - 1) * 68 / 100;
            tmpOffhandChance += owner.GetModified(eProperty.OffhandChance) + owner.GetModified(eProperty.OffhandDamageAndChance);

            if (owner is GamePlayer p && p.UseDetailedCombatLog && owner.GetModifiedSpecLevel(Specs.HandToHand) <= 0)
                p.Out.SendMessage($"OH swing%: {Math.Round(tmpOffhandChance, 2)} ({owner.GetModified(eProperty.OffhandChance) + owner.GetModified(eProperty.OffhandDamageAndChance)}% from RAs) \n", eChatType.CT_DamageAdd, eChatLoc.CL_SystemWindow);
                
            if (specLevel > 0)
                return Util.Chance((int) tmpOffhandChance) ? 1 : 0;

            // HtH chance
            specLevel = owner.GetModifiedSpecLevel(Specs.HandToHand);
            DbInventoryItem attackWeapon = owner.ActiveWeapon;
            DbInventoryItem leftWeapon = (owner.Inventory == null) ? null : owner.Inventory.GetItem(eInventorySlot.LeftHandWeapon);

            if (specLevel > 0 && attackWeapon != null && leftWeapon != null && leftWeapon.Object_Type == (int) eObjectType.HandToHand)
            {
                specLevel--;
                int randomChance = Util.Random(99);
                int doubleHitChance = (specLevel >> 1) + owner.GetModified(eProperty.OffhandChance) + owner.GetModified(eProperty.OffhandDamageAndChance);
                int tripleHitChance = doubleHitChance + (specLevel >> 2) + ((owner.GetModified(eProperty.OffhandChance) + owner.GetModified(eProperty.OffhandDamageAndChance)) >> 1);
                int quadHitChance = tripleHitChance + (specLevel >> 4) + ((owner.GetModified(eProperty.OffhandChance) + owner.GetModified(eProperty.OffhandDamageAndChance)) >> 2);

                if (owner is GamePlayer pl && pl.UseDetailedCombatLog)
                    pl.Out.SendMessage( $"Chance for 2 hits: {doubleHitChance}% | 3 hits: { (specLevel > 25 ? tripleHitChance-doubleHitChance : 0)}% | 4 hits: {(specLevel > 40 ? quadHitChance-tripleHitChance : 0)}% \n", eChatType.CT_DamageAdd, eChatLoc.CL_SystemWindow);

                if (randomChance < doubleHitChance)
                    return 1; // 1 hit = spec/2

                //doubleHitChance += specLevel >> 2;
                if (randomChance < tripleHitChance && specLevel > 25)
                    return 2; // 2 hits = spec/4

                //doubleHitChance += specLevel >> 4;
                if (randomChance < quadHitChance && specLevel > 40)
                    return 3; // 3 hits = spec/16
            }

            return 0;
        }

        public double CalculateLeftAxeModifier()
        {
            int LeftAxeSpec = owner.GetModifiedSpecLevel(Specs.Left_Axe);

            if (LeftAxeSpec == 0)
                return 1.0;

            double modifier = 0.625 + 0.0034 * LeftAxeSpec;

            if (owner.GetModified(eProperty.OffhandDamageAndChance) > 0)
                return modifier + owner.GetModified(eProperty.OffhandDamageAndChance) * 0.01;

            return modifier;
        }
    }
}
