﻿/*
 * DAWN OF LIGHT - The first free open source DAoC server emulator
 *
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU General Public License
 * as published by the Free Software Foundation; either version 2
 * of the License, or (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program; if not, write to the Free Software
 * Foundation, Inc., 59 Temple Place - Suite 330, Boston, MA  02111-1307, USA.
 *
 */

using DOL.GS.PropertyCalc;

namespace DOL.GS.Scripts
{
    /// <summary>
    /// The Skill Level calculator
    ///
    /// BuffBonusCategory1 is used for buffs, uncapped
    /// BuffBonusCategory2 unused
    /// BuffBonusCategory3 unused
    /// BuffBonusCategory4 unused
    /// BuffBonusMultCategory1 unused
    /// </summary>
    [PropertyCalculator(eProperty.Skill_First, eProperty.Skill_Last)]
    public class SkillLevelCalculator : PropertyCalculator
    {
        public SkillLevelCalculator()
        { }

        public override int CalcValue(GameLiving living, eProperty property)
        {
            //			DOLConsole.WriteSystem("calc skill prop "+property+":");
            if (living is GamePlayer)
            {
                GamePlayer player = (GamePlayer)living;

                int itemCap = player.Level / 5 + 1;

                int itemBonus = player.ItemBonus[(int)property];

                if (SkillBase.CheckPropertyType(property, ePropertyType.SkillMeleeWeapon))
                    itemBonus += player.ItemBonus[(int)eProperty.AllMeleeWeaponSkills];
                if (SkillBase.CheckPropertyType(property, ePropertyType.SkillMagical))
                    itemBonus += player.ItemBonus[(int)eProperty.AllMagicSkills];
                if (SkillBase.CheckPropertyType(property, ePropertyType.SkillDualWield))
                    itemBonus += player.ItemBonus[(int)eProperty.AllDualWieldingSkills];
                if (SkillBase.CheckPropertyType(property, ePropertyType.SkillArchery))
                    itemBonus += player.ItemBonus[(int)eProperty.AllArcherySkills];

                itemBonus += player.ItemBonus[(int)eProperty.AllSkills];

                if (itemBonus > itemCap)
                    itemBonus = itemCap;
                int buffs = player.BaseBuffBonusCategory[(int)property]; // one buff category just in case..

                //log.Info("PLAYER item bonus="+itemBonus+"; buffs=" +buffs+"; realm=" + player.RealmLevel/10);
                return itemBonus + buffs + player.RealmLevel / 10;
            }
            else if (living is MimicNPC)
            {
                MimicNPC mimic = (MimicNPC)living;

                int itemCap = mimic.Level / 5 + 1;

                int itemBonus = mimic.ItemBonus[(int)property];

                if (SkillBase.CheckPropertyType(property, ePropertyType.SkillMeleeWeapon))
                    itemBonus += mimic.ItemBonus[(int)eProperty.AllMeleeWeaponSkills];
                if (SkillBase.CheckPropertyType(property, ePropertyType.SkillMagical))
                    itemBonus += mimic.ItemBonus[(int)eProperty.AllMagicSkills];
                if (SkillBase.CheckPropertyType(property, ePropertyType.SkillDualWield))
                    itemBonus += mimic.ItemBonus[(int)eProperty.AllDualWieldingSkills];
                if (SkillBase.CheckPropertyType(property, ePropertyType.SkillArchery))
                    itemBonus += mimic.ItemBonus[(int)eProperty.AllArcherySkills];

                itemBonus += mimic.ItemBonus[(int)eProperty.AllSkills];

                if (itemBonus > itemCap)
                    itemBonus = itemCap;
                int buffs = mimic.BaseBuffBonusCategory[(int)property]; // one buff category just in case..

                //log.Info("MIMIC item bonus="+itemBonus+"; buffs="+buffs+"; realm="+mimic.RealmLevel/10);
                return itemBonus + buffs + mimic.RealmLevel / 10;
            }
            else
            {
                // TODO other living types
            }
            return 0;
        }
    }
}