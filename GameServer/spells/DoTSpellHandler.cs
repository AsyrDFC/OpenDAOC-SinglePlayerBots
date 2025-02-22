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
using DOL.AI.Brain;
using DOL.GS.Effects;
using DOL.GS.PacketHandler;
using DOL.Language;

namespace DOL.GS.Spells
{
	/// <summary>
	/// Damage Over Time spell handler
	/// </summary>
	[SpellHandlerAttribute("DamageOverTime")]
	public class DoTSpellHandler : SpellHandler
	{
		public int CriticalDamage { get; protected set; } = 0;
		private bool firstTick = true;

		public override void CreateECSEffect(ECSGameEffectInitParams initParams)
		{
			new DamageOverTimeECSGameEffect(initParams);
		}
		/// <summary>
		/// Execute damage over time spell
		/// </summary>
		/// <param name="target"></param>
		public override void FinishSpellCast(GameLiving target)
		{
			m_caster.Mana -= PowerCost(target);
			base.FinishSpellCast(target);
		}

		public override double GetLevelModFactor()
		{
			return 0;
		}

		protected override double CalculateDistanceFallOff(int distance, int radius)
		{
			return 0;
		}

		/// <summary>
		/// Determines wether this spell is compatible with given spell
		/// and therefore overwritable by better versions
		/// spells that are overwritable cannot stack
		/// </summary>
		/// <param name="compare"></param>
		/// <returns></returns>
		public override bool IsOverwritable(ECSGameSpellEffect compare)
		{
			return Spell.SpellType == compare.SpellHandler.Spell.SpellType && Spell.DamageType == compare.SpellHandler.Spell.DamageType && SpellLine.IsBaseLine == compare.SpellHandler.SpellLine.IsBaseLine;
		}

		public override AttackData CalculateDamageToTarget(GameLiving target)
		{
			AttackData ad = base.CalculateDamageToTarget(target);
            if (this.SpellLine.KeyName == GlobalSpellsLines.Mundane_Poisons)
            {
                RealmAbilities.L3RAPropertyEnhancer ra = Caster.GetAbility<RealmAbilities.ViperAbility>();
				if (ra != null)
				{
					int additional = (int)((float)ad.Damage * ((float)ra.Amount / 100));
					ad.Damage += additional;
				}
            }

            //dots can only crit through Wild Arcana RA, which is handled elsewhere
            //if (ad.CriticalDamage > 0) ad.CriticalDamage = 0;
            
            
	            //GameSpellEffect iWarLordEffect = SpellHandler.FindEffectOnTarget(target, "CleansingAura");
			//if (iWarLordEffect != null)
			//	ad.Damage *= (int)(1.00 - (iWarLordEffect.Spell.Value * 0.01));
			
			return ad;
		}

		/// <summary>
		/// Calculates min damage variance %
		/// </summary>
		/// <param name="target">spell target</param>
		/// <param name="min">returns min variance</param>
		/// <param name="max">returns max variance</param>
		public override void CalculateDamageVariance(GameLiving target, out double min, out double max)
		{
			int speclevel = 1;
			min = 1;
			max = 1;

			if (m_caster is GamePlayer)
			{
				if (m_spellLine.KeyName == GlobalSpellsLines.Mundane_Poisons)
				{
					speclevel = ((GamePlayer)m_caster).GetModifiedSpecLevel(Specs.Envenom);
					min = 1;
					max = 1;

					if (target.Level > 0)
					{
						min = 0.25 + (speclevel - 1) / (double)target.Level;
					}
				}

				if (m_spellLine.KeyName == GlobalSpellsLines.Item_Effects)
				{
					min = .75;
					max = 1;
				}
				else
				{
					speclevel = ((GamePlayer)m_caster).GetModifiedSpecLevel(m_spellLine.Spec);

					if (target.Level > 0)
					{
						min = 0.25 + (speclevel - 1) / (double)target.Level;
					}
				}
			}

			// no overspec bonus for dots

			if (min > max) min = max;
			if (min < 0) min = 0;
		}

