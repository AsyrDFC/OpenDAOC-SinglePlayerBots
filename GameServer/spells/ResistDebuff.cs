/*
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

using System;
using System.Collections.Generic;
using DOL.AI.Brain;
using DOL.GS.PacketHandler;
using DOL.Language;

namespace DOL.GS.Spells
{
	/// <summary>
	/// Base class for all resist debuffs, needed to set effectiveness and duration
	/// </summary>
	public abstract class AbstractResistDebuff : PropertyChangingSpell
	{
		/// <summary>
		/// Gets debuff type name for delve info
		/// </summary>
		public abstract string DebuffTypeName { get; }

        public override void CreateECSEffect(ECSGameEffectInitParams initParams)
        {
			new StatDebuffECSEffect(initParams);
        }

        /// <summary>
        /// Debuff category is 3 for debuffs
        /// </summary>
        public override eBuffBonusCategory BonusCategory1 { get { return eBuffBonusCategory.Debuff; } }

		/// <summary>
		/// Calculates the effect duration in milliseconds
		/// </summary>
		/// <param name="target">The effect target</param>
		/// <param name="effectiveness">The effect effectiveness</param>
		/// <returns>The effect duration in milliseconds</returns>
		protected override int CalculateEffectDuration(GameLiving target, double effectiveness)
		{
			double duration = Spell.Duration;
			
			duration *= (1.0 + m_caster.GetModified(eProperty.SpellDuration) * 0.01);		
			duration -= duration * target.GetResist(m_spell.DamageType) * 0.01;
			
			if (duration < 1)
				duration = 1;
			else if (duration > (Spell.Duration * 4))
				duration = (Spell.Duration * 4);
			return (int)duration;
		}

		public override void ApplyEffectOnTarget(GameLiving target)
		{
			//check for existing effect
			// var debuffs = target.effectListComponent.GetSpellEffects()
			// 					.Where(x => x.SpellHandler is AbstractResistDebuff);

			// foreach (var debuff in debuffs)
			// {
			// 	var debuffSpell = debuff.SpellHandler as AbstractResistDebuff;

			// 	if (debuffSpell.Property1 == this.Property1 && debuffSpell.Spell.Value >= Spell.Value)
			// 	{
			// 		// Old Spell is Better than new one
			// 		SendSpellResistAnimation(target);
			// 		this.MessageToCaster(eChatType.CT_SpellResisted, "{0} already has that effect.", target.GetName(0, true));
			// 		MessageToCaster("Wait until it expires. Spell Failed.", eChatType.CT_SpellResisted);
			// 		// Prevent Adding.
			// 		return;
			// 	}
			// }


			//TODO: correct effectiveness formula
			// invoke direct effect if not resisted for DD w/ debuff spells
			if (Caster is GamePlayer && Spell.Level > 0)
            {
                if (((GamePlayer)Caster).CharacterClass.ClassType == eClassType.ListCaster)
				{
					int specLevel = Caster.GetModifiedSpecLevel(m_spellLine.Spec);
					Effectiveness = 0.75;
                    Effectiveness += (specLevel - 1.0) * 0.5 / Spell.Level;
                    Effectiveness = Math.Max(0.75, Effectiveness);
                    Effectiveness = Math.Min(1.25, Effectiveness);
                    Effectiveness *= (1.0 + m_caster.GetModified(eProperty.BuffEffectiveness) * 0.01);
                }
				else
				{
					Effectiveness = 1.0; 
					Effectiveness *= (1.0 + m_caster.GetModified(eProperty.DebuffEffectivness) * 0.01);
				}

				Effectiveness *= GetCritBonus();
			}
            
			
			base.ApplyEffectOnTarget(target);

			if (target.Realm == 0 || Caster.Realm == 0)
			{
				target.LastAttackedByEnemyTickPvE = GameLoop.GameLoopTime;
				Caster.LastAttackTickPvE = GameLoop.GameLoopTime;
			}
			else
			{
				target.LastAttackedByEnemyTickPvP = GameLoop.GameLoopTime;
				Caster.LastAttackTickPvP = GameLoop.GameLoopTime;
			}
			if(target is GameNPC)
			{
				IOldAggressiveBrain aggroBrain = ((GameNPC)target).Brain as IOldAggressiveBrain;
				if (aggroBrain != null)
					aggroBrain.AddToAggroList(Caster, 1);
			}
			if(Spell.CastTime>0) target.StartInterruptTimer(target.SpellInterruptDuration, AttackData.eAttackType.Spell, Caster);
		}

        private double GetCritBonus()
        {
            double critMod = 1.0;
            int critChance = Caster.DotCriticalChance;

            if (critChance <= 0)
                return critMod;

            GamePlayer playerCaster = Caster as GamePlayer;

            if (playerCaster?.UseDetailedCombatLog == true && critChance > 0)
                playerCaster.Out.SendMessage($"Debuff crit chance: {Caster.DotCriticalChance}", eChatType.CT_DamageAdd, eChatLoc.CL_SystemWindow);

            if (Util.Chance(critChance))
            {                    
                critMod *= 1 + Util.Random(1, 10) * 0.1;
                playerCaster?.Out.SendMessage($"Your {Spell.Name} critically debuffs the enemy for {Math.Round(critMod - 1,3) * 100}% additional effect!", eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
            }

            return critMod;
        }

		/// <summary>
		/// Calculates chance of spell getting resisted
		/// </summary>
		/// <param name="target">the target of the spell</param>
		/// <returns>chance that spell will be resisted for specific target</returns>
		public override int CalculateSpellResistChance(GameLiving target)
		{
			int basechance = base.CalculateSpellResistChance(target);
			/*
			GameSpellEffect rampage = SpellHandler.FindEffectOnTarget(target, "Rampage");
			if (rampage != null)
			{
				basechance += (int)rampage.Spell.Value;
			}*/
			return Math.Min(100, basechance);
		}
		/// <summary>
		/// Updates changes properties to living
		/// </summary>
		/// <param name="target"></param>
		protected override void SendUpdates(GameLiving target)
		{
			base.SendUpdates(target);
			if (target is GamePlayer)
			{
				GamePlayer player = (GamePlayer)target;
				player.Out.SendCharResistsUpdate();
			}
		}

		/// <summary>
		/// Delve Info
		/// </summary>
		public override IList<string> DelveInfo
		{
			get
			{
				/*
				<Begin Info: Nullify Dissipation>
				Function: resistance decrease
 
				Decreases the target's resistance to the listed damage type.
 
				Resist decrease Energy: 15
				Target: Targetted
				Range: 1500
				Duration: 15 sec
				Power cost: 13
				Casting time:      2.0 sec
				Damage: Cold
 
				<End Info>
				 */

				var list = new List<string>();
				list.Add(LanguageMgr.GetTranslation((Caster as GamePlayer).Client, "ResistDebuff.DelveInfo.Function"));
				list.Add(" "); //empty line
				list.Add(Spell.Description);
				list.Add(" "); //empty line
				list.Add(String.Format(LanguageMgr.GetTranslation((Caster as GamePlayer).Client, "ResistDebuff.DelveInfo.Decrease", DebuffTypeName, m_spell.Value)));
				list.Add(LanguageMgr.GetTranslation((Caster as GamePlayer).Client, "DelveInfo.Target", Spell.Target));
				if (Spell.Range != 0)
					list.Add(LanguageMgr.GetTranslation((Caster as GamePlayer).Client, "DelveInfo.Range", Spell.Range));
				if (Spell.Duration >= ushort.MaxValue * 1000)
					list.Add(LanguageMgr.GetTranslation((Caster as GamePlayer).Client, "DelveInfo.Duration") + " Permanent.");
				else if (Spell.Duration > 60000)
					list.Add(string.Format(LanguageMgr.GetTranslation((Caster as GamePlayer).Client, "DelveInfo.Duration") + Spell.Duration / 60000 + ":" + (Spell.Duration % 60000 / 1000).ToString("00") + " min"));
				else if (Spell.Duration != 0)
					list.Add(LanguageMgr.GetTranslation((Caster as GamePlayer).Client, "DelveInfo.Duration") + (Spell.Duration / 1000).ToString("0' sec';'Permanent.';'Permanent.'"));
				if (Spell.Power != 0)
					list.Add(LanguageMgr.GetTranslation((Caster as GamePlayer).Client, "DelveInfo.PowerCost", Spell.Power.ToString("0;0'%'")));
				list.Add(LanguageMgr.GetTranslation((Caster as GamePlayer).Client, "DelveInfo.CastingTime", (Spell.CastTime * 0.001).ToString("0.0## sec;-0.0## sec;'instant'")));
				if (Spell.RecastDelay > 60000)
					list.Add(LanguageMgr.GetTranslation((Caster as GamePlayer).Client, "DelveInfo.RecastTime") + Spell.RecastDelay / 60000 + ":" + (Spell.RecastDelay % 60000 / 1000).ToString("00") + " min");
				else if (Spell.RecastDelay > 0)
					list.Add(LanguageMgr.GetTranslation((Caster as GamePlayer).Client, "DelveInfo.RecastTime") + (Spell.RecastDelay / 1000).ToString() + " sec");
				if (Spell.Concentration != 0)
					list.Add(LanguageMgr.GetTranslation((Caster as GamePlayer).Client, "DelveInfo.ConcentrationCost", Spell.Concentration));
				if (Spell.Radius != 0)
					list.Add(LanguageMgr.GetTranslation((Caster as GamePlayer).Client, "DelveInfo.Radius", Spell.Radius));
				if (Spell.DamageType != eDamageType.Natural)
					list.Add(LanguageMgr.GetTranslation((Caster as GamePlayer).Client, "DelveInfo.Damage", GlobalConstants.DamageTypeToName(Spell.DamageType)));

				return list;
			}
		}

		//constructor
		public AbstractResistDebuff(GameLiving caster, Spell spell, SpellLine line) : base(caster, spell, line) {}
	}

	/// <summary>
	/// Body resistance debuff
	/// </summary>
	[SpellHandlerAttribute("BodyResistDebuff")]
	public class BodyResistDebuff : AbstractResistDebuff
	{
		public override eProperty Property1 { get { return eProperty.Resist_Body; } }
		public override string DebuffTypeName { get { return "Body"; } }

		// constructor
		public BodyResistDebuff(GameLiving caster, Spell spell, SpellLine line) : base(caster, spell, line) {}
	}

	/// <summary>
	/// Cold resistance debuff
	/// </summary>
	[SpellHandlerAttribute("ColdResistDebuff")]
	public class ColdResistDebuff : AbstractResistDebuff
	{
		public override eProperty Property1 { get { return eProperty.Resist_Cold; } }
		public override string DebuffTypeName { get { return "Cold"; } }

		// constructor
		public ColdResistDebuff(GameLiving caster, Spell spell, SpellLine line) : base(caster, spell, line) {}
	}

	/// <summary>
	/// Energy resistance debuff
	/// </summary>
	[SpellHandlerAttribute("EnergyResistDebuff")]
	public class EnergyResistDebuff : AbstractResistDebuff
	{
		public override eProperty Property1 { get { return eProperty.Resist_Energy; } }
		public override string DebuffTypeName { get { return "Energy"; } }

		// constructor
		public EnergyResistDebuff(GameLiving caster, Spell spell, SpellLine line) : base(caster, spell, line) {}
	}

	/// <summary>
	/// Heat resistance debuff
	/// </summary>
	[SpellHandlerAttribute("HeatResistDebuff")]
	public class HeatResistDebuff : AbstractResistDebuff
	{
		public override eProperty Property1 { get { return eProperty.Resist_Heat; } }
		public override string DebuffTypeName { get { return "Heat"; } }

		// constructor
		public HeatResistDebuff(GameLiving caster, Spell spell, SpellLine line) : base(caster, spell, line) {}
	}

	/// <summary>
	/// Matter resistance debuff
	/// </summary>
	[SpellHandlerAttribute("MatterResistDebuff")]
	public class MatterResistDebuff : AbstractResistDebuff
	{
		public override eProperty Property1 { get { return eProperty.Resist_Matter; } }
		public override string DebuffTypeName { get { return "Matter"; } }

		// constructor
		public MatterResistDebuff(GameLiving caster, Spell spell, SpellLine line) : base(caster, spell, line) {}
	}

	/// <summary>
	/// Spirit resistance debuff
	/// </summary>
	[SpellHandlerAttribute("SpiritResistDebuff")]
	public class SpiritResistDebuff : AbstractResistDebuff
	{
		public override eProperty Property1 { get { return eProperty.Resist_Spirit; } }
		public override string DebuffTypeName { get { return "Spirit"; } }

		// constructor
		public SpiritResistDebuff(GameLiving caster, Spell spell, SpellLine line) : base(caster, spell, line) {}
	}

	/// <summary>
	/// Slash resistance debuff
	/// </summary>
	[SpellHandlerAttribute("SlashResistDebuff")]
	public class SlashResistDebuff : AbstractResistDebuff
	{
		public override eProperty Property1 { get { return eProperty.Resist_Slash; } }
		public override string DebuffTypeName { get { return "Slash"; } }

		// constructor
		public SlashResistDebuff(GameLiving caster, Spell spell, SpellLine line) : base(caster, spell, line) {}
	}

	/// <summary>
	/// Thrust resistance debuff
	/// </summary>
	[SpellHandlerAttribute("ThrustResistDebuff")]
	public class ThrustResistDebuff : AbstractResistDebuff
	{
		public override eProperty Property1 { get { return eProperty.Resist_Thrust; } }
		public override string DebuffTypeName { get { return "Thrust"; } }

		// constructor
		public ThrustResistDebuff(GameLiving caster, Spell spell, SpellLine line) : base(caster, spell, line) {}
	}

	/// <summary>
	/// Crush resistance debuff
	/// </summary>
	[SpellHandlerAttribute("CrushResistDebuff")]
	public class CrushResistDebuff : AbstractResistDebuff
	{
		public override eProperty Property1 { get { return eProperty.Resist_Crush; } }
		public override string DebuffTypeName { get { return "Crush"; } }

		// constructor
		public CrushResistDebuff(GameLiving caster, Spell spell, SpellLine line) : base(caster, spell, line) {}
	}
	/// <summary>
	/// Crush/Slash/Thrust resistance debuff
	/// </summary>
	[SpellHandlerAttribute("CrushSlashThrustDebuff")]
	public class CrushSlashThrustDebuff : AbstractResistDebuff
	{
		public override eBuffBonusCategory BonusCategory1 { get { return eBuffBonusCategory.Debuff; } }
		public override eBuffBonusCategory BonusCategory2 { get { return eBuffBonusCategory.Debuff; } }
		public override eBuffBonusCategory BonusCategory3 { get { return eBuffBonusCategory.Debuff; } }
		
		public override eProperty Property1 { get { return eProperty.Resist_Crush; } }
		public override eProperty Property2 { get { return eProperty.Resist_Slash; } }
		public override eProperty Property3 { get { return eProperty.Resist_Thrust; } }

		public override string DebuffTypeName { get { return "Crush/Slash/Thrust"; } }

		// constructor
		public CrushSlashThrustDebuff(GameLiving caster, Spell spell, SpellLine line) : base(caster, spell, line) {}
	}
	
	[SpellHandlerAttribute("EssenceSear")]
	public class EssenceResistDebuff : AbstractResistDebuff
	{
		public override eProperty Property1 { get { return eProperty.Resist_Natural; } }
		public override string DebuffTypeName { get { return "Essence"; } }

		// constructor
		public EssenceResistDebuff(GameLiving caster, Spell spell, SpellLine line) : base(caster, spell, line) {}
	}
}
