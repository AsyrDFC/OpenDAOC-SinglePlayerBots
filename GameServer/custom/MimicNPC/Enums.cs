﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DOL.GS.Scripts
{
    public enum eHand
    {
        None = -1,

        oneHand = 0,
        twoHand = 1,
        leftHand = 2
    }

    public enum ePositional
    {
        None = -1,

        Back = 0,
        Side,
        Front
    }

    public enum eQueueMessage
    {
        None = -1,

        WaitForBuffs = 0,

        LowPower,
        OutOfPower,

        LowEndurance,
        OutOfEndurance
    }

    public enum eQueueMessageResult
    {
        None = -1,

        Accept = 0,
        Deny,
        OnHold
    }

    public enum eMimicGroupRole
    {
        None = -1,

        Leader = 0,
        MainAssist,
        MainTank,
        MainCC
    }

    public enum eMimicClasses
    {
        None = -1,

        Armsman = 0,
        Cabalist,
        Cleric,
        Friar,
        Infiltrator,
        Mercenary,
        Minstrel,
        Paladin,
        Reaver,
        Scout,
        Sorcerer,
        Theurgist,
        Wizard,

        Bard,
        Blademaster,
        Champion,
        Druid,
        Eldritch,
        Enchanter,
        Hero,
        Mentalist,
        Nightshade,
        Ranger,
        Valewalker,
        Warden,

        Berserker,
        Bonedancer,
        Healer,
        Hunter,
        Runemaster,
        Savage,
        Shadowblade,
        Shaman,
        Skald,
        Spiritmaster,
        Thane,
        Warrior
    }

    public enum eMimicClassesHib
    {
        None = -1,

        Bard = 0,
        Blademaster,
        Champion,
        Druid,
        Eldritch,
        Enchanter,
        Hero,
        Mentalist,
        Nightshade,
        Ranger,
        Valewalker,
        Warden,
    }

    public enum eMimicClassesMid
    {
        None = -1,

        Berserker = 0,
        Bonedancer,
        Healer,
        Hunter,
        Runemaster,
        Savage,
        Shadowblade,
        Shaman,
        Skald,
        Spiritmaster,
        Thane,
        Warrior
    }
}