		/// <summary>
		/// Sends damage text messages but makes no damage
		/// </summary>
		/// <param name="ad"></param>
		public override void SendDamageMessages(AttackData ad)
		{
			// Graveen: only GamePlayer should receive messages :p
			GamePlayer PlayerReceivingMessages = null;
			if (m_caster is GamePlayer)
				PlayerReceivingMessages = m_caster as GamePlayer;
            if ( m_caster is GameSummonedPet)
                if ((m_caster as GameSummonedPet).Brain is IControlledBrain)
                    PlayerReceivingMessages = ((m_caster as GameSummonedPet).Brain as IControlledBrain).GetPlayerOwner();
            if (PlayerReceivingMessages == null) 
                return;
				
            if (Spell.Name.StartsWith("Proc"))
            {
                MessageToCaster(String.Format(LanguageMgr.GetTranslation(PlayerReceivingMessages.Client, "DoTSpellHandler.SendDamageMessages.YouHitFor",
                    ad.Target.GetName(0, false), ad.Damage)), eChatType.CT_YouHit);
            }
            else
            {
                MessageToCaster(String.Format(LanguageMgr.GetTranslation(PlayerReceivingMessages.Client, "DoTSpellHandler.SendDamageMessages.YourHitsFor",
                    Spell.Name, ad.Target.GetName(0, false), ad.Damage)), eChatType.CT_YouHit);
            }
            //if (ad.CriticalDamage > 0)
            //    MessageToCaster(String.Format(LanguageMgr.GetTranslation(PlayerReceivingMessages.Client, "DoTSpellHandler.SendDamageMessages.YourCriticallyHits",
            //        Spell.Name, ad.Target.GetName(0, false), ad.CriticalDamage)) + " (" + (ad.Attacker.SpellCriticalChance - 10) + "%)", eChatType.CT_YouHit);

			if (this.CriticalDamage > 0)
				MessageToCaster("You critically hit for an additional " + this.CriticalDamage + " damage!" + " (" + m_caster.DotCriticalChance + "%)", eChatType.CT_YouHit);

			//			if (ad.Damage > 0)
			//			{
			//				string modmessage = "";
			//				if (ad.Modifier > 0) modmessage = " (+"+ad.Modifier+")";
			//				if (ad.Modifier < 0) modmessage = " ("+ad.Modifier+")";
			//				MessageToCaster("You hit "+ad.Target.GetName(0, false)+" for " + ad.Damage + " damage!", eChatType.CT_Spell);
			//			}
			//			else
			//			{
			//				MessageToCaster("You hit "+ad.Target.GetName(0, false)+" for " + ad.Damage + " damage!", eChatType.CT_Spell);
			//				MessageToCaster(ad.Target.GetName(0, true) + " resists the effect!", eChatType.CT_SpellResisted);
			//				MessageToLiving(ad.Target, "You resist the effect!", eChatType.CT_SpellResisted);
			//			}
		}

		public override void ApplyEffectOnTarget(GameLiving target)
		{
			//((compare.SpellHandler is DoTSpellHandler dot) && Spell.Damage + this.CriticalDamage < compare.Spell.Damage + dot.CriticalDamage)
			// var dots = target.effectListComponent.GetSpellEffects(eEffect.DamageOverTime)
			// 									 .Where(x => x.SpellHandler?.Spell != null)
			// 									 .Select(x => x.SpellHandler)
			// 									 .Where(x => x.Spell.SpellType == Spell.SpellType &&
			// 												 x.Spell.DamageType == Spell.DamageType &&
			// 												 x.SpellLine.IsBaseLine == SpellLine.IsBaseLine);

			// foreach (var dotEffect in dots)
            // {
			// 	var dotHandler = (dotEffect as DoTSpellHandler);

			// 	if (dotHandler == null)
			// 		continue;

			// 	// Check for Overwriting.
			// 	if (dotEffect.Spell.Damage + dotHandler.CriticalDamage >= Spell.Damage + CriticalDamage)
			// 	{
			// 		// Old Spell is Better than new one

			// 		//apply first hit, then quit
			// 		OnDirectEffect(target, effectiveness);
					
			// 		this.MessageToCaster(eChatType.CT_SpellResisted, "{0} already has that effect.", target.GetName(0, true));
			// 		MessageToCaster("Wait until it expires. Spell Failed.", eChatType.CT_SpellResisted);
			// 		// Prevent Adding.
			// 		return;
			// 	}
			// }

			base.ApplyEffectOnTarget(target);
			target.StartInterruptTimer(target.SpellInterruptDuration, AttackData.eAttackType.Spell, Caster);
		}

		protected override GameSpellEffect CreateSpellEffect(GameLiving target, double effectiveness)
		{
			// damage is not reduced with distance
            return new GameSpellEffect(this, m_spell.Duration, m_spell.Frequency, effectiveness);
		}

		public override void OnEffectStart(GameSpellEffect effect)
		{
			SendEffectAnimation(effect.Owner, 0, false, 1);
		}

