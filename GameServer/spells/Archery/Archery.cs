using System;
using System.Collections.Generic;
using System.Linq;
using DOL.AI.Brain;
using DOL.Events;
using DOL.GS.Effects;
using DOL.GS.PacketHandler;

namespace DOL.GS.Spells
{
	[SpellHandler("Archery")]
	public class Archery : ArrowSpellHandler
	{
		public enum eShotType
		{
			Other = 0,
			Critical = 1,
			Power = 2,
			PointBlank = 3,
			Rapid = 4
		}

		/// <summary>
		/// Does this spell break stealth on start?
		/// </summary>
		public override bool UnstealthCasterOnStart
		{
			get { return false; }
		}

		public override bool CheckBeginCast(GameLiving selectedTarget)
		{
			if (m_caster.ObjectState != GameLiving.eObjectState.Active)	return false;
			if (!m_caster.IsAlive)
			{
				MessageToCaster("You are dead and can't cast!", eChatType.CT_System);
				return false;
			}
			
			// Is PS ?
			GameSpellEffect Phaseshift = SpellHandler.FindEffectOnTarget(Caster, "Phaseshift");
			if (Phaseshift != null && (Spell.InstrumentRequirement == 0 || Spell.SpellType == eSpellType.Mesmerize))
			{
				MessageToCaster("You're phaseshifted and can't cast a spell", eChatType.CT_System);
				return false;
			}

			// Is Shield Disarm ?
			ShieldTripDisarmEffect shieldDisarm = Caster.EffectList.GetOfType<ShieldTripDisarmEffect>();
			if (shieldDisarm != null)
			{
				MessageToCaster("You're disarmed and can't cast a spell", eChatType.CT_System);
				return false;
			}

			// Is Mentalist RA5L ?
			SelectiveBlindnessEffect SelectiveBlindness = Caster.EffectList.GetOfType<SelectiveBlindnessEffect>();
			if (SelectiveBlindness != null)
			{
				GameLiving EffectOwner = SelectiveBlindness.EffectSource;
				if(EffectOwner==selectedTarget)
				{
					if (m_caster is GamePlayer)
						((GamePlayer)m_caster).Out.SendMessage(string.Format("{0} is invisible to you!", selectedTarget.GetName(0, true)), eChatType.CT_Missed, eChatLoc.CL_SystemWindow);
					
					return false;
				}
			}
			
			// Is immune ?
			if (selectedTarget!=null&&selectedTarget.HasAbility("DamageImmunity"))
			{
				MessageToCaster(selectedTarget.Name + " is immune to this effect!", eChatType.CT_SpellResisted);
				return false;
			}
			
			if (m_caster.IsSitting)
			{
				MessageToCaster("You can't cast while sitting!", eChatType.CT_SpellResisted);
				return false;
			}
			if (m_spell.RecastDelay > 0)
			{
				int left = m_caster.GetSkillDisabledDuration(m_spell);
				if (left > 0)
				{
					MessageToCaster("You must wait " + (left / 1000 + 1).ToString() + " seconds to use this spell!", eChatType.CT_System);
					return false;
                }
            }

			switch (m_spell.Target)
			{
				case eSpellTarget.AREA:
				{
					if (!m_caster.IsWithinRadius(m_caster.GroundTarget, CalculateSpellRange()))
					{
						MessageToCaster("Your area target is out of range.  Select a closer target.", eChatType.CT_SpellResisted);
						return false;
					}

					break;
				}
				case eSpellTarget.ENEMY:
				{
					if (m_caster.IsObjectInFront(selectedTarget, 180) == false)
					{
						MessageToCaster("Your target is not in view!", eChatType.CT_SpellResisted);
						Caster.Notify(GameLivingEvent.CastFailed, new CastFailedEventArgs(this, CastFailedEventArgs.Reasons.TargetNotInView));
						return false;
					}

					if (m_caster.TargetInView == false)
					{
						MessageToCaster("Your target is not visible!", eChatType.CT_SpellResisted);
						Caster.Notify(GameLivingEvent.CastFailed, new CastFailedEventArgs(this, CastFailedEventArgs.Reasons.TargetNotInView));
						return false;
					}

					break;
				}
			}

			if (Caster != null && Caster is GamePlayer && Caster.ActiveWeapon != null && GlobalConstants.IsBowWeapon((eObjectType)Caster.ActiveWeapon.Object_Type))
			{
				if (Spell.LifeDrainReturn == (int)eShotType.Critical && (!(Caster.IsStealthed)))
				{
					MessageToCaster("You must be stealthed and wielding a bow to use this ability!", eChatType.CT_SpellResisted);
					return false;
				}

				return true;
			}
			else
			{
				if (Spell.LifeDrainReturn == (int)eShotType.Critical)
				{
					MessageToCaster("You must be stealthed and wielding a bow to use this ability!", eChatType.CT_SpellResisted);
					return false;
				}

				MessageToCaster("You must be wielding a bow to use this ability!", eChatType.CT_SpellResisted);
				return false;
			}
		}
		
