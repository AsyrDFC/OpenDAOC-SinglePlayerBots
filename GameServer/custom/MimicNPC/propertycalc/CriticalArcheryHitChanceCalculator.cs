using System;
using DOL.AI.Brain;
using DOL.GS.PropertyCalc;

namespace DOL.GS.Scripts
{
	/// <summary>
	/// The critical hit chance calculator. Returns 0 .. 100 chance.
	/// 
	/// BuffBonusCategory1 unused
	/// BuffBonusCategory2 unused
	/// BuffBonusCategory3 unused
	/// BuffBonusCategory4 for uncapped realm ability bonus
	/// BuffBonusMultCategory1 unused
	/// </summary>
	[PropertyCalculator(eProperty.CriticalArcheryHitChance)]
	public class CriticalArcheryHitChanceCalculator : PropertyCalculator
	{
		public CriticalArcheryHitChanceCalculator() {}

		public override int CalcValue(GameLiving living, eProperty property) 
		{
			int chance = living.BuffBonusCategory4[(int)property] + living.AbilityBonus[(int)property];

			//Volley effect apply crit chance during volley effect
			ECSGameEffect volley = EffectListService.GetEffectOnTarget(living, eEffect.Volley);
			if (living is GamePlayer || living is MimicNPC && volley != null)
			{
				chance += 10;

				if (living.GetAbility<RealmAbilities.AtlasOF_FalconsEye>() is RealmAbilities.AtlasOF_FalconsEye falcon_eye)
					chance += falcon_eye.Amount;
			}

			if (living is GameSummonedPet gamePet)
			{
				if (ServerProperties.Properties.EXPAND_WILD_MINION && gamePet.Brain is IControlledBrain playerBrain
					&& playerBrain.GetPlayerOwner() is GamePlayer player
					&& player.GetAbility<RealmAbilities.AtlasOF_WildMinionAbility>() is RealmAbilities.AtlasOF_WildMinionAbility ab)
					chance += ab.Amount;
			}
			else // not a pet
				chance += 10;

			return chance;
		}
	}
}
