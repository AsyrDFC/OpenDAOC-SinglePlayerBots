using DOL.AI.Brain;
using DOL.Database;
using DOL.Events;
using DOL.GS.Realm;
using GameServerScripts.Titles;
using log4net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace DOL.GS.Scripts
{
    #region Battlegrounds

    public static class MimicBattlegrounds
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public static MimicBattleground ThidBattleground;

        //BRENT CHANGED FROM 6 min to 24 min and 60 max to 104 max
        public static void Initialize()
        {
            ThidBattleground = new MimicBattleground(252,
                                                    new Point3D(37200, 51200, 3950),
                                                    new Point3D(19820, 19305, 4050),
                                                    new Point3D(53300, 26100, 4270),
                                                    150,
                                                    200,
                                                    20,
                                                    24);
        }

        public class MimicBattleground
        {
            public MimicBattleground(ushort region, Point3D albSpawn, Point3D hibSpawn, Point3D midSpawn, int minMimics, int maxMimics, byte minLevel, byte maxLevel)
            {
                m_region = region;
                m_albSpawnPoint = albSpawn;
                m_hibSpawnPoint = hibSpawn;
                m_midSpawnPoint = midSpawn;
                m_minTotalMimics = minMimics;
                m_maxTotalMimics = maxMimics;
                m_minLevel = minLevel;
                m_maxLevel = maxLevel;
            }

            private ECSGameTimer m_masterTimer;
            private ECSGameTimer m_spawnTimer;

            //BRENT CHANGEDFROM 10 min to 5 min
            private int m_timerInterval = 600000; // 5 minutes
            private long m_resetMaxTime = 0;

            private List<MimicNPC> m_albMimics = new List<MimicNPC>();
            private List<MimicNPC> m_albStagingList = new List<MimicNPC>();

            private List<MimicNPC> m_hibMimics = new List<MimicNPC>();
            private List<MimicNPC> m_hibStagingList = new List<MimicNPC>();

            private List<MimicNPC> m_midMimics = new List<MimicNPC>();
            private List<MimicNPC> m_midStagingList = new List<MimicNPC>();

            private readonly List<BattleStats> m_battleStats = new List<BattleStats>();

            private Point3D m_albSpawnPoint;
            private Point3D m_hibSpawnPoint;
            private Point3D m_midSpawnPoint;

            private ushort m_region;

            private byte m_minLevel;
            private byte m_maxLevel;

            private int m_minTotalMimics;
            private int m_maxTotalMimics;

            private int m_currentMinTotalMimics;
            private int m_currentMaxTotalMimics;

            private int m_currentMaxAlb;
            private int m_currentMaxHib;
            private int m_currentMaxMid;

            //BRENT CHANGED FROM 35 to 100
            private int m_groupChance = 35;

            public void Start()
            {
                if (m_masterTimer == null)
                {
                    m_masterTimer = new ECSGameTimer(null, new ECSGameTimer.ECSTimerCallback(MasterTimerCallback));
                    m_masterTimer.Start();
                }
            }

            public void Stop()
            {
                if (m_masterTimer != null)
                {
                    m_masterTimer.Stop();
                    m_masterTimer = null;
                }

                if (m_spawnTimer != null)
                {
                    m_spawnTimer.Stop();
                    m_spawnTimer = null;
                }

                ValidateLists();

                m_albStagingList.Clear();
                m_hibStagingList.Clear();
                m_midStagingList.Clear();
            }

            public void Clear()
            {
                Stop();

                if (m_albMimics.Any())
                {
                    foreach (MimicNPC mimic in m_albMimics)
                        mimic.Delete();

                    m_albMimics.Clear();
                }

                if (m_hibMimics.Any())
                {
                    foreach (MimicNPC mimic in m_hibMimics)
                        mimic.Delete();

                    m_hibMimics.Clear();
                }

                if (m_midMimics.Any())
                {
                    foreach (MimicNPC mimic in m_midMimics)
                        mimic.Delete();

                    m_midMimics.Clear();
                }
            }

            private int MasterTimerCallback(ECSGameTimer timer)
            {
                if (GameLoop.GetCurrentTime() > m_resetMaxTime)
                    ResetMaxMimics();

                ValidateLists();
                RefreshLists();
                SpawnLists();

                int totalMimics = m_albMimics.Count + m_hibMimics.Count + m_midMimics.Count;
                log.Info("Alb: " + m_albMimics.Count + "/" + m_currentMaxAlb);
                log.Info("Hib: " + m_hibMimics.Count + "/" + m_currentMaxHib);
                log.Info("Mid: " + m_midMimics.Count + "/" + m_currentMaxMid);
                log.Info("Total Mimics: " + totalMimics + "/" + m_currentMaxTotalMimics);

                return m_timerInterval + Util.Random(-300000, 300000); // 10 minutes + or - 5 minutes
            }

            /// <summary>
            /// Removes any dead or deleted mimics from each realm list.
            /// </summary>
            private void ValidateLists()
            {
                if (m_albMimics.Any())
                {
                    List<MimicNPC> validatedList = new List<MimicNPC>();

                    foreach (MimicNPC mimic in m_albMimics)
                    {
                        if (mimic != null && mimic.ObjectState == GameObject.eObjectState.Active && mimic.ObjectState != GameObject.eObjectState.Deleted)
                            validatedList.Add(mimic);
                    }

                    m_albMimics = validatedList;
                }

                if (m_hibMimics.Any())
                {
                    List<MimicNPC> validatedList = new List<MimicNPC>();

                    foreach (MimicNPC mimic in m_hibMimics)
                    {
                        if (mimic != null && mimic.ObjectState == GameObject.eObjectState.Active && mimic.ObjectState != GameObject.eObjectState.Deleted)
                            validatedList.Add(mimic);
                    }

                    m_hibMimics = validatedList;
                }

                if (m_midMimics.Any())
                {
                    List<MimicNPC> validatedList = new List<MimicNPC>();

                    foreach (MimicNPC mimic in m_midMimics)
                    {
                        if (mimic != null && mimic.ObjectState == GameObject.eObjectState.Active && mimic.ObjectState != GameObject.eObjectState.Deleted)
                            validatedList.Add(mimic);
                    }

                    m_midMimics = validatedList;
                }
            }

            /// <summary>
            /// Adds new mimics to each realm list based on the difference between max and current count
            /// </summary>
            private void RefreshLists()
            {
                if (m_albMimics.Count < m_currentMaxAlb)
                {
                    for (int i = 0; i < m_currentMaxAlb - m_albMimics.Count; i++)
                    {
                        byte level = (byte)Util.Random(m_minLevel, m_maxLevel);
                        MimicNPC mimic = MimicManager.GetMimic(MimicManager.GetRandomMimicClass(eRealm.Albion), level);
                        m_albMimics.Add(mimic);
                    }
                }

                if (m_hibMimics.Count < m_currentMaxHib)
                {
                    for (int i = 0; i < m_currentMaxHib - m_hibMimics.Count; i++)
                    {
                        byte level = (byte)Util.Random(m_minLevel, m_maxLevel);
                        MimicNPC mimic = MimicManager.GetMimic(MimicManager.GetRandomMimicClass(eRealm.Hibernia), level);
                        m_hibMimics.Add(mimic);
                    }
                }

                if (m_midMimics.Count < m_currentMaxMid)
                {
                    for (int i = 0; i < m_currentMaxMid - m_midMimics.Count; i++)
                    {
                        byte level = (byte)Util.Random(m_minLevel, m_maxLevel);
                        MimicNPC mimic = MimicManager.GetMimic(MimicManager.GetRandomMimicClass(eRealm.Midgard), level);
                        m_midMimics.Add(mimic);
                    }
                }
            }

            private void SpawnLists()
            {
                m_albStagingList = new List<MimicNPC>();
                m_hibStagingList = new List<MimicNPC>();
                m_midStagingList = new List<MimicNPC>();

                if (m_albMimics.Any())
                {
                    foreach (MimicNPC mimic in m_albMimics)
                    {
                        if (mimic.ObjectState != GameObject.eObjectState.Active)
                            m_albStagingList.Add(mimic);
                    }
                }

                if (m_hibMimics.Any())
                {
                    foreach (MimicNPC mimic in m_hibMimics)
                    {
                        if (mimic.ObjectState != GameObject.eObjectState.Active)
                            m_hibStagingList.Add(mimic);
                    }
                }

                if (m_midMimics.Any())
                {
                    foreach (MimicNPC mimic in m_midMimics)
                    {
                        if (mimic.ObjectState != GameObject.eObjectState.Active)
                            m_midStagingList.Add(mimic);
                    }
                }

                SetGroupMembers(m_albStagingList);
                SetGroupMembers(m_hibStagingList);
                SetGroupMembers(m_midStagingList);

                m_spawnTimer = new ECSGameTimer(null, new ECSGameTimer.ECSTimerCallback(Spawn), 500);
            }

            private int Spawn(ECSGameTimer timer)
            {
                bool albDone = false;
                bool hibDone = false;
                bool midDone = false;

                if (m_albStagingList.Any())
                {
                    MimicManager.AddMimicToWorld(m_albStagingList[m_albStagingList.Count - 1], m_albSpawnPoint, m_region);
                    m_albStagingList.RemoveAt(m_albStagingList.Count - 1);
                }
                else
                    albDone = true;

                if (m_hibStagingList.Any())
                {
                    MimicManager.AddMimicToWorld(m_hibStagingList[m_hibStagingList.Count - 1], m_hibSpawnPoint, m_region);
                    m_hibStagingList.RemoveAt(m_hibStagingList.Count - 1);
                }
                else
                    hibDone = true;

                if (m_midStagingList.Any())
                {
                    MimicManager.AddMimicToWorld(m_midStagingList[m_midStagingList.Count - 1], m_midSpawnPoint, m_region);
                    m_midStagingList.RemoveAt(m_midStagingList.Count - 1);
                }
                else
                    midDone = true;

                if (albDone && hibDone && midDone)
                    return 0;
                else
                    return 3000;
            }

            private void SetGroupMembers(List<MimicNPC> list)
            {
                if (list.Count > 1)
                {
                    int groupChance = m_groupChance;
                    int groupLeaderIndex = -1;

                    for (int i = 0; i < list.Count; i++)
                    {
                        if (i + 1 < list.Count)
                        {
                            if (Util.Chance(groupChance))
                            {
                                if (groupLeaderIndex == -1)
                                {
                                    list[i].Group = new Group(list[i]);
                                    list[i].Group.AddMember(list[i]);
                                    groupLeaderIndex = i;
                                }

                                list[groupLeaderIndex].Group.AddMember(list[i + 1]);
                                groupChance -= 5;
                            }
                            else
                            {
                                groupLeaderIndex = -1;
                                groupChance = m_groupChance;
                            }
                        }
                    }
                }
            }

            /// <summary>
            /// Gets a new total maximum of mimics and maximum of mimics for each realm randomly.
            /// </summary>
            private void ResetMaxMimics()
            {
                m_currentMaxTotalMimics = Util.Random(m_minTotalMimics, m_maxTotalMimics);
                m_currentMaxAlb = 0;
                m_currentMaxHib = 0;
                m_currentMaxMid = 0;

                for (int i = 0; i < m_currentMaxTotalMimics; i++)
                {
                    eRealm randomRealm = (eRealm)Util.Random(1, 3);

                    if (randomRealm == eRealm.Albion)
                        m_currentMaxAlb++;
                    else if (randomRealm == eRealm.Hibernia)
                        m_currentMaxHib++;
                    else if (randomRealm == eRealm.Midgard)
                        m_currentMaxMid++;
                }

                m_resetMaxTime = GameLoop.GetCurrentTime() + Util.Random(1800000, 3600000);
            }

            public void UpdateBattleStats(MimicNPC mimic)
            {
                m_battleStats.Add(new BattleStats(mimic.Name, mimic.RaceName, mimic.CharacterClass.Name, mimic.Kills, true));
            }

            public void BattlegroundStats(GamePlayer player)
            {
                List<MimicNPC> currentMimics = GetMasterList();
                List<BattleStats> currentStats = new List<BattleStats>();

                if (currentMimics.Any())
                {
                    foreach (MimicNPC mimic in currentMimics)
                        currentStats.Add(new BattleStats(mimic.Name, mimic.RaceName, mimic.CharacterClass.Name, mimic.Kills, false));
                }

                List<BattleStats> masterStatList = new List<BattleStats>();
                masterStatList.AddRange(currentStats);

                lock (m_battleStats)
                {
                    masterStatList.AddRange(m_battleStats);
                }

                List<BattleStats> sortedList = masterStatList.OrderByDescending(obj => obj.TotalKills).ToList();

                string message = "----------------------------------------\n\n";
                //BRENT CHANGED FROM 25 to 60
                int index = Math.Min(200, sortedList.Count);

                if (sortedList.Any())
                {
                    for (int i = 0; i < index; i++)
                    {
                        string stats = string.Format("{0}. {1} - {2} - {3} - Kills: {4}",
                            i + 1,
                            sortedList[i].Name,
                            sortedList[i].Race,
                            sortedList[i].ClassName,
                            sortedList[i].TotalKills);

                        if (sortedList[i].IsDead)
                            stats += " - DEAD";

                        stats += "\n\n";

                        message += stats;
                    }
                }

                switch (player.Realm)
                {
                    case eRealm.Albion:
                        if (m_albMimics.Any())
                            message += "Alb count: " + m_albMimics.Count;
                        break;

                    case eRealm.Hibernia:
                        if (m_hibMimics.Any())
                            message += "Hib count: " + m_hibMimics.Count;
                        break;

                    case eRealm.Midgard:
                        if (m_midMimics.Any())
                            message += "Mid count: " + m_midMimics.Count;
                        break;
                }

                //BRENT ADDED FOR ADMIN TO SEE TOTAL MIMICS
                if (player.Client.Account.PrivLevel == 3)
                {
                    message += "\n\nTotal Mimics: " + currentMimics.Count;

                    if (m_albMimics.Any())
                    {
                        message += "\nAlb count: " + m_albMimics.Count;
                        foreach (MimicNPC mimic in m_albMimics)
                        {
                            string stats = string.Format("{0} - {1} - {2} - Kills: {3}",
                                mimic.Name,
                                mimic.RaceName,
                                mimic.CharacterClass.Name,
                                mimic.Kills);

                            if (mimic.isDeadOrDying)
                                stats += " - DEAD";

                            message += "\n" + stats;
                        }
                    }

                    if (m_hibMimics.Any())
                    {
                        message += "\nHib count: " + m_hibMimics.Count;
                        foreach (MimicNPC mimic in m_hibMimics)
                        {
                            string stats = string.Format("{0} - {1} - {2} - Kills: {3}",
                                mimic.Name,
                                mimic.RaceName,
                                mimic.CharacterClass.Name,
                                mimic.Kills);

                            if (mimic.isDeadOrDying)
                                stats += " - DEAD";

                            message += "\n" + stats;
                        }
                    }

                    if (m_midMimics.Any())
                    {
                        message += "\nMid count: " + m_midMimics.Count;
                        foreach (MimicNPC mimic in m_midMimics)
                        {
                            string stats = string.Format("{0} - {1} - {2} - Kills: {3}",
                                mimic.Name,
                                mimic.RaceName,
                                mimic.CharacterClass.Name,
                                mimic.Kills);

                            if (mimic.isDeadOrDying)
                                stats += " - DEAD";

                            message += "\n" + stats;
                        }
                    }
                }
                //BRENT COMMENTED OUT FOR NOW
                // player.Out.SendMessage(message, PacketHandler.eChatType.CT_System, PacketHandler.eChatLoc.CL_PopupWindow);
                log.Info(message);
            }

            public List<MimicNPC> GetMasterList()
            {
                List<MimicNPC> masterList = new List<MimicNPC>();

                lock (m_albMimics)
                {
                    foreach (MimicNPC mimic in m_albMimics)
                    {
                        if (mimic != null && mimic.ObjectState == GameObject.eObjectState.Active && mimic.ObjectState != GameObject.eObjectState.Deleted)
                            masterList.Add(mimic);
                    }
                }

                lock (m_hibMimics)
                {
                    foreach (MimicNPC mimic in m_hibMimics)
                    {
                        if (mimic != null && mimic.ObjectState == GameObject.eObjectState.Active && mimic.ObjectState != GameObject.eObjectState.Deleted)
                            masterList.Add(mimic);
                    }
                }

                lock (m_midMimics)
                {
                    foreach (MimicNPC mimic in m_midMimics)
                    {
                        if (mimic != null && mimic.ObjectState == GameObject.eObjectState.Active && mimic.ObjectState != GameObject.eObjectState.Deleted)
                            masterList.Add(mimic);
                    }
                }

                return masterList;
            }
        }

        private struct BattleStats
        {
            public string Name;
            public string Race;
            public string ClassName;
            public int TotalKills;
            public bool IsDead;

            public BattleStats(string name, string race, string className, int totalKills, bool dead)
            {
                Name = name;
                Race = race;
                ClassName = className;
                TotalKills = totalKills;
                IsDead = dead;
            }
        }
    }

    #endregion Battlegrounds

    public static class MimicManager
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public static List<MimicNPC> MimicNPCs = new List<MimicNPC>();

        public static Faction alb = new Faction();
        public static Faction hib = new Faction();
        public static Faction mid = new Faction();

        public static bool Initialize()
        {
            // Factions
            alb.AddEnemyFaction(hib);
            alb.AddEnemyFaction(mid);

            hib.AddEnemyFaction(alb);
            hib.AddEnemyFaction(mid);

            mid.AddEnemyFaction(alb);
            mid.AddEnemyFaction(hib);

            // Battlegrounds
            MimicBattlegrounds.Initialize();

            return true;
        }

        public static bool AddMimicToWorld(MimicNPC mimic, Point3D position, ushort region)
        {
            if (mimic != null)
            {
                mimic.X = position.X;
                mimic.Y = position.Y;
                mimic.Z = position.Z;

                mimic.CurrentRegionID = region;

                if (mimic.AddToWorld())
                    return true;
            }

            return false;
        }

        public static MimicNPC GetMimic(eMimicClasses mimicClass, byte level, string name = "", eGender gender = eGender.Neutral, bool preventCombat = false)
        {
            if (mimicClass == eMimicClasses.None)
                return null;

            MimicNPC mimic = null;

            switch (mimicClass)
            {
                case eMimicClasses.Armsman: mimic = new MimicArmsman(level); break;
                case eMimicClasses.Cabalist: mimic = new MimicCabalist(level); break;
                case eMimicClasses.Cleric: mimic = new MimicCleric(level); break;
                case eMimicClasses.Friar: mimic = new MimicFriar(level); break;
                case eMimicClasses.Infiltrator: mimic = new MimicInfiltrator(level); break;
                case eMimicClasses.Mercenary: mimic = new MimicMercenary(level); break;
                case eMimicClasses.Minstrel: mimic = new MimicMinstrel(level); break;
                case eMimicClasses.Paladin: mimic = new MimicPaladin(level); break;
                case eMimicClasses.Reaver: mimic = new MimicReaver(level); break;
                case eMimicClasses.Scout: mimic = new MimicScout(level); break;
                case eMimicClasses.Sorcerer: mimic = new MimicSorcerer(level); break;
                case eMimicClasses.Theurgist: mimic = new MimicTheurgist(level); break;
                case eMimicClasses.Wizard: mimic = new MimicWizard(level); break;

                case eMimicClasses.Bard: mimic = new MimicBard(level); break;
                case eMimicClasses.Blademaster: mimic = new MimicBlademaster(level); break;
                case eMimicClasses.Champion: mimic = new MimicChampion(level); break;
                case eMimicClasses.Druid: mimic = new MimicDruid(level); break;
                case eMimicClasses.Eldritch: mimic = new MimicEldritch(level); break;
                case eMimicClasses.Enchanter: mimic = new MimicEnchanter(level); break;
                case eMimicClasses.Hero: mimic = new MimicHero(level); break;
                case eMimicClasses.Mentalist: mimic = new MimicMentalist(level); break;
                case eMimicClasses.Nightshade: mimic = new MimicNightshade(level); break;
                case eMimicClasses.Ranger: mimic = new MimicRanger(level); break;
                case eMimicClasses.Valewalker: mimic = new MimicValewalker(level); break;
                case eMimicClasses.Warden: mimic = new MimicWarden(level); break;

                case eMimicClasses.Berserker: mimic = new MimicBerserker(level); break;
                case eMimicClasses.Bonedancer: mimic = new MimicBonedancer(level); break;
                case eMimicClasses.Healer: mimic = new MimicHealer(level); break;
                case eMimicClasses.Hunter: mimic = new MimicHunter(level); break;
                case eMimicClasses.Runemaster: mimic = new MimicRunemaster(level); break;
                case eMimicClasses.Savage: mimic = new MimicSavage(level); break;
                case eMimicClasses.Shadowblade: mimic = new MimicShadowblade(level); break;
                case eMimicClasses.Shaman: mimic = new MimicShaman(level); break;
                case eMimicClasses.Skald: mimic = new MimicSkald(level); break;
                case eMimicClasses.Spiritmaster: mimic = new MimicSpiritmaster(level); break;
                case eMimicClasses.Thane: mimic = new MimicThane(level); break;
                case eMimicClasses.Warrior: mimic = new MimicWarrior(level); break;
            }

            if (mimic != null)
            {
                if (name != "")
                    mimic.Name = name;

                if (gender != eGender.Neutral)
                {
                    mimic.Gender = gender;

                    foreach (PlayerRace race in PlayerRace.AllRaces)
                    {
                        if (race.ID == (eRace)mimic.Race)
                        {
                            mimic.Model = (ushort)race.GetModel(gender);
                            break;
                        }
                    }
                }

                if (preventCombat)
                {
                    MimicBrain mimicBrain = mimic.Brain as MimicBrain;

                    if (mimicBrain != null)
                        mimicBrain.PreventCombat = preventCombat;
                }

                return mimic;
            }

            return null;
        }

        public static eMimicClasses GetRandomMimicClass(eRealm realm)
        {
            int randomIndex;

            if (realm == eRealm.Albion)
                randomIndex = Util.Random(12);
            else if (realm == eRealm.Hibernia)
                randomIndex = Util.Random(13, 24);
            else if (realm == eRealm.Midgard)
                randomIndex = Util.Random(25, 36);
            else
                randomIndex = Util.Random(36);

            return (eMimicClasses)randomIndex;
        }

        #region Spec

        // TODO: Will likley need to be able to tell caster specs apart for AI purposes since they operate so differently. Will bring them into here, or use some sort of enum.

        // Albion
        private static Type[] cabalistSpecs = { typeof(MatterCabalist), typeof(BodyCabalist), typeof(SpiritCabalist) };

        // Hibernia
        private static Type[] eldritchSpecs = { typeof(SunEldritch), typeof(ManaEldritch), typeof(VoidEldritch) };

        private static Type[] enchanterSpecs = { typeof(ManaEnchanter), typeof(LightEnchanter) };
        private static Type[] mentalistSpecs = { typeof(LightMentalist), typeof(ManaMentalist), typeof(MentalismMentalist) };

        // Midgard
        private static Type[] healerSpecs = { typeof(PacHealer), typeof(AugHealer) };

        public static MimicSpec Random(MimicNPC mimicNPC)
        {
            switch (mimicNPC)
            {
                // Albion
                case MimicCabalist: return Activator.CreateInstance(cabalistSpecs[Util.Random(cabalistSpecs.Length - 1)]) as MimicSpec;

                // Hibernia
                case MimicEldritch: return Activator.CreateInstance(eldritchSpecs[Util.Random(eldritchSpecs.Length - 1)]) as MimicSpec;
                case MimicEnchanter: return Activator.CreateInstance(enchanterSpecs[Util.Random(enchanterSpecs.Length - 1)]) as MimicSpec;
                case MimicMentalist: return Activator.CreateInstance(mentalistSpecs[Util.Random(mentalistSpecs.Length - 1)]) as MimicSpec;

                // Midgard
                case MimicHealer: return Activator.CreateInstance(healerSpecs[Util.Random(healerSpecs.Length - 1)]) as MimicSpec;

                default: return null;
            }
        }

        #endregion Spec
    }

    #region Equipment

    public static class MimicEquipment
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public static void SetWeaponROG(GameLiving living, eRealm realm, eCharacterClass charClass, byte level, eObjectType objectType, eInventorySlot slot, eDamageType damageType)
        {
            DbItemTemplate itemToCreate = new GeneratedUniqueItem(false, realm, charClass, level, objectType, slot, damageType);

            GameInventoryItem item = GameInventoryItem.Create(itemToCreate);
            living.Inventory.AddItem(slot, item);
        }

        public static void SetArmorROG(GameLiving living, eRealm realm, eCharacterClass charClass, byte level, eObjectType objectType)
        {
            for (int i = Slot.HELM; i <= Slot.ARMS; i++)
            {
                if (i == Slot.JEWELRY || i == Slot.CLOAK)
                    continue;

                eInventorySlot slot = (eInventorySlot)i;
                DbItemTemplate itemToCreate = new GeneratedUniqueItem(false, realm, charClass, level, objectType, slot);

                GameInventoryItem item = GameInventoryItem.Create(itemToCreate);

                living.Inventory.AddItem(slot, item);
            }
        }

        public static void SetJewelryROG(GameLiving living, eRealm realm, eCharacterClass charClass, byte level, eObjectType objectType)
        {
            for (int i = Slot.JEWELRY; i <= Slot.RIGHTRING; i++)
            {
                if (i is Slot.TORSO or Slot.LEGS or Slot.ARMS or Slot.FOREARMS or Slot.SHIELD)
                    continue;

                eInventorySlot slot = (eInventorySlot)i;
                DbItemTemplate itemToCreate = new GeneratedUniqueItem(false, realm, charClass, level, objectType, slot);

                GameInventoryItem item = GameInventoryItem.Create(itemToCreate);

                if (i == Slot.RIGHTRING || i == Slot.LEFTRING)
                    living.Inventory.AddItem(living.Inventory.FindFirstEmptySlot(eInventorySlot.LeftRing, eInventorySlot.RightRing), item);
                else if (i == Slot.LEFTWRIST || i == Slot.RIGHTWRIST)
                    living.Inventory.AddItem(living.Inventory.FindFirstEmptySlot(eInventorySlot.LeftBracer, eInventorySlot.RightBracer), item);
                else
                    living.Inventory.AddItem(slot, item);
            }
        }

        public static void SetInstrumentROG(GameLiving living, eRealm realm, eCharacterClass charClass, byte level, eObjectType objectType, eInventorySlot slot, eInstrumentType instrumentType)
        {
            DbItemTemplate itemToCreate = new GeneratedUniqueItem(false, realm, charClass, level, objectType, slot, instrumentType);

            GameInventoryItem item = GameInventoryItem.Create(itemToCreate);
            if (!living.Inventory.AddItem(slot, item))
               log.Info("Could not add " + item.Name + " to slot " + slot);
            else
               log.Info("Added " + item.Name + " to slot " + slot);
        }

        public static void SetMeleeWeapon(GameLiving living, string weapType, eHand hand, eWeaponDamageType damageType = 0)
        {
            eObjectType objectType = GetObjectType(weapType);

            int min = Math.Max(1, living.Level - 6);
            int max = Math.Min(51, living.Level + 4);

            IList<DbItemTemplate> itemList;

            itemList = GameServer.Database.SelectObjects<DbItemTemplate>(DB.Column("Level").IsGreaterOrEqualTo(min).And(
                                                                       DB.Column("Level").IsLessOrEqualTo(max).And(
                                                                       DB.Column("Object_Type").IsEqualTo((int)objectType).And(
                                                                       DB.Column("Realm").IsEqualTo((int)living.Realm)).And(
                                                                       DB.Column("IsPickable").IsEqualTo(1)))));
            if (itemList.Any())
            {
                // This allows for left handed weapons to be equipped to the right hand. Prevents Twohanded midgard weapons from being selected.
                if (hand == eHand.oneHand)
                {
                    List<DbItemTemplate> oneHandItemsToKeep = new List<DbItemTemplate>();

                    foreach (DbItemTemplate item in itemList)
                    {
                        if (item.Hand != (int)eHand.twoHand)
                            oneHandItemsToKeep.Add(item);
                    }

                    if (oneHandItemsToKeep.Any())
                    {
                        DbItemTemplate oneHandItemTemplate = oneHandItemsToKeep[Util.Random(oneHandItemsToKeep.Count - 1)];
                        AddItem(living, oneHandItemTemplate, hand);

                        return;
                    }
                }

                List<DbItemTemplate> itemsToKeep = new List<DbItemTemplate>();

                foreach (DbItemTemplate item in itemList)
                {
                    // Only used for Armsman and Paladin to ensure twohand weapon matches one handed spec.
                    if (damageType != 0)
                    {
                        if (item.Hand == (int)hand && item.Type_Damage == (int)damageType)
                            itemsToKeep.Add(item);
                    }
                    else if (item.Hand == (int)hand)
                        itemsToKeep.Add(item);

                    if (itemsToKeep.Any())
                    {
                        DbItemTemplate itemTemplate = itemsToKeep[Util.Random(itemsToKeep.Count - 1)];
                        AddItem(living, itemTemplate, hand);

                        return;
                    }
                }
            }
            else
                log.Info("No melee weapon found for " + living.Name);
        }

        public static void SetRangedWeapon(GameLiving living, eObjectType weapType)
        {
            int min = Math.Max(1, living.Level - 6);
            int max = Math.Min(51, living.Level + 3);

            IList<DbItemTemplate> itemList;
            itemList = GameServer.Database.SelectObjects<DbItemTemplate>(DB.Column("Level").IsGreaterOrEqualTo(min).And(
                                                                       DB.Column("Level").IsLessOrEqualTo(max).And(
                                                                       DB.Column("Object_Type").IsEqualTo((int)weapType).And(
                                                                       DB.Column("Realm").IsEqualTo((int)living.Realm)).And(
                                                                       DB.Column("IsPickable").IsEqualTo(1)))));

            if (itemList.Any())
            {
                DbItemTemplate itemTemplate = itemList[Util.Random(itemList.Count - 1)];
                AddItem(living, itemTemplate);

                return;
            }
            else
                log.Info("No Ranged weapon found for " + living.Name);
        }

        public static void SetShield(GameLiving living, int shieldSize)
        {
            int min = Math.Max(1, living.Level - 6);
            int max = Math.Min(51, living.Level + 3);

            IList<DbItemTemplate> itemList;

            itemList = GameServer.Database.SelectObjects<DbItemTemplate>(DB.Column("Level").IsGreaterOrEqualTo(min).And(
                                                                       DB.Column("Level").IsLessOrEqualTo(max).And(
                                                                       DB.Column("Object_Type").IsEqualTo((int)eObjectType.Shield).And(
                                                                       DB.Column("Realm").IsEqualTo((int)living.Realm)).And(
                                                                       DB.Column("Type_Damage").IsEqualTo(shieldSize).And(
                                                                       DB.Column("IsPickable").IsEqualTo(1))))));

            if (itemList.Any())
            {
                DbItemTemplate itemTemplate = itemList[Util.Random(itemList.Count - 1)];
                AddItem(living, itemTemplate);

                return;
            }
            else
                log.Info("No Shield found for " + living.Name);
        }

        public static void SetArmor(GameLiving living, eObjectType armorType)
        {
            int min = Math.Max(1, living.Level - 6);
            int max = Math.Min(51, living.Level + 3);

            IList<DbItemTemplate> itemList;

            List<DbItemTemplate> armsList = new List<DbItemTemplate>();
            List<DbItemTemplate> handsList = new List<DbItemTemplate>();
            List<DbItemTemplate> legsList = new List<DbItemTemplate>();
            List<DbItemTemplate> feetList = new List<DbItemTemplate>();
            List<DbItemTemplate> torsoList = new List<DbItemTemplate>();
            List<DbItemTemplate> helmList = new List<DbItemTemplate>();

            itemList = GameServer.Database.SelectObjects<DbItemTemplate>(DB.Column("Level").IsGreaterOrEqualTo(min).And(
                                                                       DB.Column("Level").IsLessOrEqualTo(max).And(
                                                                       DB.Column("Object_Type").IsEqualTo((int)armorType).And(
                                                                       DB.Column("Realm").IsEqualTo((int)living.Realm)).And(
                                                                       DB.Column("IsPickable").IsEqualTo(1)))));

            if (itemList.Any())
            {
                foreach (DbItemTemplate template in itemList)
                {
                    if (template.Item_Type == Slot.ARMS)
                        armsList.Add(template);
                    else if (template.Item_Type == Slot.HANDS)
                        handsList.Add(template);
                    else if (template.Item_Type == Slot.LEGS)
                        legsList.Add(template);
                    else if (template.Item_Type == Slot.FEET)
                        feetList.Add(template);
                    else if (template.Item_Type == Slot.TORSO)
                        torsoList.Add(template);
                    else if (template.Item_Type == Slot.HELM)
                        helmList.Add(template);
                }

                List<List<DbItemTemplate>> masterList = new List<List<DbItemTemplate>>
                {
                    armsList,
                    handsList,
                    legsList,
                    feetList,
                    torsoList,
                    helmList
                };

                foreach (List<DbItemTemplate> list in masterList)
                {
                    if (list.Any())
                    {
                        DbItemTemplate itemTemplate = list[Util.Random(list.Count - 1)];
                        AddItem(living, itemTemplate);
                    }
                }
            }
            else
                log.Info("No armor found for " + living.Name);
        }

        public static void SetInstrument(GameLiving living, eObjectType weapType, eInventorySlot slot, eInstrumentType instrumentType)
        {
            int min = Math.Max(1, living.Level - 6);
            int max = Math.Min(51, living.Level + 3);

            IList<DbItemTemplate> itemList;
            itemList = GameServer.Database.SelectObjects<DbItemTemplate>(DB.Column("Level").IsGreaterOrEqualTo(min).And(
                                                                       DB.Column("Level").IsLessOrEqualTo(max).And(
                                                                       DB.Column("Object_Type").IsEqualTo((int)weapType).And(
                                                                       DB.Column("DPS_AF").IsEqualTo((int)instrumentType).And(
                                                                       DB.Column("Realm").IsEqualTo((int)living.Realm)).And(
                                                                       DB.Column("IsPickable").IsEqualTo(1))))));

            if (itemList.Any())
            {
                DbItemTemplate itemTemplate = itemList[Util.Random(itemList.Count - 1)];
                DbInventoryItem item = GameInventoryItem.Create(itemTemplate);
                living.Inventory.AddItem(slot, item);

                return;
            }
            else
                log.Info("No instrument found for " + living.Name);
        }

        public static void SetJewelry(GameLiving living)
        {
            int min = Math.Max(1, living.Level - 30);
            int max = Math.Min(51, living.Level + 1);

            IList<DbItemTemplate> itemList;
            List<DbItemTemplate> cloakList = new List<DbItemTemplate>();
            List<DbItemTemplate> jewelryList = new List<DbItemTemplate>();
            List<DbItemTemplate> ringList = new List<DbItemTemplate>();
            List<DbItemTemplate> wristList = new List<DbItemTemplate>();
            List<DbItemTemplate> neckList = new List<DbItemTemplate>();
            List<DbItemTemplate> waistList = new List<DbItemTemplate>();

            itemList = GameServer.Database.SelectObjects<DbItemTemplate>(DB.Column("Level").IsGreaterOrEqualTo(min).And(
                                                                       DB.Column("Level").IsLessOrEqualTo(max).And(
                                                                       DB.Column("Object_Type").IsEqualTo((int)eObjectType.Magical).And(
                                                                       DB.Column("Realm").IsEqualTo((int)living.Realm)).And(
                                                                       DB.Column("IsPickable").IsEqualTo(1)))));
            if (itemList.Any())
            {
                foreach (DbItemTemplate template in itemList)
                {
                    if (template.Item_Type == Slot.CLOAK)
                    {
                        template.Color = Util.Random((Enum.GetValues(typeof(eColor)).Length));
                        cloakList.Add(template);
                    }
                    else if (template.Item_Type == Slot.JEWELRY)
                        jewelryList.Add(template);
                    else if (template.Item_Type == Slot.LEFTRING || template.Item_Type == Slot.RIGHTRING)
                        ringList.Add(template);
                    else if (template.Item_Type == Slot.LEFTWRIST || template.Item_Type == Slot.RIGHTWRIST)
                        wristList.Add(template);
                    else if (template.Item_Type == Slot.NECK)
                        neckList.Add(template);
                    else if (template.Item_Type == Slot.WAIST)
                        waistList.Add(template);
                }

                List<List<DbItemTemplate>> masterList = new List<List<DbItemTemplate>>
                {
                cloakList,
                jewelryList,
                neckList,
                waistList
                };

                foreach (List<DbItemTemplate> list in masterList)
                {
                    if (list.Any())
                    {
                        DbItemTemplate itemTemplate = list[Util.Random(list.Count - 1)];
                        AddItem(living, itemTemplate);
                    }
                }

                // Add two rings and bracelets
                for (int i = 0; i < 2; i++)
                {
                    if (ringList.Any())
                    {
                        DbItemTemplate itemTemplate = ringList[Util.Random(ringList.Count - 1)];
                        AddItem(living, itemTemplate);
                    }

                    if (wristList.Any())
                    {
                        DbItemTemplate itemTemplate = wristList[Util.Random(wristList.Count - 1)];
                        AddItem(living, itemTemplate);
                    }
                }

                // Not sure this is needed what were you thinking past self?
                if (living.Inventory.GetItem(eInventorySlot.Cloak) == null)
                {
                    DbItemTemplate cloak = GameServer.Database.FindObjectByKey<DbItemTemplate>("cloak");
                    cloak.Color = Util.Random((Enum.GetValues(typeof(eColor)).Length));
                    AddItem(living, cloak);
                }
            }
            else
                log.Info("No jewelry of any kind found for " + living.Name);
        }

        private static void AddItem(GameLiving living, DbItemTemplate itemTemplate, eHand hand = eHand.None)
        {
            if (itemTemplate == null)
                log.Info("itemTemplate in AddItem is null");

            DbInventoryItem item = GameInventoryItem.Create(itemTemplate);

            if (item != null)
            {
                if (itemTemplate.Item_Type == Slot.LEFTRING || itemTemplate.Item_Type == Slot.RIGHTRING)
                {
                    living.Inventory.AddItem(living.Inventory.FindFirstEmptySlot(eInventorySlot.LeftRing, eInventorySlot.RightRing), item);
                    return;
                }
                else if (itemTemplate.Item_Type == Slot.LEFTWRIST || itemTemplate.Item_Type == Slot.RIGHTWRIST)
                {
                    living.Inventory.AddItem(living.Inventory.FindFirstEmptySlot(eInventorySlot.LeftBracer, eInventorySlot.RightBracer), item);
                    return;
                }
                else if (itemTemplate.Item_Type == Slot.LEFTHAND && itemTemplate.Object_Type != (int)eObjectType.Shield && hand == eHand.oneHand)
                {
                    living.Inventory.AddItem(eInventorySlot.RightHandWeapon, item);
                    return;
                }
                else
                    living.Inventory.AddItem((eInventorySlot)itemTemplate.Item_Type, item);
            }
            else
                log.Info("Item failed to be created for " + living.Name);
        }

        public static eObjectType GetObjectType(string obj)
        {
            eObjectType objectType = 0;

            switch (obj)
            {
                case "Staff": objectType = eObjectType.Staff; break;

                case "Slash": objectType = eObjectType.SlashingWeapon; break;
                case "Thrust": objectType = eObjectType.ThrustWeapon; break;
                case "Crush": objectType = eObjectType.CrushingWeapon; break;
                case "Flexible": objectType = eObjectType.Flexible; break;
                case "Polearm": objectType = eObjectType.PolearmWeapon; break;
                case "Two Handed": objectType = eObjectType.TwoHandedWeapon; break;

                case "Blades": objectType = eObjectType.Blades; break;
                case "Piercing": objectType = eObjectType.Piercing; break;
                case "Blunt": objectType = eObjectType.Blunt; break;
                case "Large Weapons": objectType = eObjectType.LargeWeapons; break;
                case "Celtic Spear": objectType = eObjectType.CelticSpear; break;
                case "Scythe": objectType = eObjectType.Scythe; break;

                case "Sword": objectType = eObjectType.Sword; break;
                case "Axe": objectType = eObjectType.Axe; break;
                case "Hammer": objectType = eObjectType.Hammer; break;
                case "Hand to Hand": objectType = eObjectType.HandToHand; break;

                case "Cloth": objectType = eObjectType.Cloth; break;
                case "Leather": objectType = eObjectType.Leather; break;
                case "Studded": objectType = eObjectType.Studded; break;
                case "Chain": objectType = eObjectType.Chain; break;
                case "Plate": objectType = eObjectType.Plate; break;

                case "Reinforced": objectType = eObjectType.Reinforced; break;
                case "Scale": objectType = eObjectType.Scale; break;
            }

            return objectType;
        }
    }

    #endregion Equipment

    #region Spec

    public class MimicSpec
    {
        public static string SpecName;
        public string WeaponTypeOne;
        public string WeaponTypeTwo;
        public eWeaponDamageType DamageType = 0;

        public bool is2H;

        public List<SpecLine> SpecLines = new List<SpecLine>();

        public MimicSpec()
        { }

        protected void Add(string name, uint cap, float ratio)
        {
            SpecLines.Add(new SpecLine(name, cap, ratio));
        }
    }

    public struct SpecLine
    {
        public string SpecName;
        public uint SpecCap;
        public float levelRatio;

        public SpecLine(string name, uint cap, float ratio)
        {
            SpecName = name;
            SpecCap = cap;
            levelRatio = ratio;
        }

        public void SetName(string name)
        {
            SpecName = name;
        }
    }

    #endregion Spec

    #region LFG

    public static class MimicLFGManager
    {
        public static List<MimicLFGEntry> LFGListAlb = new List<MimicLFGEntry>();
        public static List<MimicLFGEntry> LFGListHib = new List<MimicLFGEntry>();
        public static List<MimicLFGEntry> LFGListMid = new List<MimicLFGEntry>();

        private static long _respawnTimeAlb = 0;
        private static long _respawnTimeHib = 0;
        private static long _respawnTimeMid = 0;

        private static int minRespawnTime = 60000;
        private static int maxRespawnTime = 600000;

        private static int minRemoveTime = 300000;
        private static int maxRemoveTime = 3600000;

        private static int maxMimics = 20;
        private static int chance = 25;

        public static List<MimicLFGEntry> GetLFG(eRealm realm, byte level)
        {
            switch (realm)
            {
                case eRealm.Albion:
                    {
                        if (_respawnTimeAlb == 0)
                        {
                            _respawnTimeAlb = GameLoop.GameLoopTime + Util.Random(minRespawnTime, maxRespawnTime);
                            LFGListAlb = GenerateList(LFGListAlb, realm, level);
                        }

                        lock (LFGListAlb)
                        {
                            LFGListAlb = ValidateList(LFGListAlb);

                            if (GameLoop.GameLoopTime > _respawnTimeAlb)
                            {
                                LFGListAlb = GenerateList(LFGListAlb, realm, level);
                                _respawnTimeAlb = GameLoop.GameLoopTime + Util.Random(minRespawnTime, maxRespawnTime);
                            }
                        }

                        return LFGListAlb;
                    }

                case eRealm.Hibernia:
                    {
                        if (_respawnTimeHib == 0)
                        {
                            _respawnTimeHib = GameLoop.GameLoopTime + Util.Random(minRespawnTime, maxRespawnTime);
                            LFGListHib = GenerateList(LFGListHib, realm, level);
                        }

                        lock (LFGListHib)
                        {
                            LFGListHib = ValidateList(LFGListHib);

                            if (GameLoop.GameLoopTime > _respawnTimeHib)
                            {
                                LFGListHib = GenerateList(LFGListHib, realm, level);
                                _respawnTimeHib = GameLoop.GameLoopTime + Util.Random(minRespawnTime, maxRespawnTime);
                            }
                        }

                        return LFGListHib;
                    }

                case eRealm.Midgard:
                    {
                        if (_respawnTimeMid == 0)
                        {
                            _respawnTimeMid = GameLoop.GameLoopTime + Util.Random(minRespawnTime, maxRespawnTime);
                            LFGListMid = GenerateList(LFGListMid, realm, level);
                        }

                        lock (LFGListMid)
                        {
                            LFGListMid = ValidateList(LFGListMid);

                            if (GameLoop.GameLoopTime > _respawnTimeMid)
                            {
                                LFGListMid = GenerateList(LFGListMid, realm, level);
                                _respawnTimeMid = GameLoop.GameLoopTime + Util.Random(minRespawnTime, maxRespawnTime);
                            }
                        }

                        return LFGListMid;
                    }
            }

            return null;
        }

        public static void Remove(eRealm realm, MimicLFGEntry entryToRemove)
        {
            switch (realm)
            {
                case eRealm.Albion:
                    if (LFGListAlb.Any())
                    {
                        lock (LFGListAlb)
                        {
                            foreach (MimicLFGEntry entry in LFGListAlb)
                            {
                                if (entry == entryToRemove)
                                {
                                    entry.RemoveTime = GameLoop.GameLoopTime - 1;
                                    break;
                                }
                            }
                        }
                    }
                    break;

                case eRealm.Hibernia:
                    if (LFGListHib.Any())
                    {
                        lock (LFGListHib)
                        {
                            foreach (MimicLFGEntry entry in LFGListHib)
                            {
                                if (entry == entryToRemove)
                                {
                                    entry.RemoveTime = GameLoop.GameLoopTime;
                                    break;
                                }
                            }
                        }
                    }
                    break;

                case eRealm.Midgard:
                    if (LFGListMid.Any())
                    {
                        lock (LFGListMid)
                        {
                            foreach (MimicLFGEntry entry in LFGListMid)
                            {
                                if (entry == entryToRemove)
                                {
                                    entry.RemoveTime = GameLoop.GameLoopTime;
                                    break;
                                }
                            }
                        }
                    }
                    break;
            }
        }

        private static List<MimicLFGEntry> GenerateList(List<MimicLFGEntry> entries, eRealm realm, byte level)
        {
            if (entries.Count < maxMimics)
            {
                int mimicsToAdd = maxMimics - entries.Count;

                for (int i = 0; i < mimicsToAdd; i++)
                {
                    if (Util.Chance(chance))
                    {
                        int levelMin = Math.Max(1, level - 3);
                        int levelMax = Math.Min(50, level + 3);
                        int levelRand = Util.Random(levelMin, levelMax);
                        long removeTime = GameLoop.GameLoopTime + Util.Random(minRemoveTime, maxRemoveTime);

                        MimicLFGEntry entry = new MimicLFGEntry(MimicManager.GetRandomMimicClass(realm), (byte)levelRand, realm, removeTime);

                        entries.Add(entry);
                    }
                }
            }

            List<MimicLFGEntry> generateList = new List<MimicLFGEntry>();
            generateList.AddRange(entries);

            return generateList;
        }

        private static List<MimicLFGEntry> ValidateList(List<MimicLFGEntry> entries)
        {
            List<MimicLFGEntry> validList = new List<MimicLFGEntry>();

            if (entries.Any())
            {
                foreach (MimicLFGEntry entry in entries)
                {
                    if (GameLoop.GameLoopTime < entry.RemoveTime)
                        validList.Add(entry);
                }
            }

            return validList;
        }

        public class MimicLFGEntry
        {
            public string Name;
            public eGender Gender;
            public eMimicClasses MimicClass;
            public byte Level;
            public eRealm Realm;
            public long RemoveTime;
            public bool RefusedGroup;

            public MimicLFGEntry(eMimicClasses mimicClass, byte level, eRealm realm, long removeTime)
            {
                Gender = Util.RandomBool() ? eGender.Male : eGender.Female;
                Name = MimicNames.GetName(Gender, realm);
                MimicClass = mimicClass;
                Level = level;
                Realm = realm;
                RemoveTime = removeTime;
            }
        }
    }

    #endregion LFG

    public class SetupMimicsEvent
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        [ScriptLoadedEvent]
        public static void OnScriptsCompiled(DOLEvent e, object sender, EventArgs args)
        {
            if (MimicManager.Initialize())
                log.Info("MimicNPCs Initialized.");
            else
                log.Error("MimicNPCs Failed to Initialize.");
        }
    }

    // Just a quick way to get names...
    public static class MimicNames
    {
        private const string albMaleNames = "EloraOrozco,KeanuHughes,SamanthaSheppard,TrentLawrence,LaurenKaur,AugustineTyler,HelenaDunn,DawsonHoffman,AspenRobertson,EmilianoWillis,AlexaBarber,SolomonBradford,RheaBond,RogerEdwards,IvyMason,BrandonGolden,GiulianaSaunders,KasenPeck,CrystalWinters,DeandrePetersen,FernandaMills,AlexTownsend,AzaleaMaynard,LandryWilkerson,JaniyahDavidson,DanteCollins,KinsleyMcFarland,DaneWeeks,KarenRamsey,LucianoBailey,KennedyMorse,BodeEspinoza,LucilleHarrington,OmariBarry,WaverlyMack,EstebanMacdonald,RosaliaRomero,BrysonMcDonald,DaisyTodd,BaylorFrost,PaulaMitchell,JaxonUnderwood,EnsleyYu,BryantMason,SiennaMedrano,ArianBeltran,KaydenceHarper,HayesVentura,ZoraSutton,WarrenLozano,CeceliaCaldwell,RylanSalas,AmberBowman,FranciscoMcCullough,HanaHawkins,VictorMcDowell,RaynaStewart,NolanPena,RachelEnriquez,ElishaLi,PaigeSchroeder,IzaiahSilva,LuciaFuentes,BowenSerrano,AllieErickson,JohnnyWard,ArianaPreston,VincenzoHuang,FrancescaCarrillo,WadeLove,AviannaStanley,ManuelAyala,BlairNolan,MaximoLarson,AlaynaPena,MarcusKnight,GracieMurray,AshtonHall,LeahWilliamson,EmersonChavez,NevaehTucker,IvanPowell,VivianCantu,AnakinStephenson,KhaleesiParks,GianniDuke,MelaniCortez,ZaynHayes,IrisPierce,NicolasHensley,MalayaAyers,UlisesZimmerman,AriyahEllison,KyeReeves,LanaCrane,FoxJarvis,ElisabethStafford,AlfredoVargas,AndreaLucas,ChanceGlover";

        private const string albFemaleNames = "AlessiaReilly,AlvaroPonce,AileenHinton,FrankieHampton,LeonaMcCormick,JasiahMcCarthy,KiraMelton,LennonAshley,KhalaniJohns,JoziahCherry,NyomiPruitt,GatlinGeorge,AdelynSchroeder,IzaiahGriffith,AliciaSmall,RudyCarpenter,LillyMaddox,LyricMaldonado,ElainaGibbs,DeaconKeller,LoganDavid,AlonsoSkinner,MaraLawrence,KalebPortillo,NathalieLang,WellsHerman,PaulinaCampos,GideonBates,MadilynDunn,DawsonBartlett,AubrielleSingh,LouisProctor,ChandlerCummings,RaidenYang,AngelinaEscobar,ZachariahNewman,OaklynnBrooks,JordanJohnston,LailaThompson,TheodoreSandoval,ElsieStanley,ManuelMcCarty,HaloWare,TadeoBenitez,AlizaMoreno,MylesKennedy,BriannaPerson,MosesHarrison,JasmineRichardson,RobertValencia,MaddisonLeblanc,BradenLawrence,LaurenAllison,DennisEstes,BrittanyAyers,UlisesSutton,IzabellaVillanueva,HuxleyHoover,VirginiaHunter,ArcherHuerta,DulceSaunders,KasenLeon,AmoraElliott,BlakeGould,VioletaRoberson,ShepherdWalsh,LeiaEnriquez,ElishaVelasquez,EsmeVillarreal,NikolaiWilson,LunaStout,CallahanBest,LexieShah,ZainMitchell,WillowMay,FinleyBerry,AnnabelleStrong,AxlBlevins,AilaReyes,EliHorn,AvahBall,ShaneDixon,BlakelyCuevas,BreckenWagner,MaeveChristian,LedgerEsquivel,JayleeMoran,TateDaniels,EmberThomas,LoganHarmon,MarenCorrea,ZakaiHensley,MalayaRubio,TitanYates,CharleyShields,DevonHouse,SariahOwen,CannonOrtiz,AnnaSchroeder,IzaiahPadilla,MaggieLittle";

        private const string hibMaleNames = "LennoxRandolph,KaileyMarsh,BoHill,HannahGeorge,MarkSanford,EmeraldRosas,RemiNorris,AriellePitts,TreyNorman,MalaniSchmitt,MurphyBlanchard,LayneBarron,DustinJennings,PalmerCalhoun,GaryGood,NathaliaParsons,LewisBell,MelodyShepherd,RonaldReese,RosemaryStephens,MessiahMiddleton,MadalynFisher,GaelMercado,MckinleyRollins,WesCollier,IvoryHopkins,AliTerry,WrenReid,JosueZavala,LivJefferson,RaylanAguirre,AriahMcFarland,DaneArellano,FayeBryant,JonahGrant,AlainaStephenson,JoeBlack,MollyBean,MccoyFelix,PaisleighPeterson,SantiagoBurnett,EmberlyMiranda,RoryLyons,KenzieGoodwin,KaisonCarson,NalaniHarmon,RobertoGraham,AlaiaLugo,SantosTrujillo,DanielleMcMillan,RockyBaldwin,EsmeraldaLogan,RoccoVilla,JohannaBarry,EmeryThompson,MadisonCurry,BriggsShaffer,AlannaWatkins,NashBishop,BrooklynnEvans,EliasKoch,MilanaChoi,KhariLloyd,EmelyWallace,ChaseBennett,JosephineSchneider,RaymondCopeland,DayanaHuffman,ChrisGray,SarahGarza,JudahGuzman,AshleyMichael,BronsonMcLaughlin,StephanieHumphrey,KrewHunter,KhloeValentine,DemetriusHahn,FallonSmith,LiamCarlson,KaliMartinez,AlexanderRobinson,NoraDudley,ColterBartlett,AubrielleGriffith,FranklinJimenez,AdelineZimmerman,SergioErickson,SabrinaZuniga,SincereJaramillo,GuadalupePrince,AronRush,MaleahCruz,RyanFleming,FatimaCurry,BriggsLarsen,XiomaraOrr,BenicioDennis,MaisieHansen,CharlieRivas,AverieLester,LeeWaller";

        private const string hibFemaleNames = "WhitleyParker,CalebMayer,AinhoaSosa,EmirRusso,TinsleyBautista,RaulWoodward,DrewDominguez,KadenHayden,AvayahLandry,JaxxCompton,ElinaUnderwood,ReeceOdom,LaylaniYork,LeandroBryant,ParkerHartman,BakerSingh,VivienneDunlap,AriesO’Neal,TreasureQuintana,KelvinEnriquez,NellieCain,BensonMcCoy,MckenzieMoyer,AhmirCochran,AlmaRomero,BrysonTorres,VioletHanson,KhalilMorse,KairiStephenson,JoeLarson,AlaynaBurnett,DavisHicks,AlinaArellano,KellanCortez,HavenPortillo,WallaceDennis,MaisieHardin,HassanPark,LiaFerguson,MiguelBeltran,KaydenceContreras,EmilioTapia,MichaelaOrtega,KobePrince,GretaWeaver,TuckerHarrell,KaraJames,JaxsonCampos,SuttonParra,DavionWagner,MaeveMurphy,CameronKerr,BayleeAbbott,KohenAguirre,AriahLam,BodieCastillo,EvaEstes,HakeemSuarez,JimenaVillarreal,NikolaiHurst,AdaleeDecker,TaylorHarrington,LegacyYu,BryantMunoz,KehlaniAvalos,CoenNorman,MalaniBonilla,AdenMyers,LydiaHanna,AydinArellano,FayeWise,FrederickGrimes,BraelynGallegos,JonasPace,GianaSullivan,EvanLevy,FloraHopkins,AliHickman,ScarletteSimpson,ElliottCook,AaliyahKent,MekhiArnold,FinleyCastaneda,CollinLucero,IlaBrowning,RohanLynn,SamiraHumphrey,KrewMorton,MalloryLi,JorgeParker,AubreyMcLaughlin,IbrahimMalone,SkylerMaddox,LyricSaunders,MeadowDay,KaysonMaldonado,ElainaBass,LandenWilson,LunaMurillo,LanceStafford,BridgetJenkins";

        private const string midMaleNames = "DeclanMcMillan,OakleighMitchell,JaxonBranch,LuisaLam,BodiePollard,MarisolSummers,DariusLawson,PhoebeBaker,EzraOrr,AlaiyaCopeland,AxtonXiong,AmayahHartman,BakerHamilton,MackenzieBall,ShaneMcCarthy,KiraRandall,TrentonKnox,KallieSchmidt,ZaydenSloan,SeleneCastro,JasperBecker,LauraLeon,MarshallLarson,AlaynaSchmitt,MurphyCampos,SuttonGallagher,MarcosHines,PoppyMyers,AdamWheeler,SydneyFloyd,PierceGuzman,AshleyGutierrez,LucaPetersen,FernandaCampos,GideonTruong,JudithCamacho,TatumGuerrero,MargotYoung,AsherWeaver,TeaganSchultz,CodyWilliamson,CatherineDonovan,BrayanRowland,HarleighErickson,JohnnyHendricks,DaniJaramillo,RiggsStewart,MayaCrosby,TristenFox,JulietteAllen,CarterOwen,MikaylaKing,JulianWade,EvieAli,ArjunSawyer,MarinaVillanueva,HuxleyHorton,AitanaCampos,GideonBlake,AmandaPowell,BennettCook,AaliyahRoman,KianBarron,AnyaFlowers,SaulHenderson,MariaBenton,JamalSellers,MercyJaramillo,RiggsQuintero,KeylaMueller,AlbertWagner,MaeveAcosta,JensenVang,MadisynSingh,LouisDejesus,JulissaAcevedo,DakariMcGee,KayleighGood,DavianSandoval,ElsieMontoya,FordJenkins,RyleeMcDaniel,MajorMorrison,RebeccaMcIntosh,KristianMerritt,KaisleyReid,JosueSalazar,FreyaWolfe,DonovanWilkins,AmaliaSchmidt,ZaydenFrye,RayaLyons,CyrusBrowning,PrincessO’Neill,MarcelVillarreal,JazlynBowen,TrevorHouston,LylahBuchanan,EnriqueBartlett,AubrielleGlenn,ZaidCoffey";

        private const string midFemaleNames = "PaolaLim,CalGillespie,AliannaAcosta,JensenChang,OpheliaSummers,DariusStout,ChanaLu,DuncanBecker,LauraLeal,CedricSanders,EverleighHouse,YehudaOrr,AlaiyaAcevedo,DakariPugh,LandryChen,EmmanuelPittman,MarieRuiz,AustinPage,CataleyaJefferson,RaylanMcLaughlin,StephanieFigueroa,SpencerCamacho,ArmaniHinton,FrankieYork,MilanFranco,GagePhan,ElsaBernal,EithanHancock,KatelynBeltran,RickyRivers,KianaArias,AlecCochran,AlmaSalinas,EdgarMiller,IsabellaFletcher,JayStanton,JayceeAdams,HudsonGlenn,BlairePineda,GerardoEsquivel,JayleeLe,DamienRichmond,WhitneyVo,GordonBaldwin,EsmeraldaAndersen,AlistairGlass,ClareGibson,TylerJimenez,AdelineQuintana,KelvinProctor,ChandlerWilkins,YusufArmstrong,PresleyKoch,SalvadorConley,SalemHuber,MacHuynh,OakleeSparks,DrakeKline,SevynFowler,KameronMonroe,CarlyAyala,TannerHarrison,JasmineMalone,RubenMiller,IsabellaWilcox,JerryHuber,RaquelBeil,ArielDonovan,AzariahSparks,DrakeBallard,AlejandraMcCarthy,DevinHolmes,BaileyFord,LuisO’brien,JoannaEllison,KyeMontgomery,EvangelineFinley,CalumSexton,EllenMolina,PrinceBanks,CaliHarris,SamuelWard,ArianaRobinson,MatthewMassey,ClementineWatts,DakotaDavid,HayleeBenitez,JusticeWright,LilyMays,JadielHarris,PenelopeTorres,JaydenSutton,IzabellaFrank,BraylenHopkins,GabrielaFuller,AndreSanchez,AriaArnold,AbrahamJuarez,JulietFrye,FrancoHurst,AdaleeRobbins";
        public static string GetName(eGender gender, eRealm realm)
        {
            string[] names = new string[0];

            switch (realm)
            {
                case eRealm.Albion:
                    if (gender == eGender.Male)
                        names = albMaleNames.Split(',');
                    else
                        names = albFemaleNames.Split(',');
                    break;

                case eRealm.Hibernia:
                    if (gender == eGender.Male)
                        names = hibMaleNames.Split(',');
                    else
                        names = hibFemaleNames.Split(",");
                    break;

                case eRealm.Midgard:
                    if (gender == eGender.Male)
                        names = midMaleNames.Split(',');
                    else
                        names = midFemaleNames.Split(",");
                    break;
            }

            int randomIndex = Util.Random(names.Length - 1);

            return names[randomIndex];
        }
    }
}