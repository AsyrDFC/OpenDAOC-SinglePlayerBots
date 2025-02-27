using System;
using System.Linq;
using System.Reflection;
using System.Text;
using DOL.Database;
using DOL.GS.Effects;
using DOL.GS.Utils;
using DOL.Language;
using log4net;

namespace DOL.GS.PacketHandler.Client.v168
{
	[PacketHandlerAttribute(PacketHandlerType.TCP, eClientPackets.PositionUpdate, "Handles player position updates", eClientStatus.PlayerInGame)]
	public class PlayerPositionUpdateHandler : IPacketHandler
	{
		/// <summary>
		/// Defines a logger for this class.
		/// </summary>
		private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

		public const string LASTMOVEMENTTICK = "PLAYERPOSITION_LASTMOVEMENTTICK";
		public const string LASTCPSTICK = "PLAYERPOSITION_LASTCPSTICK";

		/// <summary>
		/// Stores the count of times the player is above speedhack tolerance!
		/// If this value reaches 10 or more, a logfile entry is written.
		/// </summary>
		public const string SPEEDHACKCOUNTER = "SPEEDHACKCOUNTER";
		public const string SHSPEEDCOUNTER = "MYSPEEDHACKCOUNTER";

		//static int lastZ=int.MinValue;
		public void HandlePacket(GameClient client, GSPacketIn packet)
		{
			//Tiv: in very rare cases client send 0xA9 packet before sending S<=C 0xE8 player world initialize
			if ((client.Player.ObjectState != GameObject.eObjectState.Active) || (client.ClientState != GameClient.eClientState.Playing))
				return;

			// Don't allow movement if the player isn't close to the NPC they're supposed to be riding.
			// Instead, teleport them to it and send an update packet (the client may then ask for a create packet).
			if (client.Player.Steed != null && client.Player.Steed.ObjectState == GameObject.eObjectState.Active)
			{
				GamePlayer rider = client.Player;
				GameNPC steed = rider.Steed;

				// The rider and their steed are never at the exact same position (made worse by a high latency).
				// So the radius is arbitrary and must not be too low to avoid spamming packets.
				if (!rider.IsWithinRadius(steed, 1000))
				{
					rider.X = steed.X;
					rider.Y = steed.Y;
					rider.Z = steed.Z;
					rider.Heading = steed.Heading;
					rider.MovementStartTick = GameLoop.GameLoopTime;
					rider.Out.SendPlayerJump(false);
					return;
				}
			}

			if (client.Version >= GameClient.eClientVersion.Version1124)
			{
				_HandlePacket1124(client, packet);
				return;
			}

			long environmentTick = GameLoop.GameLoopTime; 
			int oldSpeed = client.Player.CurrentSpeed;

			//read the state of the player
			packet.Skip(2); //PID
			ushort speedData = packet.ReadShort();
			int speed = (speedData & 0x1FF);

			//			if(!GameServer.ServerRules.IsAllowedDebugMode(client)
			//				&& (speed > client.Player.MaxSpeed + SPEED_TOL))


			if ((speedData & 0x200) != 0)
				speed = -speed;

			if ((client.Player.IsMezzed || client.Player.IsStunned) && client.Player.effectListComponent.GetAllEffects().FirstOrDefault(x => x.GetType() == typeof(SpeedOfSoundECSEffect)) == null)
				// Nidel: updating client.Player.CurrentSpeed instead of speed
				client.Player.CurrentSpeed = 0;
			else
				client.Player.CurrentSpeed = (short)speed;

			client.Player.IsStrafing = ((speedData & 0xe000) != 0);

			int realZ = packet.ReadShort();
			ushort xOffsetInZone = packet.ReadShort();
			ushort yOffsetInZone = packet.ReadShort();
			ushort currentZoneID = packet.ReadShort();

            try
            {
				Zone grabZone = WorldMgr.GetZone(currentZoneID);
            } catch (Exception e)
            {
				//if we get a zone that doesn't exist, move player to their bindstone
				client.Player.MoveTo(
					(ushort)client.Player.BindRegion,
					client.Player.BindXpos,
					client.Player.BindYpos,
					(ushort)client.Player.BindZpos,
					(ushort)client.Player.BindHeading
					);
				return;
			
			}
			

			//Dinberg - Instance considerations.
			//Now this gets complicated, so listen up! We have told the client a lie when it comes to the zoneID.
			//As a result, every movement update, they are sending a lie back to us. Two liars could get confusing!

			//BUT, the lie we sent has a truth to it - the geometry and layout of the zone. As a result, the zones
			//x and y offsets will still actually be relevant to our current zone. And for the clones to have been
			//created, there must have been a real zone to begin with, of id == instanceZone.SkinID.

			//So, although our client is lying to us, and thinks its in another zone, that zone happens to coincide
			//exactly with the zone we are instancing - and so all the positions still ring true.

			//Philosophically speaking, its like looking in a mirror and saying 'Am I a reflected, or reflector?'
			//What it boils down to has no bearing whatsoever on the result of anything, so long as someone sitting
			//outside of the unvierse knows not to listen to whether you say which you are, and knows the truth to the
			//answer. Then, he need only know what you are doing ;)

			Zone newZone = WorldMgr.GetZone(currentZoneID);			
			if (newZone == null)
			{
				if(client.Player==null) return;
				if(!client.Player.TempProperties.GetProperty("isbeingbanned",false))
				{
					if (log.IsErrorEnabled)
						log.Error(client.Player.Name + "'s position in unknown zone! => " + currentZoneID);
					GamePlayer player=client.Player;
					player.TempProperties.SetProperty("isbeingbanned", true);
					player.MoveToBind();
				}

				return; // TODO: what should we do? player lost in space
			}

			// move to bind if player fell through the floor
			if (realZ == 0)
			{
				client.Player.MoveTo(
					(ushort)client.Player.BindRegion,
					client.Player.BindXpos,
					client.Player.BindYpos,
					(ushort)client.Player.BindZpos,
					(ushort)client.Player.BindHeading
				);
				return;
			}

			int realX = newZone.XOffset + xOffsetInZone;
			int realY = newZone.YOffset + yOffsetInZone;

			bool zoneChange = newZone != client.Player.LastPositionUpdateZone;
			if (zoneChange)
			{
				//If the region changes -> make sure we don't take any falling damage
				if (client.Player.LastPositionUpdateZone != null && newZone.ZoneRegion.ID != client.Player.LastPositionUpdateZone.ZoneRegion.ID)
					client.Player.MaxLastZ = int.MinValue;

				// Update water level and diving flag for the new zone
				// commenting this out for now, creates a race condition when teleporting within same region, jumping player back and forth as player xyz isnt updated yet.
				//client.Out.SendPlayerPositionAndObjectID();		
				zoneChange = true;

				/*
				 * "You have entered Burial Tomb."
				 * "Burial Tomb"
				 * "Current area is adjusted for one level 1 player."
				 * "Current area has a 50% instance bonus."
				 */

                string description = newZone.Description;
                string screenDescription = description;

                var translation = LanguageMgr.GetTranslation(client, newZone) as DbLanguageZone;
                if (translation != null)
                {
                    if (!string.IsNullOrEmpty(translation.Description))
                        description = translation.Description;

                    if (!string.IsNullOrEmpty(translation.ScreenDescription))
                        screenDescription = translation.ScreenDescription;
                }

                client.Out.SendMessage(LanguageMgr.GetTranslation(client.Account.Language, "PlayerPositionUpdateHandler.Entered", description),
				                       eChatType.CT_System, eChatLoc.CL_SystemWindow);
                client.Out.SendMessage(screenDescription, eChatType.CT_ScreenCenterSmaller, eChatLoc.CL_SystemWindow);

				client.Player.LastPositionUpdateZone = newZone;

				if (client.Player.GMStealthed)
					client.Player.Stealth(true);
			}

			int coordsPerSec = 0;
			int jumpDetect = 0;
			int timediff = (int)(GameLoop.GameLoopTime - client.Player.LastPositionUpdateTick);
			int distance = 0;

			if (timediff > 0)
			{
				var newPoint = new Point3D(realX, realY, realZ);
				distance = newPoint.GetDistanceTo(new Point3D((int)client.Player.LastPositionUpdatePoint.X, (int)client.Player.LastPositionUpdatePoint.Y,
					(int)client.Player.LastPositionUpdatePoint.Z));
				coordsPerSec = distance * 1000 / timediff;

				if (distance < 100 && client.Player.LastPositionUpdatePoint.Z > 0)
				{
					jumpDetect = realZ - (int)client.Player.LastPositionUpdatePoint.Z;
				}
			}

			#region DEBUG
			#if OUTPUT_DEBUG_INFO
			if (client.Player.LastPositionUpdatePoint.X != 0 && client.Player.LastPositionUpdatePoint.Y != 0)
			{
				log.Debug(client.Player.Name + ": distance = " + distance + ", speed = " + oldSpeed + ",  coords/sec=" + coordsPerSec);
			}
			if (jumpDetect > 0)
			{
				log.Debug(client.Player.Name + ": jumpdetect = " + jumpDetect);
			}
			#endif
			#endregion DEBUG

			client.Player.LastPositionUpdateTick = GameLoop.GameLoopTime;
			client.Player.LastPositionUpdatePoint.X = realX;
			client.Player.LastPositionUpdatePoint.Y = realY;
			client.Player.LastPositionUpdatePoint.Z = realZ;

			int tolerance = ServerProperties.Properties.CPS_TOLERANCE;

			if (client.Player.Steed != null && client.Player.Steed.MaxSpeed > 0)
				tolerance += client.Player.Steed.MaxSpeed;
			else if (client.Player.MaxSpeed > 0)
				tolerance += client.Player.MaxSpeed;

			if (client.Player.IsJumping)
			{
				coordsPerSec = 0;
				jumpDetect = 0;
				client.Player.IsJumping = false;
			}

			if (client.Player.IsAllowedToFly == false && (coordsPerSec > tolerance || jumpDetect > ServerProperties.Properties.JUMP_TOLERANCE))
			{
				bool isHackDetected = true;

				if (coordsPerSec > tolerance)
				{
					// check to see if CPS time tolerance is exceeded
					int lastCPSTick = client.Player.TempProperties.GetProperty<int>(LASTCPSTICK, 0);

					if (environmentTick - lastCPSTick > ServerProperties.Properties.CPS_TIME_TOLERANCE)
					{
						isHackDetected = false;
					}
				}

				if (isHackDetected)
				{
					StringBuilder builder = new StringBuilder();
					builder.Append("MOVEHACK_DETECT");
					builder.Append(": CharName=");
					builder.Append(client.Player.Name);
					builder.Append(" Account=");
					builder.Append(client.Account.Name);
					builder.Append(" IP=");
					builder.Append(client.TcpEndpointAddress);
					builder.Append(" CPS:=");
					builder.Append(coordsPerSec);
					builder.Append(" JT=");
					builder.Append(jumpDetect);
					ChatUtil.SendDebugMessage(client, builder.ToString());

					if (client.Account.PrivLevel == 1)
					{
						GameServer.Instance.LogCheatAction(builder.ToString());

						if (ServerProperties.Properties.ENABLE_MOVEDETECT)
						{
							if (ServerProperties.Properties.BAN_HACKERS && false) // banning disabled until this technique is proven accurate
							{
								DbBans b = new DbBans();
								b.Author = "SERVER";
								b.Ip = client.TcpEndpointAddress;
								b.Account = client.Account.Name;
								b.DateBan = DateTime.Now;
								b.Type = "B";
								b.Reason = string.Format("Autoban MOVEHACK:(CPS:{0}, JT:{1}) on player:{2}", coordsPerSec, jumpDetect, client.Player.Name);
								GameServer.Database.AddObject(b);
								GameServer.Database.SaveObject(b);

								string message = "";
								
								message = "You have been auto kicked and banned due to movement hack detection!";
								for (int i = 0; i < 8; i++)
								{
									client.Out.SendMessage(message, eChatType.CT_Help, eChatLoc.CL_SystemWindow);
									client.Out.SendMessage(message, eChatType.CT_Help, eChatLoc.CL_ChatWindow);
								}

								client.Out.SendPlayerQuit(true);
								client.Player.SaveIntoDatabase();
								client.Player.Quit(true);
							}
							else
							{
								string message = "";
								
								message = "You have been auto kicked due to movement hack detection!";
								for (int i = 0; i < 8; i++)
								{
									client.Out.SendMessage(message, eChatType.CT_Help, eChatLoc.CL_SystemWindow);
									client.Out.SendMessage(message, eChatType.CT_Help, eChatLoc.CL_ChatWindow);
								}

								client.Out.SendPlayerQuit(true);
								client.Player.SaveIntoDatabase();
								client.Player.Quit(true);
							}
							client.Disconnect();
							return;
						}
					}
				}

				client.Player.TempProperties.SetProperty(LASTCPSTICK, environmentTick);
			}

			var headingflag = packet.ReadShort();
			var flyingflag = packet.ReadShort();
			var flags = (byte)packet.ReadByte();

			client.Player.Heading = (ushort)(headingflag & 0xFFF);
			if (client.Player.X != realX || client.Player.Y != realY)
				client.Player.TempProperties.SetProperty(LASTMOVEMENTTICK, client.Player.CurrentRegion.Time);
			client.Player.X = realX;
			client.Player.Y = realY;
			client.Player.Z = realZ;

			// update client zone information for waterlevel and diving
			if (zoneChange)
				client.Out.SendPlayerPositionAndObjectID();

			// used to predict current position, should be before
			// any calculation (like fall damage)
			client.Player.MovementStartTick = GameLoop.GameLoopTime;

			// Begin ---------- New Area System -----------
			if (client.Player.CurrentRegion.Time > client.Player.AreaUpdateTick) // check if update is needed
			{
				var oldAreas = client.Player.CurrentAreas;

				// Because we may be in an instance we need to do the area check from the current region
				// rather than relying on the zone which is in the skinned region.  - Tolakram

				var newAreas = client.Player.CurrentRegion.GetAreasOfZone(newZone, client.Player);

				// Check for left areas
				if (oldAreas != null)
					foreach (IArea area in oldAreas)
					{
						if (!newAreas.Contains(area))
						{
							area.OnPlayerLeave(client.Player);
							
							//Check if leaving Border Keep areas so we can check RealmTimer
							AbstractArea checkrvrarea = area as AbstractArea;
							if (checkrvrarea != null && (checkrvrarea.Description.Equals("Castle Sauvage") || 
								checkrvrarea.Description.Equals("Snowdonia Fortress") || 
								checkrvrarea.Description.Equals("Svasud Faste") ||
								checkrvrarea.Description.Equals("Vindsaul Faste") ||
								checkrvrarea.Description.Equals("Druim Ligen") ||
								checkrvrarea.Description.Equals("Druim Cain")))
							{
								RealmTimer.CheckRealmTimer(client.Player);
							}
						}
					}
				// Check for entered areas
				foreach (IArea area in newAreas)
					if (oldAreas == null || !oldAreas.Contains(area))
						area.OnPlayerEnter(client.Player);
				// set current areas to new one...
				client.Player.CurrentAreas = newAreas;
				client.Player.AreaUpdateTick = client.Player.CurrentRegion.Time + 750; // update every .75 seconds
			}
			// End ---------- New Area System -----------


			client.Player.TargetInView = (flags & 0x10) != 0;
			client.Player.GroundTargetInView = (flags & 0x08) != 0;
			client.Player.IsTorchLighted = (flags & 0x80) != 0;
			client.Player.IsDiving = (flags & 0x02) != 0x00;
			//7  6  5  4  3  2  1 0
			//15 14 13 12 11 10 9 8
			//                1 1

			const string SHLASTUPDATETICK = "SHPLAYERPOSITION_LASTUPDATETICK";
			const string SHLASTFLY = "SHLASTFLY_STRING";
			const string SHLASTSTATUS = "SHLASTSTATUS_STRING";
			long SHlastTick = client.Player.TempProperties.GetProperty<long>(SHLASTUPDATETICK);
			int SHlastFly = client.Player.TempProperties.GetProperty<int>(SHLASTFLY);
			int SHlastStatus = client.Player.TempProperties.GetProperty<int>(SHLASTSTATUS);
			int SHcount = client.Player.TempProperties.GetProperty<int>(SHSPEEDCOUNTER);
			int status = (speedData & 0x1FF ^ speedData) >> 8;
			int fly = (flyingflag & 0x1FF ^ flyingflag) >> 8;

			if (client.Player.IsJumping)
				SHcount = 0;
			if (SHlastTick != 0 && SHlastTick != environmentTick)
			{
				if (((SHlastStatus == status || (status & 0x8) == 0)) && ((fly & 0x80) != 0x80) && (SHlastFly == fly || (SHlastFly & 0x10) == (fly & 0x10) || !((((SHlastFly & 0x10) == 0x10) && ((fly & 0x10) == 0x0) && (flyingflag & 0x7FF) > 0))))
				{
					if ((environmentTick - SHlastTick) < 400)
					{
						SHcount++;

						if (SHcount > 1 && client.Account.PrivLevel > 1)
						{
							//Apo: ?? no idea how to name the first parameter for language translation: 1: ??, 2: {detected} ?, 3: {count} ?
							client.Out.SendMessage(string.Format("SH: ({0}) detected: {1}, count {2}", 500 / (environmentTick - SHlastTick), environmentTick - SHlastTick, SHcount), eChatType.CT_Staff, eChatLoc.CL_SystemWindow);
						}

						if (SHcount % 5 == 0)
						{
							StringBuilder builder = new StringBuilder();
							builder.Append("TEST_SH_DETECT[");
							builder.Append(SHcount);
							builder.Append("] (");
							builder.Append(environmentTick - SHlastTick);
							builder.Append("): CharName=");
							builder.Append(client.Player.Name);
							builder.Append(" Account=");
							builder.Append(client.Account.Name);
							builder.Append(" IP=");
							builder.Append(client.TcpEndpointAddress);
							GameServer.Instance.LogCheatAction(builder.ToString());

							if (client.Account.PrivLevel > 1)
							{
								client.Out.SendMessage("SH: Logging SH cheat.", eChatType.CT_Damaged, eChatLoc.CL_SystemWindow);

								if (SHcount >= ServerProperties.Properties.SPEEDHACK_TOLERANCE)
									client.Out.SendMessage("SH: Player would have been banned!", eChatType.CT_Damaged, eChatLoc.CL_SystemWindow);
							}

							if ((client.Account.PrivLevel == 1) && SHcount >= ServerProperties.Properties.SPEEDHACK_TOLERANCE)
							{
								if (ServerProperties.Properties.BAN_HACKERS)
								{
									DbBans b = new DbBans();
									b.Author = "SERVER";
									b.Ip = client.TcpEndpointAddress;
									b.Account = client.Account.Name;
									b.DateBan = DateTime.Now;
									b.Type = "B";
									b.Reason = string.Format("Autoban SH:({0},{1}) on player:{2}", SHcount, environmentTick - SHlastTick, client.Player.Name);
									GameServer.Database.AddObject(b);
									GameServer.Database.SaveObject(b);

									string message = "";
									
									message = "You have been auto kicked and banned for speed hacking!";
									for (int i = 0; i < 8; i++)
									{
										client.Out.SendMessage(message, eChatType.CT_Help, eChatLoc.CL_SystemWindow);
										client.Out.SendMessage(message, eChatType.CT_Help, eChatLoc.CL_ChatWindow);
									}

									client.Out.SendPlayerQuit(true);
									client.Player.SaveIntoDatabase();
									client.Player.Quit(true);
								}
								else
								{
									string message = "";
									
									message = "You have been auto kicked for speed hacking!";
									for (int i = 0; i < 8; i++)
									{
										client.Out.SendMessage(message, eChatType.CT_Help, eChatLoc.CL_SystemWindow);
										client.Out.SendMessage(message, eChatType.CT_Help, eChatLoc.CL_ChatWindow);
									}

									client.Out.SendPlayerQuit(true);
									client.Player.SaveIntoDatabase();
									client.Player.Quit(true);
								}
								client.Disconnect();
								return;
							}
						}
					}
					else
					{
						SHcount = 0;
					}

					SHlastTick = environmentTick;
				}
			}
			else
				SHlastTick = environmentTick;

			int state = ((speedData >> 10) & 7);
			client.Player.IsClimbing = (state == 7);
			client.Player.IsSwimming = (state == 1);
			// debugFly on, but player not do /debug on (hack)
			if (state == 3 && !client.Player.TempProperties.GetProperty(GamePlayer.DEBUG_MODE_PROPERTY, false) && !client.Player.IsAllowedToFly)
			{
				StringBuilder builder = new StringBuilder();
				builder.Append("HACK_FLY");
				builder.Append(": CharName=");
				builder.Append(client.Player.Name);
				builder.Append(" Account=");
				builder.Append(client.Account.Name);
				builder.Append(" IP=");
				builder.Append(client.TcpEndpointAddress);
				GameServer.Instance.LogCheatAction(builder.ToString());
				{
					if (ServerProperties.Properties.BAN_HACKERS)
					{
						DbBans b = new DbBans();
						b.Author = "SERVER";
						b.Ip = client.TcpEndpointAddress;
						b.Account = client.Account.Name;
						b.DateBan = DateTime.Now;
						b.Type = "B";
						b.Reason = string.Format("Autoban flying hack: on player:{0}", client.Player.Name);
						GameServer.Database.AddObject(b);
						GameServer.Database.SaveObject(b);
					}
					string message = "";
					
					message = "Client Hack Detected!";
					for (int i = 0; i < 6; i++)
					{
						client.Out.SendMessage(message, eChatType.CT_System, eChatLoc.CL_SystemWindow);
						client.Out.SendMessage(message, eChatType.CT_System, eChatLoc.CL_ChatWindow);
					}
					client.Out.SendPlayerQuit(true);
					client.Disconnect();
					return;
				}
			}

			SHlastFly = fly;
			SHlastStatus = status;
			client.Player.TempProperties.SetProperty(SHLASTUPDATETICK, SHlastTick);
			client.Player.TempProperties.SetProperty(SHLASTFLY, SHlastFly);
			client.Player.TempProperties.SetProperty(SHLASTSTATUS, SHlastStatus);
			client.Player.TempProperties.SetProperty(SHSPEEDCOUNTER, SHcount);
			lock (client.Player.LastUniqueLocations)
			{
				GameLocation[] locations = client.Player.LastUniqueLocations;
				GameLocation loc = locations[0];
				if (loc.X != realX || loc.Y != realY || loc.Z != realZ || loc.RegionID != client.Player.CurrentRegionID)
				{
					loc = locations[locations.Length - 1];
					Array.Copy(locations, 0, locations, 1, locations.Length - 1);
					locations[0] = loc;
					loc.X = realX;
					loc.Y = realY;
					loc.Z = realZ;
					loc.Heading = client.Player.Heading;
					loc.RegionID = client.Player.CurrentRegionID;
				}
			}

			//**************//
			//FALLING DAMAGE//
			//**************//
			int fallSpeed = 0;
			double fallDamage = 0;
			if (GameServer.ServerRules.CanTakeFallDamage(client.Player) && !client.Player.IsSwimming)
			{
				int maxLastZ = client.Player.MaxLastZ;
				/* Are we on the ground? */
				if ((flyingflag >> 15) != 0)
				{
					int safeFallLevel = client.Player.GetAbilityLevel(Abilities.SafeFall);
					fallSpeed = (flyingflag & 0xFFF) - 100 * safeFallLevel; // 0x7FF fall speed and 0x800 bit = fall speed overcaped
					int fallMinSpeed = 400;
					int fallDivide = 6;
					if (client.Version >= GameClient.eClientVersion.Version188)
					{
						fallMinSpeed = 500;
						fallDivide = 15;
					}

					int fallPercent = Math.Min(99, (fallSpeed - (fallMinSpeed + 1)) / fallDivide);

					if (fallSpeed > fallMinSpeed)
					{
						// client.Out.SendMessage(LanguageMgr.GetTranslation(client.Account.Language, "PlayerPositionUpdateHandler.FallingDamage"),
						// eChatType.CT_Damaged, eChatLoc.CL_SystemWindow);
						fallDamage = client.Player.CalcFallDamage(fallPercent);
					}

					client.Player.MaxLastZ = client.Player.Z;
				}

				else
				{
					// always set Z if on the ground
					if (flyingflag == 0)
						client.Player.MaxLastZ = client.Player.Z;
					// set Z if in air and higher than old Z
					else if (maxLastZ < client.Player.Z)
						client.Player.MaxLastZ = client.Player.Z;
				}
			}
			//**************//

            //Riding is set here!
            if (client.Player.Steed != null && client.Player.Steed.ObjectState == GameObject.eObjectState.Active)
				client.Player.Heading = client.Player.Steed.Heading;

			var outpak = new GSUDPPacketOut(client.Out.GetPacketCode(eServerPackets.PlayerPosition));
			outpak.WriteShort((ushort)client.SessionID);
			if (client.Player.Steed != null && client.Player.Steed.ObjectState == GameObject.eObjectState.Active)
				outpak.WriteShort(0x1800);
			else
			{
				var rSpeed = client.Player.IsIncapacitated ? 0 : client.Player.CurrentSpeed;
				ushort content;
				if (rSpeed < 0)
					content = (ushort)((rSpeed < -511 ? 511 : -rSpeed) + 0x200);
				else
					content = (ushort)(rSpeed > 511 ? 511 : rSpeed);

				if (!client.Player.IsAlive)
					content |= 5 << 10;
				else
				{
					ushort pState = 0;
					if (client.Player.IsSwimming)
						pState = 1;
					if (client.Player.IsClimbing)
						pState = 7;
					if (client.Player.IsSitting)
						pState = 4;
					if (client.Player.IsStrafing)
						pState |= 8;
					content |= (ushort)(pState << 10);
				}
				outpak.WriteShort(content);
			}
			outpak.WriteShort((ushort)client.Player.Z);
			outpak.WriteShort((ushort)(client.Player.X - client.Player.CurrentZone.XOffset));
			outpak.WriteShort((ushort)(client.Player.Y - client.Player.CurrentZone.YOffset));
			// Write Zone
			outpak.WriteShort(client.Player.CurrentZone.ZoneSkinID);

			// Copy Heading && Falling or Write Steed
			if (client.Player.Steed != null && client.Player.Steed.ObjectState == GameObject.eObjectState.Active)
			{
				outpak.WriteShort((ushort)client.Player.Steed.ObjectID);
				outpak.WriteShort((ushort)client.Player.Steed.RiderSlot(client.Player));
			}
			else
			{
				// Set Player always on ground, this is an "anti lag" packet
				ushort contenthead = (ushort)(client.Player.Heading + (true ? 0x1000 : 0));
				outpak.WriteShort(contenthead);
				outpak.WriteShort(0); // No Fall Speed.
			}

			// Write Flags
			byte flagcontent = 0;
			if (client.Player.IsWireframe)
				flagcontent |= 0x01;
			if (client.Player.IsStealthed)
				flagcontent |= 0x02;
			if (client.Player.IsDiving)
				flagcontent |= 0x04;
			if (client.Player.IsTorchLighted)
				flagcontent |= 0x80;
			outpak.WriteByte(flagcontent);

			// Write health + Attack
			outpak.WriteByte((byte)(client.Player.HealthPercent + (client.Player.attackComponent.AttackState ? 0x80 : 0)));

			// Write Remainings.
			outpak.WriteByte(client.Player.ManaPercent);
			outpak.WriteByte(client.Player.EndurancePercent);
			var outpakArr = outpak.ToArray();

			var outpak190 = new GSUDPPacketOut(client.Out.GetPacketCode(eServerPackets.PlayerPosition));
			outpak190.Write(outpakArr, 5, outpakArr.Length - 5);
			outpak190.FillString(client.Player.CharacterClass.Name, 32);
			outpak190.WriteByte((byte)(client.Player.RPFlag ? 1 : 0)); // roleplay flag, if == 1, show name (RP) with gray color
			outpak190.WriteByte(0); // send last byte for 190+ packets
			outpak190.WritePacketLength();

			GSUDPPacketOut outpak1112 = new GSUDPPacketOut(client.Out.GetPacketCode(eServerPackets.PlayerPosition));
			outpak1112.Write(outpakArr, 5, outpakArr.Length - 5);
			outpak1112.WriteByte((byte)(client.Player.RPFlag ? 1 : 0));
			outpak1112.WriteByte(0); //outpak1112.WriteByte((con168.Length == 22) ? con168[21] : (byte)0);
			outpak1112.WritePacketLength();

			GSUDPPacketOut outpak1124 = new GSUDPPacketOut(client.Out.GetPacketCode(eServerPackets.PlayerPosition));
			byte playerAction = 0x00;
			if (client.Player.IsDiving)
				playerAction |= 0x04;
			if (client.Player.TargetInView)
				playerAction |= 0x30;
			if (client.Player.GroundTargetInView)
				playerAction |= 0x08;
			if (client.Player.IsTorchLighted)
				playerAction |= 0x80;
			if (client.Player.IsStealthed)
				playerAction |= 0x02;
			ushort playerState = 0;
			outpak1124.WriteFloatLowEndian(client.Player.X);
			outpak1124.WriteFloatLowEndian(client.Player.Y);
			outpak1124.WriteFloatLowEndian(client.Player.Z);
			outpak1124.WriteFloatLowEndian(client.Player.CurrentSpeed);
			outpak1124.WriteFloatLowEndian(fallSpeed);
			outpak1124.WriteShort((ushort)client.SessionID);
			outpak1124.WriteShort(currentZoneID);
			outpak1124.WriteShort(playerState);
			outpak1124.WriteShort((ushort)(client.Player.Steed?.RiderSlot(client.Player) ?? 0)); // fall damage flag coming in, steed seat position going out
			outpak1124.WriteShort(client.Player.Heading);
			outpak1124.WriteByte(playerAction);
			outpak1124.WriteByte((byte)(client.Player.RPFlag ? 1 : 0));
			outpak1124.WriteByte(0);
			outpak1124.WriteByte((byte)(client.Player.HealthPercent + (client.Player.attackComponent.AttackState ? 0x80 : 0)));
			outpak1124.WriteByte(client.Player.ManaPercent);
			outpak1124.WriteByte(client.Player.EndurancePercent);
			outpak1124.WritePacketLength();


			foreach (GamePlayer player in client.Player.GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
			{
				if (player == null || player == client.Player)
					continue;

				if ((client.Player.InHouse || player.InHouse) && player.CurrentHouse != client.Player.CurrentHouse)
					continue;

				if (client.Player.MinotaurRelic != null)
				{
					MinotaurRelic relic = client.Player.MinotaurRelic;
					if (!relic.Playerlist.Contains(player) && player != client.Player)
					{
						relic.Playerlist.Add(player);
						player.Out.SendMinotaurRelicWindow(client.Player, client.Player.MinotaurRelic.Effect, true);
					}
				}

				if (!client.Player.IsStealthed || player.CanDetect(client.Player))
				{
					//forward the position packet like normal!
					if (player.Client.Version >= GameClient.eClientVersion.Version1124)
						player.Out.SendUDP(outpak1124);
					else if (player.Client.Version >= GameClient.eClientVersion.Version1112)
						player.Out.SendUDP(outpak1112);
					else if (player.Client.Version >= GameClient.eClientVersion.Version190)
						player.Out.SendUDP(outpak190);
				}
				else
					player.Out.SendObjectDelete(client.Player); //remove the stealthed player from view
			}

			if (client.Player.CharacterClass.ID == (int)eCharacterClass.Warlock)
			{
				//Send Chamber effect
				client.Player.Out.SendWarlockChamberEffect(client.Player);
			}

			//handle closing of windows
			//trade window
			if (client.Player.TradeWindow != null)
			{
				if (client.Player.TradeWindow.Partner != null)
				{
					if (!client.Player.IsWithinRadius(client.Player.TradeWindow.Partner, WorldMgr.GIVE_ITEM_DISTANCE))
						client.Player.TradeWindow.CloseTrade();
				}
			}
		}

		private void _HandlePacket1124(GameClient client, GSPacketIn packet)
		{
			//Tiv: in very rare cases client send 0xA9 packet before sending S<=C 0xE8 player world initialize
			if ((client.Player.ObjectState != GameObject.eObjectState.Active) ||
				(client.ClientState != GameClient.eClientState.Playing))
				return;

			long environmentTick = GameLoop.GameLoopTime;
			int oldSpeed = client.Player.CurrentSpeed;

			var newPlayerX = packet.ReadFloatLowEndian();
			var newPlayerY = packet.ReadFloatLowEndian();
			var newPlayerZ = packet.ReadFloatLowEndian();
			var newPlayerSpeed = packet.ReadFloatLowEndian();
			var newPlayerZSpeed = packet.ReadFloatLowEndian();
			ushort sessionID = packet.ReadShort();
			if (client.Version >= GameClient.eClientVersion.Version1127)
				packet.ReadShort(); // object ID
			ushort currentZoneID = packet.ReadShort();
			ushort playerState = packet.ReadShort();
			ushort fallingDMG = packet.ReadShort();
			ushort newHeading = packet.ReadShort();
			byte playerAction = (byte)packet.ReadByte();
			packet.Skip(2); // unknown bytes x2
			byte playerHealth = (byte)packet.ReadByte();
			// two trailing bytes, no data + 2 more for 1.127+

			//int speed = (newPlayerSpeed & 0x1FF);
			//Flags1 = (eFlags1)playerState;
			//Flags2 = (eFlags2)playerAction;                        
			if ((client.Player.IsMezzed || client.Player.IsStunned) && client.Player.effectListComponent.GetAllEffects().FirstOrDefault(x => x.GetType() == typeof(SpeedOfSoundECSEffect)) == null)
				client.Player.CurrentSpeed = 0;
			else
            {
				if (client.Player.CurrentSpeed == 0 && (client.Player.LastPositionUpdatePoint.X != newPlayerX 
					|| client.Player.LastPositionUpdatePoint.Y != newPlayerY))
				{
					if(client.Player.IsSitting)
						client.Player.Sit(false);
					client.Player.CurrentSpeed = (short)newPlayerSpeed;
				}
				else
					client.Player.CurrentSpeed = (short)newPlayerSpeed;
			}
				
			/*
			client.Player.IsStrafing = Flags1 == eFlags1.STRAFELEFT || Flags1 == eFlags1.STRAFERIGHT;
			client.Player.IsDiving = Flags2 == eFlags2.DIVING ? true : false;
			client.Player.IsSwimming = Flags1 == eFlags1.SWIMMING ? true : false;
			if (client.Player.IsRiding)
				Flags1 = eFlags1.RIDING;
			client.Player.IsClimbing = Flags1 == eFlags1.CLIMBING ? true : false;
			if (!client.Player.IsAlive)
				Flags1 = eFlags1.DEAD;*/

			client.Player.IsJumping = ((playerAction & 0x40) != 0);
			client.Player.IsStrafing = ((playerState & 0xe000) != 0);

			Zone newZone = WorldMgr.GetZone(currentZoneID);
			if (newZone == null)
			{
				if (!client.Player.TempProperties.GetProperty("isbeingbanned", false))
				{
					log.Error(client.Player.Name + "'s position in unknown zone! => " + currentZoneID);
					GamePlayer player = client.Player;
					player.TempProperties.SetProperty("isbeingbanned", true);
					player.MoveToBind();
				}

				return; // TODO: what should we do? player lost in space
			}

			// move to bind if player fell through the floor
			if (newPlayerZ == 0)
			{
				client.Player.MoveToBind();
				return;
			}

			//int realX = newPlayerX;
			//int realY = newPlayerY;
			//int realZ = newPlayerZ;
			bool zoneChange = newZone != client.Player.LastPositionUpdateZone;
			if (zoneChange)
			{
				//If the region changes -> make sure we don't take any falling damage
				if (client.Player.LastPositionUpdateZone != null && newZone.ZoneRegion.ID != client.Player.LastPositionUpdateZone.ZoneRegion.ID)
				{
					client.Player.MaxLastZ = int.MinValue;
				}
				// Update water level and diving flag for the new zone
				//client.Out.SendPlayerPositionAndObjectID();

				/*
				 * "You have entered Burial Tomb."
				 * "Burial Tomb"
				 * "Current area is adjusted for one level 1 player."
				 * "Current area has a 50% instance bonus."
				 */

				string description = newZone.Description;
				string screenDescription = description;

				var translation = LanguageMgr.GetTranslation(client, newZone) as DbLanguageZone;
				if (translation != null)
				{
					if (!string.IsNullOrEmpty(translation.Description))
						description = translation.Description;

					if (!string.IsNullOrEmpty(translation.ScreenDescription))
						screenDescription = translation.ScreenDescription;
				}

				client.Out.SendMessage(LanguageMgr.GetTranslation(client.Account.Language, "PlayerPositionUpdateHandler.Entered", description),
									   eChatType.CT_System, eChatLoc.CL_SystemWindow);
				client.Out.SendMessage(screenDescription, eChatType.CT_ScreenCenterSmaller, eChatLoc.CL_SystemWindow);

				client.Player.LastPositionUpdateZone = newZone;

				if (client.Player.GMStealthed)
					client.Player.Stealth(true);
			}

			int coordsPerSec = 0;
			int jumpDetect = 0;
			long timediff = GameLoop.GameLoopTime - client.Player.LastPositionUpdateTick;
			int distance = 0;

			if (timediff > 0)
			{
				var newPoint = new Point3D(newPlayerX, newPlayerY, newPlayerZ);
				distance = newPoint.GetDistanceTo(new Point3D((int)client.Player.LastPositionUpdatePoint.X, (int)client.Player.LastPositionUpdatePoint.Y,
					(int)client.Player.LastPositionUpdatePoint.Z));
				coordsPerSec =distance * 1000 /(int)timediff;

				if (distance < 100 && client.Player.LastPositionUpdatePoint.Z > 0)
				{
					jumpDetect = (int)(newPlayerZ - client.Player.LastPositionUpdatePoint.Z);
				}
			}

			client.Player.LastPositionUpdateTick = GameLoop.GameLoopTime;
			client.Player.LastPositionUpdatePoint.X = newPlayerX;
			client.Player.LastPositionUpdatePoint.Y = newPlayerY;
			client.Player.LastPositionUpdatePoint.Z = newPlayerZ;
			client.Player.X = (int)newPlayerX;
			client.Player.Y = (int)newPlayerY;
			client.Player.Z = (int)newPlayerZ;

			int tolerance = ServerProperties.Properties.CPS_TOLERANCE;

			if (client.Player.Steed != null && client.Player.Steed.MaxSpeed > 0)
			{
				tolerance += client.Player.Steed.MaxSpeed;
			}
			else if (client.Player.MaxSpeed > 0)
			{
				tolerance += client.Player.MaxSpeed;
			}

			if (client.Player.IsJumping)
			{
				coordsPerSec = 0;
				jumpDetect = 0;
				client.Player.IsJumping = false;
			}

			if (!client.Player.IsAllowedToFly && (coordsPerSec > tolerance || jumpDetect > ServerProperties.Properties.JUMP_TOLERANCE))
			{
				bool isHackDetected = true;

				if (coordsPerSec > tolerance)
				{
					// check to see if CPS time tolerance is exceeded
					int lastCPSTick = client.Player.TempProperties.GetProperty<int>(LASTCPSTICK, 0);

					if (environmentTick - lastCPSTick > ServerProperties.Properties.CPS_TIME_TOLERANCE)
					{
						isHackDetected = false;
					}
				}

				if (isHackDetected)
				{
					StringBuilder builder = new StringBuilder();
					builder.Append("MOVEHACK_DETECT");
					builder.Append(": CharName=");
					builder.Append(client.Player.Name);
					builder.Append(" Account=");
					builder.Append(client.Account.Name);
					builder.Append(" IP=");
					builder.Append(client.TcpEndpointAddress);
					builder.Append(" CPS:=");
					builder.Append(coordsPerSec);
					builder.Append(" JT=");
					builder.Append(jumpDetect);
					ChatUtil.SendDebugMessage(client, builder.ToString());

					if (client.Account.PrivLevel == 1)
					{
						GameServer.Instance.LogCheatAction(builder.ToString());

						if (ServerProperties.Properties.ENABLE_MOVEDETECT)
						{
							if (ServerProperties.Properties.BAN_HACKERS && false) // banning disabled until this technique is proven accurate
							{
								DbBans b = new DbBans();
								b.Author = "SERVER";
								b.Ip = client.TcpEndpointAddress;
								b.Account = client.Account.Name;
								b.DateBan = DateTime.Now;
								b.Type = "B";
								b.Reason = string.Format("Autoban MOVEHACK:(CPS:{0}, JT:{1}) on player:{2}", coordsPerSec, jumpDetect, client.Player.Name);
								GameServer.Database.AddObject(b);
								GameServer.Database.SaveObject(b);

								string message = "";

								message = "You have been auto kicked and banned due to movement hack detection!";
								for (int i = 0; i < 8; i++)
								{
									client.Out.SendMessage(message, eChatType.CT_Help, eChatLoc.CL_SystemWindow);
									client.Out.SendMessage(message, eChatType.CT_Help, eChatLoc.CL_ChatWindow);
								}

								client.Out.SendPlayerQuit(true);
								client.Player.SaveIntoDatabase();
								client.Player.Quit(true);
							}
							else
							{
								string message = "";

								message = "You have been auto kicked due to movement hack detection!";
								for (int i = 0; i < 8; i++)
								{
									client.Out.SendMessage(message, eChatType.CT_Help, eChatLoc.CL_SystemWindow);
									client.Out.SendMessage(message, eChatType.CT_Help, eChatLoc.CL_ChatWindow);
								}

								client.Out.SendPlayerQuit(true);
								client.Player.SaveIntoDatabase();
								client.Player.Quit(true);
							}
							client.Disconnect();
							return;
						}
					}
				}

				client.Player.TempProperties.SetProperty(LASTCPSTICK, environmentTick);
			}
			//client.Player.Heading = (ushort)(newHeading & 0xFFF); //patch 0024 expermental

			if (client.Player.X != newPlayerX || client.Player.Y != newPlayerY)
			{
				client.Player.TempProperties.SetProperty(LASTMOVEMENTTICK, client.Player.CurrentRegion.Time);
			}

			client.Player.X = (int)newPlayerX;
			client.Player.Y = (int)newPlayerY;
			client.Player.Z = (int)newPlayerZ;
			client.Player.Heading = (ushort)(newHeading & 0xFFF);

			// used to predict current position, should be before
			// any calculation (like fall damage)
			client.Player.MovementStartTick = GameLoop.GameLoopTime; // experimental 0024

			// Begin ---------- New Area System -----------
			if (client.Player.CurrentRegion.Time > client.Player.AreaUpdateTick) // check if update is needed
			{
				var oldAreas = client.Player.CurrentAreas;

				// Because we may be in an instance we need to do the area check from the current region
				// rather than relying on the zone which is in the skinned region.  - Tolakram

				var newAreas = client.Player.CurrentRegion.GetAreasOfZone(newZone, client.Player);

				// Check for left areas
				if (oldAreas != null)
				{
					foreach (IArea area in oldAreas)
					{
						if (!newAreas.Contains(area))
						{
							area.OnPlayerLeave(client.Player);

							//Check if leaving Border Keep areas so we can check RealmTimer
							AbstractArea checkrvrarea = area as AbstractArea;
							if (checkrvrarea != null && (checkrvrarea.Description.Equals("Castle Sauvage") || 
								checkrvrarea.Description.Equals("Snowdonia Fortress") || 
								checkrvrarea.Description.Equals("Svasud Faste") ||
								checkrvrarea.Description.Equals("Vindsaul Faste") ||
								checkrvrarea.Description.Equals("Druim Ligen") ||
								checkrvrarea.Description.Equals("Druim Cain")))
							{
								RealmTimer.CheckRealmTimer(client.Player);
							}
						}
					}
				}
				// Check for entered areas
				foreach (IArea area in newAreas)
				{
					if (oldAreas == null || !oldAreas.Contains(area))
					{
						area.OnPlayerEnter(client.Player);
					}
				}
				// set current areas to new one...
				client.Player.CurrentAreas = newAreas;
				client.Player.AreaUpdateTick = client.Player.CurrentRegion.Time + 2000; // update every 2 seconds
			}
			// End ---------- New Area System -----------


			//client.Player.TargetInView = ((flags & 0x10) != 0);
			//client.Player.IsDiving = ((playerAction & 0x02) != 0);
			client.Player.TargetInView = ((playerAction & 0x30) != 0);
			client.Player.GroundTargetInView = ((playerAction & 0x08) != 0);
			client.Player.IsTorchLighted = ((playerAction & 0x80) != 0);
			// patch 0069 player diving is 0x02, but will broadcast to other players as 0x04
			// if player has a pet summoned, player action is sent by client as 0x04, but sending to other players this is skipped
			client.Player.IsDiving = ((playerAction & 0x02) != 0);

			int state = ((playerState >> 10) & 7);
			client.Player.IsClimbing = (state == 7);
			client.Player.IsSwimming = (state == 1);

			//int status = (data & 0x1FF ^ data) >> 8;
			//int fly = (flyingflag & 0x1FF ^ flyingflag) >> 8;
			if (state == 3 && client.Player.TempProperties.GetProperty<bool>(GamePlayer.DEBUG_MODE_PROPERTY, false) == false && !client.Player.IsAllowedToFly) //debugFly on, but player not do /debug on (hack)
			{
				StringBuilder builder = new StringBuilder();
				builder.Append("HACK_FLY");
				builder.Append(": CharName=");
				builder.Append(client.Player.Name);
				builder.Append(" Account=");
				builder.Append(client.Account.Name);
				builder.Append(" IP=");
				builder.Append(client.TcpEndpointAddress);
				GameServer.Instance.LogCheatAction(builder.ToString());
				{
					if (ServerProperties.Properties.BAN_HACKERS)
					{
						DbBans b = new DbBans();
						b.Author = "SERVER";
						b.Ip = client.TcpEndpointAddress;
						b.Account = client.Account.Name;
						b.DateBan = DateTime.Now;
						b.Type = "B";
						b.Reason = string.Format("Autoban flying hack: on player:{0}", client.Player.Name);
						GameServer.Database.AddObject(b);
						GameServer.Database.SaveObject(b);
					}
					string message = "";

					message = "Client Hack Detected!";
					for (int i = 0; i < 6; i++)
					{
						client.Out.SendMessage(message, eChatType.CT_System, eChatLoc.CL_SystemWindow);
						client.Out.SendMessage(message, eChatType.CT_System, eChatLoc.CL_ChatWindow);
					}
					client.Out.SendPlayerQuit(true);
					client.Disconnect();
					return;
				}
			}
			lock (client.Player.LastUniqueLocations)
			{
				GameLocation[] locations = client.Player.LastUniqueLocations;
				GameLocation loc = locations[0];
				if (loc.X != (int)newPlayerX || loc.Y != (int)newPlayerY || loc.Z != (int)newPlayerZ || loc.RegionID != client.Player.CurrentRegionID)
				{
					loc = locations[locations.Length - 1];
					Array.Copy(locations, 0, locations, 1, locations.Length - 1);
					locations[0] = loc;
					loc.X = (int)newPlayerX;
					loc.Y = (int)newPlayerY;
					loc.Z = (int)newPlayerZ;
					loc.Heading = client.Player.Heading;
					loc.RegionID = client.Player.CurrentRegionID;
				}
			}

			//FALLING DAMAGE

			if (GameServer.ServerRules.CanTakeFallDamage(client.Player) && !client.Player.IsSwimming)
			{
				try
				{
					int maxLastZ = client.Player.MaxLastZ;

					// Are we on the ground?
					if ((fallingDMG >> 15) != 0)
					{
						int safeFallLevel = client.Player.GetAbilityLevel(Abilities.SafeFall);

						var fallSpeed = (newPlayerZSpeed * -1) - (100 * safeFallLevel);

						int fallDivide = 15;

						var fallPercent = (int)Math.Min(99, (fallSpeed - 501) / fallDivide);

						if (fallSpeed > 500)
						{
							if (client.Player.CharacterClass.ID != (int)eCharacterClass.Necromancer || !client.Player.IsShade)
							{
								client.Player.CalcFallDamage(fallPercent);
							}
						}

						client.Player.MaxLastZ = client.Player.Z;
					}
					else if (maxLastZ < client.Player.Z || client.Player.IsRiding || newPlayerZSpeed > -150) // is riding, for dragonflys
						client.Player.MaxLastZ = client.Player.Z;
				}
				catch
				{
					log.Warn("error when attempting to calculate fall damage");
				}
			}

			ushort steedSeatPosition = 0;

			if (client.Player.Steed != null && client.Player.Steed.ObjectState == GameObject.eObjectState.Active)
			{
				client.Player.Heading = client.Player.Steed.Heading;
				newHeading = (ushort)client.Player.Steed.ObjectID;
				steedSeatPosition = (ushort)client.Player.Steed.RiderSlot(client.Player);
			}
			else if ((playerState >> 10) == 4) // patch 0062 fix bug on release preventing players from receiving res sickness
				client.Player.IsSitting = true;

			GSUDPPacketOut outpak1124 = new GSUDPPacketOut(client.Out.GetPacketCode(eServerPackets.PlayerPosition));
			//patch 0069 test to fix player swim out byte flag
			byte playerOutAction = 0x00;
			if (client.Player.IsDiving)
				playerOutAction |= 0x04;
			if (client.Player.TargetInView)
				playerOutAction |= 0x30;
			if (client.Player.GroundTargetInView)
				playerOutAction |= 0x08;
			if (client.Player.IsTorchLighted)
				playerOutAction |= 0x80;
			if (client.Player.IsStealthed)
				playerOutAction |= 0x02;

			outpak1124.WriteFloatLowEndian(newPlayerX);
			outpak1124.WriteFloatLowEndian(newPlayerY);
			outpak1124.WriteFloatLowEndian(newPlayerZ);
			outpak1124.WriteFloatLowEndian(newPlayerSpeed);
			outpak1124.WriteFloatLowEndian(newPlayerZSpeed);
			outpak1124.WriteShort(sessionID);
			outpak1124.WriteShort(currentZoneID);
			outpak1124.WriteShort(playerState);
			outpak1124.WriteShort(steedSeatPosition); // fall damage flag coming in, steed seat position going out
			outpak1124.WriteShort(newHeading);
			outpak1124.WriteByte(playerOutAction);
			outpak1124.WriteByte((byte)(client.Player.RPFlag ? 1 : 0));
			outpak1124.WriteByte(0);
			outpak1124.WriteByte((byte)(client.Player.HealthPercent + (client.Player.attackComponent.AttackState ? 0x80 : 0)));
			outpak1124.WriteByte(client.Player.ManaPercent);
			outpak1124.WriteByte(client.Player.EndurancePercent);
			outpak1124.WritePacketLength();

			var outpak1127 = new GSUDPPacketOut(client.Out.GetPacketCode(eServerPackets.PlayerPosition));
			outpak1127.Write(outpak1124.GetBuffer(), 5, 22); // from position X to sessionID
			outpak1127.WriteShort((ushort)client.Player.ObjectID);
			outpak1127.WriteShort(currentZoneID);
			outpak1127.WriteShort(playerState);
			outpak1127.WriteShort(steedSeatPosition); // fall damage flag coming in, steed seat position going out
			outpak1127.WriteShort(newHeading);
			outpak1127.WriteByte(playerOutAction);
			outpak1127.WriteByte((byte)(client.Player.RPFlag ? 1 : 0));
			outpak1127.WriteByte(0);
			outpak1127.WriteByte((byte)(client.Player.HealthPercent + (client.Player.attackComponent.AttackState ? 0x80 : 0)));
			outpak1127.WriteByte(client.Player.ManaPercent);
			outpak1127.WriteByte(client.Player.EndurancePercent);
			outpak1127.WriteShort(0);
			outpak1127.WritePacketLength();

			var outpak190 = new GSUDPPacketOut(client.Out.GetPacketCode(eServerPackets.PlayerPosition));
			outpak190.WriteShort((ushort)client.SessionID);
			outpak190.WriteShort((ushort)(client.Player.CurrentSpeed & 0x1FF));
			outpak190.WriteShort((ushort)newPlayerZ);
			var xoff = (ushort)(newPlayerX - (client.Player.CurrentZone?.XOffset ?? 0));
			outpak190.WriteShort(xoff);
			var yoff = (ushort)(newPlayerY - (client.Player.CurrentZone?.YOffset ?? 0));
			outpak190.WriteShort(yoff);
			outpak190.WriteShort(currentZoneID);
			outpak190.WriteShort(newHeading);
			outpak190.WriteShort(steedSeatPosition);
			outpak190.WriteByte(playerAction);
			outpak190.WriteByte((byte)(client.Player.HealthPercent + (client.Player.attackComponent.AttackState ? 0x80 : 0)));
			outpak190.WriteByte(client.Player.ManaPercent);
			outpak190.WriteByte(client.Player.EndurancePercent);

			var outpak1112 = new GSUDPPacketOut(client.Out.GetPacketCode(eServerPackets.PlayerPosition));
			outpak1112.Write(outpak190.GetBuffer(), 5, (int)outpak190.Length - 5);
			outpak1112.WriteByte((byte)(client.Player.RPFlag ? 1 : 0));
			outpak1112.WriteByte(0);
			outpak1112.WritePacketLength();

			outpak190.FillString(client.Player.CharacterClass.Name, 32);
			outpak190.WriteByte((byte)(client.Player.RPFlag ? 1 : 0));
			outpak190.WriteByte(0);
			outpak190.WritePacketLength();

			foreach (GamePlayer player in client.Player.GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
			{
				if (player == null || player == client.Player)
					continue;

				if ((client.Player.InHouse || player.InHouse) && player.CurrentHouse != client.Player.CurrentHouse)
					continue;

				if (!client.Player.IsStealthed || player.CanDetect(client.Player))
				{
					if (player.Client.Version >= GameClient.eClientVersion.Version1127)
						player.Out.SendUDP(outpak1127);
					else if (player.Client.Version >= GameClient.eClientVersion.Version1124)
						player.Out.SendUDP(outpak1124);
					else if (player.Client.Version >= GameClient.eClientVersion.Version1112)
						player.Out.SendUDP(outpak1112);
					else
						player.Out.SendUDP(outpak190);
				}
				else
					player.Out.SendObjectDelete(client.Player); //remove the stealthed player from view
			}

			//handle closing of windows
			//trade window
			if (client.Player.TradeWindow?.Partner != null && !client.Player.IsWithinRadius(client.Player.TradeWindow.Partner, WorldMgr.GIVE_ITEM_DISTANCE))
				client.Player.TradeWindow.CloseTrade();
		}
	}
}
