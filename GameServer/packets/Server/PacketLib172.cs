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
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DOL.Database;
using log4net;

namespace DOL.GS.PacketHandler
{
	[PacketLib(172, GameClient.eClientVersion.Version172)]
	public class PacketLib172 : PacketLib171
	{
		/// <summary>
		/// Defines a logger for this class.
		/// </summary>
		private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

		/// <summary>
		/// Constructs a new PacketLib for Version 1.72 clients
		/// </summary>
		/// <param name="client">the gameclient this lib is associated with</param>
		public PacketLib172(GameClient client)
			: base(client)
		{
		}

		public override void SendPlayerCreate(GamePlayer playerToCreate)
		{
			Region playerRegion = playerToCreate.CurrentRegion;
			if (playerRegion == null)
			{
				if (log.IsWarnEnabled)
					log.Warn("SendPlayerCreate: playerRegion == null");
				return;
			}

			Zone playerZone = playerToCreate.CurrentZone;
			if (playerZone == null)
			{
				if (log.IsWarnEnabled)
					log.Warn("SendPlayerCreate: playerZone == null");
				return;
			}

			if (m_gameClient.Player == null || playerToCreate.IsVisibleTo(m_gameClient.Player) == false)
				return;

			using (GSTCPPacketOut pak = new GSTCPPacketOut(GetPacketCode(eServerPackets.PlayerCreate172)))
			{
				pak.WriteShort((ushort)playerToCreate.Client.SessionID);
				pak.WriteShort((ushort)playerToCreate.ObjectID);
				pak.WriteShort(playerToCreate.Model);
				pak.WriteShort((ushort)playerToCreate.Z);
	            //Dinberg:Instances - Zoneskin ID for clientside positioning 'bluff'.
				pak.WriteShort(playerZone.ZoneSkinID);
				pak.WriteShort((ushort)playerRegion.GetXOffInZone(playerToCreate.X, playerToCreate.Y));
				pak.WriteShort((ushort)playerRegion.GetYOffInZone(playerToCreate.X, playerToCreate.Y));
				pak.WriteShort(playerToCreate.Heading);

				pak.WriteByte(playerToCreate.GetFaceAttribute(eCharFacePart.EyeSize)); //1-4 = Eye Size / 5-8 = Nose Size
				pak.WriteByte(playerToCreate.GetFaceAttribute(eCharFacePart.LipSize)); //1-4 = Ear size / 5-8 = Kin size
				pak.WriteByte(playerToCreate.GetFaceAttribute(eCharFacePart.EyeColor)); //1-4 = Skin Color / 5-8 = Eye Color
				pak.WriteByte(playerToCreate.GetDisplayLevel(m_gameClient.Player));
				pak.WriteByte(playerToCreate.GetFaceAttribute(eCharFacePart.HairColor)); //Hair: 1-4 = Color / 5-8 = unknown
				pak.WriteByte(playerToCreate.GetFaceAttribute(eCharFacePart.FaceType)); //1-4 = Unknown / 5-8 = Face type
				pak.WriteByte(playerToCreate.GetFaceAttribute(eCharFacePart.HairStyle)); //1-4 = Unknown / 5-8 = Hair Style

				int flags = (GameServer.ServerRules.GetLivingRealm(m_gameClient.Player, playerToCreate) & 0x03) << 2;
				if (playerToCreate.IsAlive == false)
					flags |= 0x01;
				if (playerToCreate.IsUnderwater)
					flags |= 0x02; //swimming
				if (playerToCreate.IsStealthed)
					flags |= 0x10;
				if (playerToCreate.IsWireframe)
					flags |= 0x20;
				pak.WriteByte((byte)flags);

				pak.WritePascalString(GameServer.ServerRules.GetPlayerName(m_gameClient.Player, playerToCreate));
				pak.WritePascalString(GameServer.ServerRules.GetPlayerGuildName(m_gameClient.Player, playerToCreate));
				pak.WritePascalString(GameServer.ServerRules.GetPlayerLastName(m_gameClient.Player, playerToCreate));
				pak.WriteByte(0x00); //Trialing 0 ... needed!
				SendTCP(pak);
			}

			SendObjectGuildID(playerToCreate, playerToCreate.Guild); //used for nearest friendly/enemy object buttons and name colors on PvP server
		}

