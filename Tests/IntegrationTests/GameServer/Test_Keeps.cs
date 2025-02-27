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
using NUnit.Framework;

using DOL.Database;
using DOL.GS;
using DOL.GS.Keeps;
using DOL.Tests.Unit.Gameserver;

namespace DOL.Tests.Integration.Keeps
{
    [TestFixture]
    class Test_Keeps
    {
        [OneTimeSetUp]
        public void SetupFakeServer()
        {
            var sqliteDB = Create.TemporarySQLiteDB();
            sqliteDB.RegisterDataObject(typeof(DbKeepComponent));
            sqliteDB.RegisterDataObject(typeof(DbKeepPosition));
            sqliteDB.RegisterDataObject(typeof(DbKeep));
            sqliteDB.RegisterDataObject(typeof(DbNpcEquipment));
            sqliteDB.RegisterDataObject(typeof(DbBattleground));

            var fakeServer = new FakeServer();
            fakeServer.SetDatabase(sqliteDB);
            GameServer.LoadTestDouble(fakeServer);
            GameNpcInventoryTemplate.Init();

            AddKeepPositions();
        }

        [Test]
        [Category("Unreliable")]
        public void ComponentFillPosition_TwoIdenticalComponentsWithAGuardEachOnSameKeep_KeepHas2Guards()
        {
            var keep = CreateKeep();
            var keepComponent = CreateKeepWall();
            keepComponent.ID = 1;
            keepComponent.Keep = keep;
            keepComponent.LoadPositions();
            keepComponent.FillPositions();
            var keepComponent2 = CreateKeepWall();
            keepComponent2.ID = 2;
            keepComponent2.Keep = keep;
            keepComponent2.LoadPositions();
            keepComponent2.FillPositions();
            Assert.AreEqual(2, keepComponent.Keep.Guards.Count);
        }

        [Test]
        [Category("Unreliable")]
        public void ComponentFillPosition_TwoIdenticalComponentsWithAGuardOnEachHeightOnSameKeepWithLevel2_KeepHas4Guards()
        {
            var keep = CreateKeep();
            keep.Level = 2; //Height = 1
            var keepComponent = CreateKeepWall();
            keepComponent.ID = 1;
            keepComponent.Keep = keep;
            keepComponent.LoadPositions();
            keepComponent.FillPositions();
            var keepComponent2 = CreateKeepWall();
            keepComponent2.ID = 2;
            keepComponent2.Keep = keep;
            keepComponent2.LoadPositions();
            keepComponent2.FillPositions();
            Assert.AreEqual(4, keepComponent.Keep.Guards.Count);
        }

        private void AddKeepPositions()
        {
            var keepPosition = GuardPositionAtKeepWallTemplate;
            keepPosition.TemplateID = "posA";
            keepPosition.Height = 0;
            AddDatabaseEntry(keepPosition);
            keepPosition = GuardPositionAtKeepWallTemplate;
            keepPosition.TemplateID = "posB";
            keepPosition.Height = 1;
            AddDatabaseEntry(keepPosition);
        }

        private string GuardFighter => "DOL.GS.Keeps.GuardFighter";
        private DbKeepPosition GuardPositionAtKeepWallTemplate
        {
            get
            {
                var keepPosition = new DbKeepPosition();
                keepPosition.ComponentRotation = 0;
                keepPosition.ComponentSkin = 1;
                keepPosition.Height = 0;
                keepPosition.ClassType = GuardFighter;
                return keepPosition;
            }
        }

        private GameKeepComponent CreateKeepWall()
        {
            var dbKeep = new DbKeep();
            var keep = new GameKeep();
            keep.DBKeep = dbKeep;
            var keepComponent = new GameKeepComponent();
            keepComponent.Skin = GuardPositionAtKeepWallTemplate.ComponentSkin;
            keepComponent.ComponentHeading = GuardPositionAtKeepWallTemplate.ComponentRotation;
            return keepComponent;
        }
        private GameKeep CreateKeep() => new GameKeep() { DBKeep = new DbKeep() };


        private static void AddDatabaseEntry(DataObject dataObject)
        {
            dataObject.AllowAdd = true;
            GameServer.Database.AddObject(dataObject);
        }
    }
}