		public override void SendSpellMessages()
		{
			MessageToCaster("You prepare a " + Spell.Name, eChatType.CT_YouHit);
		}


		public override int CalculateToHitChance(GameLiving target)
		{
			int bonustohit = Caster.GetModified(eProperty.ToHitBonus);

			// miss rate is 0 on same level opponent
			int hitchance = 100 + bonustohit;

			if ((Caster is GamePlayer && target is GamePlayer) == false)
			{
				hitchance -= (int)(Caster.GetConLevel(target) * ServerProperties.Properties.PVE_SPELL_CONHITPERCENT);
				hitchance += Math.Max(0, target.attackComponent.Attackers.Count - 1) * ServerProperties.Properties.MISSRATE_REDUCTION_PER_ATTACKERS;
			}

			return hitchance;
		}


		/// <summary>
		/// Adjust damage based on chance to hit.
		/// </summary>
		/// <param name="damage"></param>
		/// <param name="hitChance"></param>
		/// <returns></returns>
		public override int AdjustDamageForHitChance(int damage, int hitChance)
		{
			int adjustedDamage = damage;

			if (hitChance < 85)
			{
				adjustedDamage += (int)(adjustedDamage * (hitChance - 85) * 0.038);
			}

			return adjustedDamage;
		}


		/// <summary>
		/// Level mod for effect between target and caster if there is any
		/// </summary>
		/// <returns></returns>
		public override double GetLevelModFactor()
		{
			return 0.025;
		}


		public override AttackData CalculateDamageToTarget(GameLiving target)
		{
			AttackData ad = base.CalculateDamageToTarget(target);
			GamePlayer player;
			//GameSpellEffect bladeturn = FindEffectOnTarget(target, "Bladeturn");
            target.effectListComponent.Effects.TryGetValue(eEffect.Bladeturn, out var bladeturn);
			if (bladeturn != null)
			{
				switch (Spell.LifeDrainReturn)
				{
					case (int)eShotType.Critical:
						{
							if (target is GamePlayer)
							{
								player = target as GamePlayer;
								player.Out.SendMessage("A shot penetrated your magic barrier!", eChatType.CT_SpellResisted, eChatLoc.CL_SystemWindow);
							}
							ad.AttackResult = eAttackResult.HitUnstyled;
						}
						break;

					case (int)eShotType.Power:
						{
							player = target as GamePlayer;
							player.Out.SendMessage("A shot penetrated your magic barrier!", eChatType.CT_SpellResisted, eChatLoc.CL_SystemWindow);
							ad.AttackResult = eAttackResult.HitUnstyled;
                            EffectService.RequestImmediateCancelEffect(bladeturn.FirstOrDefault());
                        }
                        break;

					case (int)eShotType.Other:
					default:
						{
							if (Caster is GamePlayer)
							{
								player = Caster as GamePlayer;
								player.Out.SendMessage("Your strike was absorbed by a magical barrier!", eChatType.CT_SpellResisted, eChatLoc.CL_SystemWindow);
							}
							if (target is GamePlayer)
							{
								player = target as GamePlayer;
								player.Out.SendMessage("The blow was absorbed by a magical barrier!", eChatType.CT_SpellResisted, eChatLoc.CL_SystemWindow);
								ad.AttackResult = eAttackResult.Missed;
								EffectService.RequestImmediateCancelEffect(bladeturn.FirstOrDefault());
							}
						}
						break;
				}
			}

			if (ad.AttackResult != eAttackResult.Missed)
			{
				GameNPC npc = target as GameNPC;
				if (npc != null)
				{
					if (npc.Brain != null && (npc.Brain is IControlledBrain) == false)
					{
						// boost for npc damage until we find exactly where calculation is going wrong -tolakram
						ad.Damage = (int)(ad.Damage * 1.57);
					}
				}

				// Volley damage reduction based on live testing - tolakram
				if (Spell.Target == eSpellTarget.AREA)
				{
					ad.Damage = (int)(ad.Damage * 0.815);
				}
			}

			return ad;
		}