		/// <summary>
		/// This is used to build a server side "Position Object"
		/// Usually Position Packet Should only be relayed
		/// The only purpose of this method is refreshing postion when there is Lag
		/// </summary>
		/// <param name="player"></param>
		public override void SendPlayerForgedPosition(GamePlayer player)
		{
			using (GSUDPPacketOut pak = new GSUDPPacketOut(GetPacketCode(eServerPackets.PlayerPosition)))
			{
				// PID
				pak.WriteShort((ushort)player.Client.SessionID);

				// Write Speed
				if (player.Steed != null && player.Steed.ObjectState == GameObject.eObjectState.Active)
				{
					player.Heading = player.Steed.Heading;
					pak.WriteShort(0x1800);
				}
				else
				{
					short rSpeed = player.CurrentSpeed;
					if (player.IsIncapacitated)
						rSpeed = 0;

					ushort content;
					if (rSpeed < 0)
					{
						content = (ushort)((Math.Abs(rSpeed) > 511 ? 511 : Math.Abs(rSpeed)) + 0x200);
					}
					else
					{
						content = (ushort)(rSpeed > 511 ? 511 : rSpeed);
					}

					if (!player.IsAlive)
					{
						content += 5 << 10;
					}
					else
					{
						ushort state = 0;

						if (player.IsSwimming)
							state = 1;

						if (player.IsClimbing)
							state = 7;

						if (player.IsSitting)
							state = 4;

						content += (ushort)(state << 10);
					}

					content += (ushort)(player.IsStrafing ? 1 << 13 : 0 << 13);

					pak.WriteShort(content);
				}

				// Get Off Corrd
				int offX = player.X - player.CurrentZone.XOffset;
				int offY = player.Y - player.CurrentZone.YOffset;

				pak.WriteShort((ushort)player.Z);
				pak.WriteShort((ushort)offX);
				pak.WriteShort((ushort)offY);

				// Write Zone
				pak.WriteShort(player.CurrentZone.ZoneSkinID);

				// Copy Heading && Falling or Write Steed
				if (player.Steed != null && player.Steed.ObjectState == GameObject.eObjectState.Active)
				{
					pak.WriteShort((ushort)player.Steed.ObjectID);
					pak.WriteShort((ushort)player.Steed.RiderSlot(player));
				}
				else
				{
					// Set Player always on ground, this is an "anti lag" packet
					ushort contenthead = (ushort)(player.Heading + (true ? 0x1000 : 0));
					pak.WriteShort(contenthead);
					// No Fall Speed.
					pak.WriteShort(0);
				}

				// Write Flags
				byte flagcontent = 0;

				if (player.IsDiving)
				{
					flagcontent += 0x04;
				}

				if (player.IsWireframe)
				{
					flagcontent += 0x01;
				}

				if (player.IsStealthed)
				{
					flagcontent += 0x02;
				}

				if (player.IsTorchLighted)
				{
					flagcontent += 0x80;
				}

				pak.WriteByte(flagcontent);

				// Write health + Attack
				byte healthcontent = (byte)(player.HealthPercent + (player.attackComponent.AttackState ? 0x80 : 0));

				pak.WriteByte(healthcontent);

				SendUDP(pak);
			}
		}

