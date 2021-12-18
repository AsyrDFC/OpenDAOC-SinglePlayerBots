﻿using DOL.AI.Brain;
using System.Collections.Generic;

namespace DOL.GS {

    /// <summary>
    /// ROGMobGenerator
    /// At the moment this generator only adds ROGs to the loot
    /// </summary>
    public class ROGMobGenerator : LootGeneratorBase {

        //base chance in %
        public static ushort BASE_ROG_CHANCE = 35;


        /// <summary>
        /// Generate loot for given mob
        /// </summary>
        /// <param name="mob"></param>
        /// <param name="killer"></param>
        /// <returns></returns>
        public override LootList GenerateLoot(GameNPC mob, GameObject killer)
        {
            LootList loot = base.GenerateLoot(mob, killer);

            try
            {
                GamePlayer player = killer as GamePlayer;
                if (killer is GameNPC && ((GameNPC)killer).Brain is IControlledBrain)
                {
                    player = ((ControlledNpcBrain)((GameNPC)killer).Brain).GetPlayerOwner();
                }

                if (player == null)
                {
                    return loot;
                }

                int killedcon = (int)player.GetConLevel(mob) + 3; //+3 offsets grey mobs

                //grey con dont drop loot
                if (killedcon <= 0)
                {
                    return loot;
                }

                eCharacterClass classForLoot = (eCharacterClass)player.CharacterClass.ID;
                // allow the leader to decide the loot realm
                if (player.Group != null)
                {
                    player = player.Group.Leader;
                }

                // chance to get a RoG Item
                int chance = BASE_ROG_CHANCE + ((int)killedcon) * 2;

                int lvl = mob.Level + 1;
                if (lvl < 1)
                {
                    lvl = 1;
                }

                //players below level 50 will always get loot for their class, 
                //or a valid class for one of their groupmates
                if (player.Level < 50 && player.Group != null)
                {
                    classForLoot = GetRandomClassFromGroup(player.Group);
                }
                else
                {
                    //level 50 players have a base 10% chance to recieve ROGs from a random class other than their own
                    if (Util.Chance(10))
                    {
                        classForLoot = GetRandomClassFromRealm(player.Realm);
                    }
                }

                GeneratedUniqueItem item = AtlasROGManager.GenerateMonsterLootROG(player.Realm, classForLoot, (byte)(mob.Level + 1));
                item.GenerateItemQuality(killedcon);
                if(mob.Level < 10)
                    loot.AddRandom(100, item, 1);
                else if (mob.Level < 20)
                    loot.AddRandom(BASE_ROG_CHANCE + (100 * (10-(mob.Level-10)/10)), item, 1);
                //135% chance at level 10, 85% chance at level 15, 35% chance at level 20
                else
                    loot.AddRandom(BASE_ROG_CHANCE, item, 1);

            }
            catch
            {
                return loot;
            }

            return loot;
        }

        private eCharacterClass GetRandomClassFromGroup(Group group)
        {
            List<eCharacterClass> validClasses = new List<eCharacterClass>();
            foreach (GamePlayer player in group.GetMembersInTheGroup())
            {
                validClasses.Add((eCharacterClass)player.CharacterClass.ID);
            }

            return validClasses[Util.Random(validClasses.Count - 1)];

        }

        private eCharacterClass GetRandomClassFromRealm(eRealm realm)
        {
            List<eCharacterClass> classesForRealm = new List<eCharacterClass>();
            switch (realm)
            {
                case eRealm.Albion:
                    classesForRealm.Add(eCharacterClass.Armsman);
                    classesForRealm.Add(eCharacterClass.Cabalist);
                    classesForRealm.Add(eCharacterClass.Cleric);
                    classesForRealm.Add(eCharacterClass.Friar);
                    classesForRealm.Add(eCharacterClass.Infiltrator);
                    classesForRealm.Add(eCharacterClass.Mercenary);
                    classesForRealm.Add(eCharacterClass.Necromancer);
                    classesForRealm.Add(eCharacterClass.Paladin);
                    classesForRealm.Add(eCharacterClass.Reaver);
                    classesForRealm.Add(eCharacterClass.Scout);
                    classesForRealm.Add(eCharacterClass.Sorcerer);
                    classesForRealm.Add(eCharacterClass.Theurgist);
                    classesForRealm.Add(eCharacterClass.Wizard);
                    break;
                case eRealm.Midgard:
                    classesForRealm.Add(eCharacterClass.Berserker);
                    classesForRealm.Add(eCharacterClass.Bonedancer);
                    classesForRealm.Add(eCharacterClass.Healer);
                    classesForRealm.Add(eCharacterClass.Hunter);
                    classesForRealm.Add(eCharacterClass.Runemaster);
                    classesForRealm.Add(eCharacterClass.Savage);
                    classesForRealm.Add(eCharacterClass.Shadowblade);
                    classesForRealm.Add(eCharacterClass.Skald);
                    classesForRealm.Add(eCharacterClass.Spiritmaster);
                    classesForRealm.Add(eCharacterClass.Thane);
                    classesForRealm.Add(eCharacterClass.Warrior);
                    break;
                case eRealm.Hibernia:
                    classesForRealm.Add(eCharacterClass.Animist);
                    classesForRealm.Add(eCharacterClass.Bard);
                    classesForRealm.Add(eCharacterClass.Blademaster);
                    classesForRealm.Add(eCharacterClass.Champion);
                    classesForRealm.Add(eCharacterClass.Druid);
                    classesForRealm.Add(eCharacterClass.Eldritch);
                    classesForRealm.Add(eCharacterClass.Enchanter);
                    classesForRealm.Add(eCharacterClass.Hero);
                    classesForRealm.Add(eCharacterClass.Mentalist);
                    classesForRealm.Add(eCharacterClass.Nightshade);
                    classesForRealm.Add(eCharacterClass.Ranger);
                    classesForRealm.Add(eCharacterClass.Valewalker);
                    classesForRealm.Add(eCharacterClass.Warden);
                    break;
            }

            return classesForRealm[Util.Random(classesForRealm.Count - 1)];
        }
    }
}