		/// <summary>
		/// Determines what damage type to use.  For archery the player can choose.
		/// </summary>
		/// <returns></returns>
		public override eDamageType DetermineSpellDamageType()
		{
			GameSpellEffect ef = FindEffectOnTarget(Caster, "ArrowDamageTypes");
			if (ef != null)
			{
				return ef.SpellHandler.Spell.DamageType;
			}
			else
			{
				return eDamageType.Slash;
			}
		}

		/// <summary>
		/// Calculates the base 100% spell damage which is then modified by damage variance factors
		/// </summary>
		/// <returns></returns>
		public override double CalculateDamageBase(GameLiving target)
		{
			double spellDamage = Spell.Damage;
			GamePlayer player = Caster as GamePlayer;

			if (player != null)
			{
				int manaStatValue = player.GetModified((eProperty)player.CharacterClass.ManaStat);
				spellDamage *= (manaStatValue + 300) / 275.0;
			}

			if (spellDamage < 0)
				spellDamage = 0;

			return spellDamage;
		}

		public override void FinishSpellCast(GameLiving target)
		{
			if (target == null && Spell.Target != eSpellTarget.AREA)
				return;

			if (Caster == null)
				return;

			if (Caster is GamePlayer playerCaster && Caster.IsStealthed)
				playerCaster.Stealth(false);

			if (Spell.Target == eSpellTarget.AREA)
			{
				// always put archer into combat when using area (volley)
				Caster.LastAttackTickPvE = GameLoop.GameLoopTime;
				Caster.LastAttackTickPvP = GameLoop.GameLoopTime;

				foreach (GameLiving npc in WorldMgr.GetNPCsCloseToSpot(Caster.CurrentRegionID, Caster.GroundTarget.X, Caster.GroundTarget.Y, Caster.GroundTarget.Z, (ushort)Spell.Radius))
				{
					if (npc.Realm == 0 || Caster.Realm == 0)
					{
						npc.LastAttackedByEnemyTickPvE = GameLoop.GameLoopTime;
					}
				}
			}
			else
			{
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
			}

			base.FinishSpellCast(target);
		}

		/// <summary>
		/// Calculates the effective casting time
		/// </summary>
		/// <returns>effective casting time in milliseconds</returns>
		public override int CalculateCastingTime()
		{
			if (Spell.LifeDrainReturn == (int)eShotType.Power) return 6000;

			int ticks = m_spell.CastTime;

			double percent = 1.0;
			int dex = Caster.GetModified(eProperty.Dexterity);

			if (dex < 60)
			{
				//do nothing.
			}
			else if (dex < 250)
			{
				percent = 1.0 - (dex - 60) * 0.15 * 0.01;
			}
			else
			{
				percent = 1.0 - ((dex - 60) * 0.15 + (dex - 250) * 0.05) * 0.01;
			}

			GamePlayer player = m_caster as GamePlayer;

			if (player != null)
			{
				percent *= 1.0 - m_caster.GetModified(eProperty.CastingSpeed) * 0.01;
			}

			ticks = (int)(ticks * Math.Max(m_caster.CastingSpeedReductionCap, percent));

			if (ticks < m_caster.MinimumCastingSpeed)
				ticks = m_caster.MinimumCastingSpeed;

			return ticks;
		}