		public override void OnEffectPulse(GameSpellEffect effect)
		{
			base.OnEffectPulse(effect);

			if (effect.Owner.IsAlive)
			{
				// An acidic cloud surrounds you!
				MessageToLiving(effect.Owner, Spell.Message1, eChatType.CT_Spell);
				// {0} is surrounded by an acidic cloud!
				Message.SystemToArea(effect.Owner, Util.MakeSentence(Spell.Message2, effect.Owner.GetName(0, false)), eChatType.CT_YouHit, effect.Owner);
				OnDirectEffect(effect.Owner);
			}
		}

		/// <summary>
		/// When an applied effect expires.
		/// Duration spells only.
		/// </summary>
		/// <param name="effect">The expired effect</param>
		/// <param name="noMessages">true, when no messages should be sent to player and surrounding</param>
		/// <returns>immunity duration in milliseconds</returns>
		public override int OnEffectExpires(GameSpellEffect effect, bool noMessages)
		{
			base.OnEffectExpires(effect, noMessages);
			if (!noMessages)
			{
				// The acidic mist around you dissipates.
				MessageToLiving(effect.Owner, Spell.Message3, eChatType.CT_SpellExpires);
				// The acidic mist around {0} dissipates.
				Message.SystemToArea(effect.Owner, Util.MakeSentence(Spell.Message4, effect.Owner.GetName(0, false)), eChatType.CT_SpellExpires, effect.Owner);
			}
			return 0;
		}

		public override void OnDirectEffect(GameLiving target)
		{
			if (target == null) return;
			if (!target.IsAlive || target.ObjectState != GameLiving.eObjectState.Active) return;

			// no interrupts on DoT direct effect
			// calc damage
			AttackData ad = CalculateDamageToTarget(target);

			ad.CriticalDamage = CalculateCriticalDamage(ad);

			//ad.CausesCombat = true;
			SendDamageMessages(ad);
			if (ad.Attacker.Realm == 0)
			{
				ad.Target.LastAttackTickPvE = GameLoop.GameLoopTime;
			}
			else
			{
				ad.Target.LastAttackTickPvP = GameLoop.GameLoopTime;
			}
			DamageTarget(ad, false);

			if (firstTick) firstTick = false;
		}

		public void OnDirectEffect(GameLiving target, double effectiveness, bool causesCombat)
		{
			if (target == null) return;
			if (!target.IsAlive || target.ObjectState != GameLiving.eObjectState.Active) return;

			// no interrupts on DoT direct effect
			// calc damage
			AttackData ad = CalculateDamageToTarget(target);
			ad.CausesCombat = causesCombat;
			SendDamageMessages(ad);
			DamageTarget(ad, false);
		}

		public override double CalculateDamageBase(GameLiving target)
		{
			double spellDamage = Spell.Damage;
			GamePlayer player = null;
			if (m_caster is GamePlayer)
				player = m_caster as GamePlayer;

			if (m_spellLine.KeyName != GlobalSpellsLines.Mundane_Poisons && m_spellLine.KeyName != GlobalSpellsLines.Item_Effects && m_spellLine.KeyName != GlobalSpellsLines.Item_Spells)
			{
				if (player != null && player.CharacterClass.ManaStat != eStat.UNDEFINED)
				{
					int manaStatValue = player.GetModified((eProperty)player.CharacterClass.ManaStat);
					spellDamage *= (manaStatValue + 200) / 275.0;
					if (spellDamage < 0)
						spellDamage = 0;
				}
				else if (m_caster is GameNPC)
				{
					int manaStatValue = m_caster.GetModified(eProperty.Intelligence);
					spellDamage *= (manaStatValue + 200) / 275.0;
					if (spellDamage < 0)
						spellDamage = 0;
				}
			}
			return spellDamage;
		}

		// constructor
		public DoTSpellHandler(GameLiving caster, Spell spell, SpellLine line) : base(caster, spell, line) { }
		private int CalculateCriticalDamage(AttackData ad)
        {
			if (CriticalDamage > 0 || !firstTick)
				return CriticalDamage;

			int criticalChance = Caster.DotCriticalChance;

			if (criticalChance < 0)
				return 0;

			int randNum = Util.CryptoNextInt(0, 100);
			int critCap = Math.Min(50, criticalChance);

			if (Caster is GamePlayer spellCaster && spellCaster.UseDetailedCombatLog && critCap > 0)
				spellCaster.Out.SendMessage($"dot crit chance: {critCap} random: {randNum}", eChatType.CT_DamageAdd, eChatLoc.CL_SystemWindow);

			if (critCap > randNum && (ad.Damage >= 1))
			{
				int critmax = (ad.Target is GamePlayer) ? ad.Damage / 2 : ad.Damage;
				CriticalDamage = Util.Random(ad.Damage / 10, critmax); //tThink min crit is 10% of damage
			}

			return CriticalDamage;
		}
	}
}
