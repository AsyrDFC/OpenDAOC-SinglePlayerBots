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

using DOL.GS.Effects;
using DOL.GS.Scripts;

namespace DOL.GS.Spells
{
	/// <summary>
	/// Base class for spells with immunity like mez/root/stun/nearsight
	/// </summary>
	public abstract class ImmunityEffectSpellHandler : SpellHandler
	{
		/// <summary>
		/// called when spell effect has to be started and applied to targets
		/// </summary>
		public override void FinishSpellCast(GameLiving target)
		{
			m_caster.Mana -= PowerCost(target);
			base.FinishSpellCast(target);
		}

		/// <summary>
		/// Determines wether this spell is better than given one
		/// </summary>
		/// <param name="oldeffect"></param>
		/// <param name="neweffect"></param>
		/// <returns>true if this spell is better version than compare spell</returns>
		public override bool IsNewEffectBetter(GameSpellEffect oldeffect, GameSpellEffect neweffect)
		{
			if (oldeffect.Owner is GamePlayer) return false; //no overwrite for players
			return base.IsNewEffectBetter(oldeffect, neweffect);
		}

		public override void ApplyEffectOnTarget(GameLiving target)
		{
			if (target == null || target.CurrentRegion == null)
				return;

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

			base.ApplyEffectOnTarget(target);
			target.StartInterruptTimer(target.SpellInterruptDuration, AttackData.eAttackType.Spell, Caster);
		}

		/// <summary>
		/// Calculates the effect duration in milliseconds
		/// </summary>
		/// <param name="target">The effect target</param>
		/// <param name="effectiveness">The effect effectiveness</param>
		/// <returns>The effect duration in milliseconds</returns>
		protected override int CalculateEffectDuration(GameLiving target, double effectiveness)
		{
			// http://support.darkageofcamelot.com/kb/article.php?id=423
			// Patch Notes: Version 1.52
			// The duration is 100% at the middle of the area, and it tails off to 50%
			// duration at the edges. This does NOT change the way area effect spells
			// work against monsters, only realm enemies (i.e. enemy players and enemy realm guards).
			double duration = base.CalculateEffectDuration(target, effectiveness);

			if (!(target is GamePlayer || target is MimicNPC) && !(target is Keeps.GameKeepGuard))
				return (int)duration;

			duration *= (0.5 + 0.5*effectiveness);
			duration -= duration * target.GetResistBase(Spell.DamageType) * 0.01;

			if (duration < 1)
				duration = 1;
			else if (duration > (Spell.Duration * 4))
				duration = (Spell.Duration * 4);

			return
				(int)duration;
		}

		/// <summary>
		/// Creates the corresponding spell effect for the spell
		/// </summary>
		/// <param name="target"></param>
		/// <param name="effectiveness"></param>
		/// <returns></returns>
		protected override GameSpellEffect CreateSpellEffect(GameLiving target, double effectiveness)
		{
			return new GameSpellAndImmunityEffect(this, CalculateEffectDuration(target, effectiveness), 0, effectiveness);
		}

		/// <summary>
		/// constructor
		/// </summary>
		/// <param name="caster">The spell caster</param>
		/// <param name="spell">The spell being cast</param>
		/// <param name="spellLine">The spell's spellline</param>
		public ImmunityEffectSpellHandler(GameLiving caster, Spell spell, SpellLine spellLine) : base(caster, spell, spellLine) {}
	}
}