		public override int PowerCost(GameLiving target) { return 0; }

		public override int CalculateEnduranceCost()
		{
			#region [Freya] Nidel: Arcane Syphon chance
			int syphon = Caster.GetModified(eProperty.ArcaneSyphon);
			if (syphon > 0)
			{
				if(Util.Chance(syphon))
				{
					return 0;
				}
			}
			#endregion
			return (int)(Caster.MaxEndurance * (Spell.Power * .01));
		}
		
		public override bool CasterIsAttacked(GameLiving attacker)
		{
			if (Spell.Uninterruptible)
				return false;

			if (IsInCastingPhase && Stage < 2)
			{
				double mod = Caster.GetConLevel(attacker);
				double chance = 65;
				chance += mod * 10;
				chance = Math.Max(1, chance);
				chance = Math.Min(99, chance);
				if (attacker is GamePlayer) chance = 100;
				if (Util.Chance((int)chance))
				{
					Caster.TempProperties.SetProperty(INTERRUPT_TIMEOUT_PROPERTY, GameLoop.GameLoopTime + Caster.SpellInterruptDuration);
					MessageToLiving(Caster, attacker.GetName(0, true) + " attacks you and your shot is interrupted!", eChatType.CT_SpellResisted);
					InterruptCasting();
					return true;
				}
			}
			return true;
		}
		
		public override IList<string> DelveInfo
		{
			get
			{
				var list = new List<string>();
				//list.Add("Function: " + (Spell.SpellType == "" ? "(not implemented)" : Spell.SpellType));
				//list.Add(" "); //empty line
				list.Add(Spell.Description);
				list.Add(" "); //empty line
				if (Spell.InstrumentRequirement != 0)
					list.Add("Instrument require: " + GlobalConstants.InstrumentTypeToName(Spell.InstrumentRequirement));
				if (Spell.Damage != 0)
					list.Add("Damage: " + Spell.Damage.ToString("0.###;0.###'%'"));
				else if (Spell.Value != 0)
					list.Add("Value: " + Spell.Value.ToString("0.###;0.###'%'"));
				list.Add("Target: " + Spell.Target);
				if (Spell.Range != 0)
					list.Add("Range: " + Spell.Range);
				if (Spell.Duration >= ushort.MaxValue * 1000)
					list.Add("Duration: Permanent.");
				else if (Spell.Duration > 60000)
					list.Add(string.Format("Duration: {0}:{1} min", Spell.Duration / 60000, (Spell.Duration % 60000 / 1000).ToString("00")));
				else if (Spell.Duration != 0)
					list.Add("Duration: " + (Spell.Duration / 1000).ToString("0' sec';'Permanent.';'Permanent.'"));
				if (Spell.Frequency != 0)
					list.Add("Frequency: " + (Spell.Frequency * 0.001).ToString("0.0"));
				if (Spell.Power != 0)
					list.Add("Endurance cost: " + Spell.Power.ToString("0;0'%'"));
				list.Add("Casting time: " + (Spell.CastTime * 0.001).ToString("0.0## sec;-0.0## sec;'instant'"));
				if (Spell.RecastDelay > 60000)
					list.Add("Recast time: " + (Spell.RecastDelay / 60000).ToString() + ":" + (Spell.RecastDelay % 60000 / 1000).ToString("00") + " min");
				else if (Spell.RecastDelay > 0)
					list.Add("Recast time: " + (Spell.RecastDelay / 1000).ToString() + " sec");
				if (Spell.Radius != 0)
					list.Add("Radius: " + Spell.Radius);
				if (Spell.DamageType != eDamageType.Natural)
					list.Add("Damage: " + GlobalConstants.DamageTypeToName(Spell.DamageType));
				return list;
			}
		}

		/// <summary>
		/// Do not trigger Subspells
		/// </summary>
		/// <param name="target"></param>
		public override void CastSubSpells(GameLiving target)
		{
		}
		
		public Archery(GameLiving caster, Spell spell, SpellLine line) : base(caster, spell, line) { }
	}
}