		protected override void SendInventorySlotsUpdateRange(ICollection<int> slots, eInventoryWindowType windowType)
		{
			using (GSTCPPacketOut pak = new GSTCPPacketOut(GetPacketCode(eServerPackets.InventoryUpdate)))
			{
				pak.WriteByte((byte)(slots == null ? 0 : slots.Count));
				pak.WriteByte((byte)((m_gameClient.Player.IsCloakHoodUp ? 0x01 : 0x00) | (int)m_gameClient.Player.rangeAttackComponent.ActiveQuiverSlot)); //bit0 is hood up bit4 to 7 is active quiver
				pak.WriteByte((byte)m_gameClient.Player.VisibleActiveWeaponSlots);
				pak.WriteByte((byte)windowType);
				if (slots != null)
				{
					foreach (int updatedSlot in slots)
					{
						if (updatedSlot >= (int)eInventorySlot.Consignment_First && updatedSlot <= (int)eInventorySlot.Consignment_Last)
							pak.WriteByte((byte)(updatedSlot - (int)eInventorySlot.Consignment_First + (int)eInventorySlot.HousingInventory_First));
						else
							pak.WriteByte((byte)(updatedSlot));
						DbInventoryItem item = null;
						item = m_gameClient.Player.Inventory.GetItem((eInventorySlot)updatedSlot);

						if (item == null)
						{
							pak.Fill(0x00, 19);
							continue;
						}

						pak.WriteByte((byte)item.Level);

						int value1; // some object types use this field to display count
						int value2; // some object types use this field to display count
						switch (item.Object_Type)
						{
							case (int)eObjectType.Arrow:
							case (int)eObjectType.Bolt:
							case (int)eObjectType.Poison:
							case (int)eObjectType.GenericItem:
								value1 = item.Count;
								value2 = item.SPD_ABS;
								break;
							case (int)eObjectType.Thrown:
								value1 = item.DPS_AF;
								value2 = item.Count;
								break;
							case (int)eObjectType.Instrument:
								value1 = (item.DPS_AF == 2 ? 0 : item.DPS_AF);
								value2 = 0;
								break; // unused
							case (int)eObjectType.Shield:
								value1 = item.Type_Damage;
								value2 = item.DPS_AF;
								break;
							case (int)eObjectType.AlchemyTincture:
							case (int)eObjectType.SpellcraftGem:
								value1 = 0;
								value2 = 0;
								/*
								must contain the quality of gem for spell craft and think same for tincture
								*/
								break;
							case (int)eObjectType.GardenObject:
								value1 = 0;
								value2 = item.SPD_ABS;
								/*
								Value2 byte sets the width, only lower 4 bits 'seem' to be used (so 1-15 only)

								The byte used for "Hand" (IE: Mini-delve showing a weapon as Left-Hand
								usabe/TwoHanded), the lower 4 bits store the height (1-15 only)
								*/
								break;

							default:
								value1 = item.DPS_AF;
								value2 = item.SPD_ABS;
								break;
						}
						pak.WriteByte((byte)value1);
						pak.WriteByte((byte)value2);

						if (item.Object_Type == (int)eObjectType.GardenObject)
							pak.WriteByte((byte)(item.DPS_AF));
						else
							pak.WriteByte((byte)(item.Hand << 6));
						pak.WriteByte((byte)((item.Type_Damage > 3 ? 0 : item.Type_Damage << 6) | item.Object_Type));
						pak.WriteShort((ushort)item.Weight);
						pak.WriteByte(item.ConditionPercent); // % of con
						pak.WriteByte(item.DurabilityPercent); // % of dur
						pak.WriteByte((byte)item.Quality); // % of qua
						pak.WriteByte((byte)item.Bonus); // % bonus
						pak.WriteShort((ushort)item.Model);
						pak.WriteByte((byte)item.Extension);
						if (item.Emblem != 0)
							pak.WriteShort((ushort)item.Emblem);
						else
							pak.WriteShort((ushort)item.Color);
						pak.WriteShort((ushort)item.Effect);
						string name = item.Name;
						if (item.Count > 1)
							name = item.Count + " " + name;
	                    if (item.SellPrice > 0)
	                    {
							if (ServerProperties.Properties.CONSIGNMENT_USE_BP)
	                            name += "[" + item.SellPrice.ToString() + " BP]";
	                        else
	                            name += "[" + Money.GetString(item.SellPrice) + "]";
	                    }
						pak.WritePascalString(name);
					}
				}
				SendTCP(pak);
			}
		}

		public override void SendLivingEquipmentUpdate(GameLiving living)
		{
			if (m_gameClient.Player == null || living.IsVisibleTo(m_gameClient.Player) == false)
				return;

			using (GSTCPPacketOut pak = new GSTCPPacketOut(GetPacketCode(eServerPackets.EquipmentUpdate)))
			{

				ICollection<DbInventoryItem> items = null;
				if (living.Inventory != null)
					items = living.Inventory.VisibleItems;

				pak.WriteShort((ushort)living.ObjectID);
				pak.WriteByte((byte)((living.IsCloakHoodUp ? 0x01 : 0x00) | (int)living.rangeAttackComponent.ActiveQuiverSlot)); //bit0 is hood up bit4 to 7 is active quiver

				pak.WriteByte((byte)living.VisibleActiveWeaponSlots);
				if (items != null)
				{
					pak.WriteByte((byte)items.Count);
					foreach (DbInventoryItem item in items)
					{
						pak.WriteByte((byte)item.SlotPosition);

						ushort model = (ushort)(item.Model & 0x1FFF);
						if (model > 2952)
							model = 0;

						int texture = (item.Emblem != 0) ? item.Emblem : item.Color;

						if ((texture & ~0xFF) != 0)
							model |= 0x8000;
						else if ((texture & 0xFF) != 0)
							model |= 0x4000;
						if (item.Effect != 0)
							model |= 0x2000;

						pak.WriteShort(model);

						if (item.SlotPosition > Slot.RANGED || item.SlotPosition < Slot.RIGHTHAND)
							pak.WriteByte((byte)item.Extension);

						if ((texture & ~0xFF) != 0)
							pak.WriteShort((ushort)texture);
						else if ((texture & 0xFF) != 0)
							pak.WriteByte((byte)texture);
						if (item.Effect != 0)
							pak.WriteShort((ushort)item.Effect);
					}
				}
				else
				{
					pak.WriteByte(0x00);
				}
				SendTCP(pak);
			}
		}

