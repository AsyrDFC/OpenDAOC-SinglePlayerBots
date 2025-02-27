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
using DOL.GS.PacketHandler;
using DOL.Database;

namespace DOL.GS.ServerRules
{
	/// <summary>
	/// Set of rules for "PvE" server type.
	/// </summary>
	[ServerRules(EGameServerType.GST_PvE)]
	public class PvEServerRules : AbstractServerRules
	{
		public override string RulesDescription()
		{
			return "standard PvE server rules";
		}

		public override bool IsAllowedToAttack(GameLiving attacker, GameLiving defender, bool quiet)
		{
			if (!base.IsAllowedToAttack(attacker, defender, quiet))
				return false;

			// if controlled NPC - do checks for owner instead
			if (attacker is GameNPC)
			{
				IControlledBrain controlled = ((GameNPC)attacker).Brain as IControlledBrain;
				if (controlled != null)
				{
					attacker = controlled.GetPlayerOwner();
					quiet = true; // silence all attacks by controlled npc
				}
			}
			if (defender is GameNPC)
			{
				IControlledBrain controlled = ((GameNPC)defender).Brain as IControlledBrain;
				if (controlled != null)
					defender = controlled.GetPlayerOwner();
			}

			//"You can't attack yourself!"
			if(attacker == defender)
			{
				if (quiet == false) MessageToLiving(attacker, "You can't attack yourself!");
				return false;
			}

			// Pet release might cause one of these to be null
			if (attacker == null || defender == null)
				return false;

			if (attacker.Realm != eRealm.None && defender.Realm != eRealm.None)
			{
				if (attacker is GamePlayer && ((GamePlayer)attacker).DuelTarget == defender)
					return true;
				if (quiet == false) MessageToLiving(attacker, "You can not attack other players on this server!");
				return false;
			}

			//allow attacks on same realm only under the following circumstances
			if (attacker.Realm == defender.Realm)
			{
				//allow confused mobs to attack same realm
				if (attacker is GameNPC && (attacker as GameNPC).IsConfused)
					return true;

				// else, don't allow mobs to attack mobs
				if (attacker.Realm == eRealm.None)
				{
					return FactionMgr.CanLivingAttack(attacker, defender);
				}

				if (quiet == false) MessageToLiving(attacker, "You can't attack a member of your realm!");
				return false;
			}

			return true;
		}

		public override bool IsSameRealm(GameLiving source, GameLiving target, bool quiet)
		{
			if(source == null || target == null) 
				return false;

			// if controlled NPC - do checks for owner instead
			if (source is GameNPC)
			{
				IControlledBrain controlled = ((GameNPC)source).Brain as IControlledBrain;
				if (controlled != null)
				{
					source = controlled.GetPlayerOwner();
					quiet = true; // silence all attacks by controlled npc
				}
			}
			if (target is GameNPC)
			{
				IControlledBrain controlled = ((GameNPC)target).Brain as IControlledBrain;
				if (controlled != null)
					target = controlled.GetPlayerOwner();
			}

			if (source == target)
				return true;

			// clients with priv level > 1 are considered friendly by anyone
			if(target is GamePlayer && ((GamePlayer)target).Client.Account.PrivLevel > 1) return true;

			// mobs can heal mobs, players heal players/NPC
			if(source.Realm == 0 && target.Realm == 0) return true;
			if(source.Realm != 0 && target.Realm != 0) return true;

			//Peace flag NPCs are same realm
			if (target is GameNPC)
				if ((((GameNPC)target).Flags & GameNPC.eFlags.PEACE) != 0)
					return true;

			if (source is GameNPC)
				if ((((GameNPC)source).Flags & GameNPC.eFlags.PEACE) != 0)
					return true;

			if(quiet == false) MessageToLiving(source, target.GetName(0, true) + " is not a member of your realm!");
			return false;
		}

		/// <summary>
        /// Is player allowed to make the item
		/// </summary>
		/// <param name="player"></param>
		/// <param name="item"></param>
		/// <returns></returns>
		public override bool IsAllowedToCraft(GamePlayer player, DbItemTemplate item)
		{
			return player.Realm == (eRealm)item.Realm || (item.Realm == 0 && ServerProperties.Properties.ALLOW_CRAFT_NOREALM_ITEMS);
		}

		public override bool IsAllowedCharsInAllRealms(GameClient client)
		{
			return true;
		}

		public override bool IsAllowedToGroup(GamePlayer source, GamePlayer target, bool quiet)
		{			
			return true;
		}

		public override bool IsAllowedToJoinGuild(GamePlayer source, Guild guild)
		{
			return true;
		}

		public override bool IsAllowedToTrade(GameLiving source, GameLiving target, bool quiet)
		{
			return true;
		}

		public override bool IsAllowedToUnderstand(GameLiving source, GamePlayer target)
		{
			return true;
		}

		/// <summary>
		/// Gets the server type color handling scheme
		/// 
		/// ColorHandling: this byte tells the client how to handle color for PC and NPC names (over the head) 
		/// 0: standard way, other realm PC appear red, our realm NPC appear light green 
		/// 1: standard PvP way, all PC appear red, all NPC appear with their level color 
		/// 2: Same realm livings are friendly, other realm livings are enemy; nearest friend/enemy buttons work
		/// 3: standard PvE way, all PC friendly, realm 0 NPC enemy rest NPC appear light green 
		/// 4: All NPC are enemy, all players are friendly; nearest friend button selects self, nearest enemy don't work at all
		/// </summary>
		/// <param name="client">The client asking for color handling</param>
		/// <returns>The color handling</returns>
		public override byte GetColorHandling(GameClient client)
		{
			return 3;
		}

		/// <summary>
		/// Rules For Player Level UP
		/// Gives some realm points above level 20
		/// </summary>
		/// <param name="player"></param>
		/// <param name="previousLevel"></param>
		public override void OnPlayerLevelUp(GamePlayer player, int previousLevel)
		{
		}
		
		/// <summary>
		/// Gets the player's Total Amount of Realm Points Based on Level, Realm Level of other constraints.
		/// </summary>
		/// <param name="source">The player</param>
		/// <param name="target"></param>
		/// <returns>The total pool of realm points !</returns>
		public override int GetPlayerRealmPointsTotal(GamePlayer source)
		{
			return source.Level > 19 ? source.RealmLevel + (source.Level-19) : source.RealmLevel;
		}
	}
}