		public override void SendTradeWindow()
		{
			if (m_gameClient.Player == null)
				return;
			if (m_gameClient.Player.TradeWindow == null)
				return;
			using (GSTCPPacketOut pak = new GSTCPPacketOut(GetPacketCode(eServerPackets.TradeWindow)))
			{
				lock (m_gameClient.Player.TradeWindow.Sync)
				{
					foreach (DbInventoryItem item in m_gameClient.Player.TradeWindow.TradeItems)
					{
						pak.WriteByte((byte)item.SlotPosition);
					}
					pak.Fill(0x00, 10 - m_gameClient.Player.TradeWindow.TradeItems.Count);

					pak.WriteShort(0x0000);
					pak.WriteShort((ushort)Money.GetMithril(m_gameClient.Player.TradeWindow.TradeMoney));
					pak.WriteShort((ushort)Money.GetPlatinum(m_gameClient.Player.TradeWindow.TradeMoney));
					pak.WriteShort((ushort)Money.GetGold(m_gameClient.Player.TradeWindow.TradeMoney));
					pak.WriteShort((ushort)Money.GetSilver(m_gameClient.Player.TradeWindow.TradeMoney));
					pak.WriteShort((ushort)Money.GetCopper(m_gameClient.Player.TradeWindow.TradeMoney));

					pak.WriteShort(0x0000);
					pak.WriteShort((ushort)Money.GetMithril(m_gameClient.Player.TradeWindow.PartnerTradeMoney));
					pak.WriteShort((ushort)Money.GetPlatinum(m_gameClient.Player.TradeWindow.PartnerTradeMoney));
					pak.WriteShort((ushort)Money.GetGold(m_gameClient.Player.TradeWindow.PartnerTradeMoney));
					pak.WriteShort((ushort)Money.GetSilver(m_gameClient.Player.TradeWindow.PartnerTradeMoney));
					pak.WriteShort((ushort)Money.GetCopper(m_gameClient.Player.TradeWindow.PartnerTradeMoney));

					pak.WriteShort(0x0000);
					ArrayList items = m_gameClient.Player.TradeWindow.PartnerTradeItems;
					if (items != null)
					{
						pak.WriteByte((byte)items.Count);
						pak.WriteByte(0x01);
					}
					else
					{
						pak.WriteShort(0x0000);
					}
					pak.WriteByte((byte)(m_gameClient.Player.TradeWindow.Repairing ? 0x01 : 0x00));
					pak.WriteByte((byte)(m_gameClient.Player.TradeWindow.Combine ? 0x01 : 0x00));
					if (items != null)
					{
						foreach (DbInventoryItem item in items)
						{
							pak.WriteByte((byte)item.SlotPosition);
							pak.WriteByte((byte)item.Level);
							pak.WriteByte((byte)item.DPS_AF); // dps_af
							pak.WriteByte((byte)item.SPD_ABS); //spd_abs
							pak.WriteByte((byte)(item.Hand << 6));
							pak.WriteByte((byte)((item.Type_Damage > 3 ? 0 : item.Type_Damage << 6) | item.Object_Type));
							pak.WriteShort((ushort)item.Weight); // weight
							pak.WriteByte(item.ConditionPercent); // con %
							pak.WriteByte(item.DurabilityPercent); // dur %
							pak.WriteByte((byte)item.Quality); // qua %
							pak.WriteByte((byte)item.Bonus); // bon %
							pak.WriteShort((ushort)item.Model); //model

							if (item.SlotPosition > Slot.RANGED || item.SlotPosition < Slot.RIGHTHAND)
								pak.WriteByte((byte)item.Extension);

							pak.WriteShort((ushort)item.Color); //color
							pak.WriteShort((ushort)item.Effect); //weaponproc
							if (item.Count > 1)
								pak.WritePascalString(item.Count + " " + item.Name);
							else
								pak.WritePascalString(item.Name); //size and name item
						}
					}
					if (m_gameClient.Player.TradeWindow.Partner != null)
						pak.WritePascalString("Trading with " + m_gameClient.Player.GetName(m_gameClient.Player.TradeWindow.Partner)); // transaction with ...
					else
						pak.WritePascalString("Selfcrafting"); // transaction with ...
					SendTCP(pak);
				}
			}
		}
	}
}
